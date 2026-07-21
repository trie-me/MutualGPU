# MutualGPU TypeScript SDK

The workspace includes a cookie-backed browser requestor client and one shared provider lifecycle:

- `@mutualgpu/requestor-web` exposes `RequestorClient` for capability discovery, task submission and polling, result descriptors, and browser-provider key issuance. Before its first API operation it performs one credentialed `GET /` to establish the requestor cookie. Every later request uses `credentials: "include"`, and one missing-identity response is retried defensively.
- `@mutualgpu/provider-core` exposes `ProviderClient`, result upload, and the canonical `provider.proto` wire codec.
- `@mutualgpu/provider-node` uses native Node HTTPS/HTTP2 for the bidirectional gRPC session.
- `@mutualgpu/provider-web` uses one binary Protobuf message per Chrome WebSocket frame and the HTTP enrollment mapping.

All endpoints are required to use TLS: Node/API URLs use `https://` and Chrome sessions use `wss://`. The SDK rejects cleartext URLs before it sends provider credentials.

## Browser requestor

```js
import { RequestorClient } from "@mutualgpu/requestor-web";

const requestor = new RequestorClient("https://api.example/");
const capabilities = await requestor.listCapabilities();
const capability = capabilities[0];
const machine = capability.machineAvailability[0];

const task = await requestor.submitTask({
  capabilityId: capability.capabilityId,
  contractHash: capability.contractHash,
  scalars: { enable_safety_checker: "false" },
  resources: {
    computeTier: machine.computeTier,
    memoryGiB: machine.memoryGiB
  },
  idempotencyKey: crypto.randomUUID()
}, imageFile);

const current = await requestor.getTask(task.taskId);
const result = current.canRetrieveResult
  ? await requestor.getTaskResult(task.taskId)
  : null;
```

Cross-origin hosts must be present in `MutualGPU:ProviderCorsOrigins`. The API issues the requestor cookie as `HttpOnly; Secure; SameSite=None; Partitioned`; application code never reads or forwards the cookie itself. Partitioning keeps the anonymous session usable when ordinary third-party cookies are blocked while preventing it from being shared across unrelated top-level sites.

After deploying the API build that accompanies an SDK release, run the required hosted release gate from an allowed browser origin:

```text
MUTUALGPU_API_URL=https://mutualgpu.com \
MUTUALGPU_CORS_TEST_ORIGIN=https://yosun-triposplat-webgpu-demo.static.hf.space \
npm run test:release-gate
```

The gate runs the complete SDK suite, loads the packaged `RequestorClient` in Chromium, verifies root bootstrap plus repeated protected calls, and repeats the session check with Chromium third-party-cookie blocking enabled.

## Node provider

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const transport = new NodeGrpcTransport(
  "https://api.example",
  process.env.MUTUALGPU_PROVIDER_KEY);
const provider = new ProviderClient(transport);

await provider.enroll(definition);
await provider.connect(async task => {
  // Assignment delivery alone does not make the task running.
  await task.accept();

  const inputUrl = task.input?.url ?? await task.refreshInputDownload();
  const result = await runInstalledHandler({ inputUrl, scalars: task.scalars });
  const { receipt } = await task.uploadResult({ resultZip: result.zip, metadata: result.metadata });
  await task.complete(receipt);
});
```

## Chrome provider

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { BrowserWebSocketTransport } from "@mutualgpu/provider-web";

const transport = new BrowserWebSocketTransport(
  "https://api.example/",
  providerKey);
const provider = new ProviderClient(transport);

await provider.enroll(definition);
await provider.connect(async task => {
  await task.accept();
  // Run WebGPU work, then upload and complete as in the Node example.
});
```

`ProviderClient` permits one active task only. A handler must call `accept()` or `reject(reason)` before returning. After acceptance, progress is coalesced to one update per second, and `complete(receipt)` waits for the server's completion confirmation. An unexpected transport close starts bounded reconnect attempts and sends the active task handle in the next `ConnectRequest`; it can rebind only while the host's grace period remains valid. `close()` stops that recovery loop.

Run the conformance and adapter tests with:

```text
npm test --prefix sdk/typescript
```

## Local API gRPC smoke test

`npm run smoke:node` is deliberately not a GPU worker. It is a small SDK consumer of the real MutualGPU API gRPC service: it invokes `Enroll`, opens the bidirectional `Connect` RPC, waits for the server's `Connected` response, and closes cleanly. It submits no task and performs no GPU work.

It needs one configured provider identity on a locally running API host. No public endpoint, tunnel, or GPU workload is required:

```text
MUTUALGPU_API_URL=https://localhost:7043 \
MUTUALGPU_PROVIDER_KEY=your-provider-key \
MUTUALGPU_EXECUTION_UNIT_ID=your-configured-provider-uuid \
npm run smoke:node
```

On Node versions that do not use the platform trust store by default, prepend `NODE_OPTIONS=--use-system-ca` after trusting the local .NET development certificate. A zero exit status proves that the Node transport interoperates with the actual API's gRPC service for both unary enrollment and the streaming connection handshake.

The automated `npm test` suite covers the remaining SDK control-message and result-upload paths with canonical protocol fixtures. Use a public HTTPS endpoint only for a later test where the SDK process is genuinely on another network.

## Real-browser HTTPS and WSS integration test

`npm run test:browser-integration` launches Chromium, bundles the checked-in browser SDK into the page at `MUTUALGPU_API_URL`, then uses `BrowserWebSocketTransport` and `ProviderClient` in that browser page. It performs direct provider enrollment with its configured test key and waits for the real WSS `Connected` handshake. It deliberately does not mint a key or invoke the password-gated WebGPU enrollment UI.

```text
MUTUALGPU_API_URL=https://mutualgpu.com \
MUTUALGPU_PROVIDER_KEY=your-provider-key \
MUTUALGPU_BROWSER_EXECUTABLE="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
npm run test:browser-integration
```

For the protected demo key file, run this repeatable wrapper from `examples/MutualGPU`:

```text
just browser-sdk-integration-file /private/tmp/mutualgpu-provider-keys.v2DiHW 1
```

The test needs Chromium or Chrome but does not claim to execute a GPU workload. Browser WebGPU workload acceptance is a separate test because the SDK transport does not itself require `navigator.gpu`.

## Full local demo

The API host and `npm run demo:node` make a complete local requestor demo. The Node process is a simulated provider, not a GPU worker: it executes the normal enrollment, gRPC session, acceptance, result upload, and completion path, then publishes a clearly marked synthetic ZIP result.

In one terminal, configure one local provider identity and start the API:

```text
export MutualGPU__Providers__0__ExecutionUnitId=00000000-0000-0000-0000-000000000001
export MutualGPU__Providers__0__PresharedKey=local-demo-key
dotnet dev-certs https --trust
dotnet run --project src/MutualGPU.Api --urls https://localhost:7043
```

In a second terminal, start the simulated provider. On Node versions that do not use the platform trust store by default, retain `NODE_OPTIONS=--use-system-ca`:

```text
NODE_OPTIONS=--use-system-ca \
MUTUALGPU_API_URL=https://localhost:7043 \
MUTUALGPU_PROVIDER_KEY=local-demo-key \
MUTUALGPU_EXECUTION_UNIT_ID=00000000-0000-0000-0000-000000000001 \
npm run demo:node
```

Open `https://localhost:7043`, select **mutualgpu-local-demo**, and submit a task. The requestor task list should show progress and completion, then offer the synthetic `result` ZIP and `metadata` descriptors. Stop the provider with `Ctrl-C` when finished.
