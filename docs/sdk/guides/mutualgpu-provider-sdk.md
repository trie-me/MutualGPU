# MutualGPU provider SDK usage guide

The MutualGPU provider SDK lets an application advertise the work it can perform and handle tasks from the exchange. Provider code describes capabilities, decides whether to accept a task, executes the workload, and produces a result. The SDK owns the API protocol around those decisions.

The current SDK supports Node.js and Chrome providers. These are the only supported consumer environments described by this documentation.

## What the SDK handles

Provider code does not construct MutualGPU API requests. The SDK handles:

- provider authentication;
- enrollment serialization and server-owned capability identity;
- connection setup and protocol negotiation;
- assignment acknowledgement and task authorization;
- progress throttling;
- input URL refresh;
- result authorization, checksums, upload, and completion;
- reconnect and active-task rebinding.

Provider code is responsible for:

- describing the machine and capabilities it offers;
- accepting or rejecting each assignment;
- validating and executing requestor-supplied inputs safely;
- producing a valid ZIP and any declared optional outputs;
- reporting execution failure when appropriate;
- closing the client during graceful shutdown.

## Packages

Use `@mutualgpu/provider-core` with the transport for the provider environment. Core and transport packages should use the same SDK version.

| Package | Purpose |
| --- | --- |
| `@mutualgpu/provider-core` | `ProviderClient` and the task lifecycle |
| `@mutualgpu/provider-node` | Node.js transport |
| `@mutualgpu/provider-web` | Chrome transport |

The current packages are repository-local preview packages rather than a documented public-registry release. From a source checkout, install the workspace and run its tests with:

```text
npm install --prefix sdk/typescript
npm test --prefix sdk/typescript
```

The packages are ECMAScript modules. Node consumers must run in an ESM project or use `.mjs` entry points. The Node transport also requires the standard `fetch`, `Blob`, `FormData`, and Web Crypto APIs used by result upload. A public release must state its supported Node and Chrome version range before consumers should depend on one.

The preview currently ships JavaScript without TypeScript declaration files. The object shapes in this guide and the [consumer API reference](../reference/provider-api.md) are therefore the public consumer contract for the demo.

## Configuration

An SDK consumer supplies two values:

- the MutualGPU HTTPS base URL;
- the provider preshared key issued by the MutualGPU operator.

The SDK derives the required service endpoints from the base URL. It rejects cleartext URLs before sending credentials.

The provider key identifies one configured execution unit. It is not a requestor credential or a storage credential. Keep it out of source control, generally accessible browser bundles, and logs.

## Provider lifecycle

A provider has one lifecycle:

1. Construct a transport and `ProviderClient`.
2. Enroll the machine and its capabilities.
3. Connect a task handler.
4. Accept or reject each assignment.
5. After acceptance, execute work and optionally report progress.
6. Upload the result and complete with the returned receipt, or fail the task.
7. Close the provider during shutdown.

`ProviderClient` permits one active task. Use one client per execution unit instead of adding application-side concurrency around a single client.

## Describe the provider

Enrollment describes the machine and the complete set of capabilities currently offered by that execution unit:

```js
const definition = {
  machine: {
    tier: "Large",
    specifications: {
      computeTier: "Large",
      memoryGiB: 32
    }
  },
  capabilities: [{
    name: "example-renderer",
    description: "Produces a rendered asset bundle.",
    inputs: [{
      key: "quality",
      type: "Integer",
      required: false,
      label: "Quality",
      default: "80",
      minimum: 1,
      maximum: 100,
      displayOrder: 0
    }],
    output: {
      hasMetadata: true,
      hasLogs: true
    }
  }]
};
```

Capability identity and continuity fields are server-owned. The SDK supplies them; consumers should not create capability IDs or contract hashes.

`machine.tier` is provider classification metadata. Its concrete values are `Small`, `Medium`, `Large`, and `ExtraLarge`; `Automatic` is only a requestor choice and is invalid during enrollment.

`machine.specifications` describes the hardware capacity exposed to requestors and used by scheduling. `computeTier` supplies the minimum CPU/GPU class and `memoryGiB` supplies the minimum memory capacity. A provider can satisfy work requesting an equal or smaller specification. These fields are separate from `machine.tier`, which remains provider classification metadata.

The accepted memory ranges are:

| `computeTier` | Valid `memoryGiB` |
| --- | --- |
| `Small` | 8 through 16 |
| `Medium` | 8 through 24 |
| `Large` | 8 through 48 |
| `ExtraLarge` | 8 through 128 |

Capability names must be unique within an enrollment, and input keys must be unique within a capability. Enrollment is a complete replacement of the execution unit's advertised definition: omitting a previously enrolled capability removes it from future scheduling.

### Inputs

Each input has a stable `key` used in `task.scalars`. Supported types are:

```text
String, Integer, Number, Boolean, Date, DateTime, DateTimeOffset, Image
```

An enrollment can declare at most one image input. Only an image input can declare `contentTypes`, and only a string input can declare `allowedValues`. Scalar definitions can declare `default`, `minimum`, and `maximum` where appropriate. See the [enrollment schema reference](../reference/enrollment-schema.md) for every field and validation rule.

### Outputs

Every successful task produces a ZIP. A capability can additionally declare:

- metadata;
- a thumbnail;
- a preview and its accepted content types;
- UTF-8 logs.

Only upload optional parts declared by the capability.

The exact output properties are `hasThumbnail`, `hasPreview`, `hasMetadata`, `hasLogs`, `previewContentTypes`, and `metadataSchema`. `previewContentTypes` is required in practice when a preview is enabled because the server accepts a preview only when its uploaded MIME type is declared. The demo stores `metadataSchema` as contract metadata but does not perform complete JSON Schema evaluation.

## Node.js provider

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiUrl = new URL(process.env.MUTUALGPU_API_URL);
const providerKey = process.env.MUTUALGPU_PROVIDER_KEY;

const provider = new ProviderClient(
  new NodeGrpcTransport(apiUrl, providerKey)
);

await provider.enroll(definition);

await provider.connect(async task => {
  if (!canRun(task)) {
    await task.reject("Required local runtime is unavailable");
    return;
  }

  await task.accept();

  await task.reportProgress({
    phase: "render",
    percent: 10,
    message: "Starting renderer"
  });

  const input = task.input ? await downloadInput(task.input) : undefined;
  const resultZip = await render({ scalars: task.scalars, input });

  const { receipt } = await task.uploadResult({
    resultZip,
    metadata: { renderer: "example-renderer" },
    logs: "Render completed successfully.\n"
  });

  await task.complete(receipt);
});
```

The provider session remains active after `connect` completes its handshake. The underlying connection keeps the Node.js process available for assignments.

For production shutdown, arrange cooperative cancellation for the workload separately. `provider.close()` closes the SDK transport and stops reconnection; it does not abort application code already running inside the handler.

## Chrome provider

Chrome uses the same lifecycle with its browser transport:

```js
import { ProviderClient } from "@mutualgpu/provider-core";
import { BrowserWebSocketTransport } from "@mutualgpu/provider-web";

const provider = new ProviderClient(
  new BrowserWebSocketTransport("https://api.example/", providerKey)
);

await provider.enroll(definition);
await provider.connect(async task => {
  await task.accept();
  const resultZip = await runWebGpuWork(task.scalars);
  const { receipt } = await task.uploadResult({ resultZip });
  await task.complete(receipt);
});
```

The MutualGPU operator must allow the provider application's browser origin. That is deployment configuration; SDK code still needs only the API base URL and provider key.

The API host uses an exact origin allow-list for browser enrollment and result upload. For example:

```text
MutualGPU__ProviderCorsOrigins__0=https://provider.example
```

Configure the origin, including scheme and port where applicable, rather than the API URL or WebSocket URL. Leave the list empty when browser providers are disabled.

Do not ship a provider key in a generally accessible public web application. A Chrome provider is suitable only when the provider operator controls the browser environment and key distribution.

## Accept or reject an assignment

An assignment starts pending. Before doing expensive work, call exactly one of:

```js
await task.accept();
```

or:

```js
await task.reject("GPU is below the required capability");
```

Receiving an assignment is not acceptance. If the handler returns while pending, the SDK rejects it automatically. After acceptance, the handler must complete or fail the task.

The task facade exposes an `acknowledgementDeadline` set to 30 seconds after local assignment receipt. Treat it as an operational deadline, not as a durable server timestamp. Accept or reject promptly instead of waiting until the final instant.

## Read task inputs

Scalar values are available as strings:

```js
const quality = Number(task.scalars.quality ?? "80");
```

An image assignment can contain an opaque, short-lived HTTPS descriptor:

```js
async function downloadInput(descriptor) {
  const response = await fetch(descriptor.url);
  if (!response.ok) throw new Error(`Input download failed: ${response.status}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength !== descriptor.length) {
    throw new Error("Input length did not match the assignment descriptor");
  }
  const digest = await sha256(bytes);
  if (digest !== descriptor.sha256.toLowerCase()) {
    throw new Error("Input checksum did not match the assignment descriptor");
  }
  return { bytes, contentType: descriptor.contentType };
}

async function sha256(bytes) {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)]
    .map(byte => byte.toString(16).padStart(2, "0"))
    .join("");
}
```

Validate `descriptor.contentType` against the enrolled image input before passing the bytes to a decoder. Treat the URL, input bytes, and scalar values as untrusted requestor data. Do not log the URL because it may contain short-lived storage authorization.

If the URL expires after acceptance, request a replacement through the task object:

```js
const refreshedUrl = await task.refreshInputDownload();
```

Fetch the URL as supplied. The storage implementation behind it is not part of the SDK contract.

## Report progress

Progress is optional and is valid only after acceptance:

```js
const sent = await task.reportProgress({
  phase: "inference",
  percent: 42,
  message: "Processing tile 21 of 50"
});
```

The SDK sends at most one update per second. It returns `false` when a call is dropped inside that interval, so execution must not depend on every progress update being delivered.

## Publish a result

Use `task.uploadResult`; do not construct an upload request:

```js
const { receipt, sha256 } = await task.uploadResult({
  resultZip,
  metadata: { durationMs: 812 },
  thumbnail,
  preview,
  logs
});

await task.complete(receipt);
```

`uploadResult` obtains authorization, calculates the ZIP checksum, builds and sends the result, validates the response, and returns the completion receipt. The returned `sha256` is available for application diagnostics.

Each part accepts a `Blob`, `ArrayBuffer`, typed array, string, or an object that supplies a body and optional MIME metadata:

```js
const preview = {
  data: previewBytes,
  contentType: "image/webp",
  fileName: "preview.webp"
};
```

The body property may be named `data`, `body`, or `content`. Set `contentType` explicitly for JPEG or WebP images; otherwise image parts default to `image/png`. `metadata` may be supplied directly as a plain JSON object.

Current demo limits are:

| Value | Constraint |
| --- | --- |
| `resultZip` or `zip` | Required valid ZIP, at most 50 MiB |
| `metadata` | JSON object, at most 64 KiB |
| `thumbnail` | Valid PNG, JPEG, or WebP, at most 5 MiB |
| `preview` | Declared image content type, at most 5 MiB |
| `logs` | UTF-8 text, at most 1 MiB |

The complete result is limited to 64 MiB. A malformed or server-rejected result consumes its single-use upload authorization and ends that attempt; application code should not replay it. If the network fails before a response is received, the outcome can be ambiguous. Do not blindly upload again: fail the active task with a safe `upload` diagnostic unless the operator's recovery policy explicitly permits another SDK-managed upload attempt. MutualGPU owns the logical task's retry policy.

## Report failure

After acceptance, report an execution failure with a stable step name and safe diagnostic message:

```js
if (!modelFilesAreValid) {
  await task.fail("model_load", "Installed model files failed validation");
  return;
}
```

Do not include provider keys, input URLs, requestor data, or other secrets in failure reasons.

An exception that escapes the handler after acceptance is converted by `ProviderClient` into an `execution` failure. Call `task.fail` directly when the application has a more useful stable failure step.

## Reconnect and shutdown

The SDK automatically retries an unexpected connection loss until `close()` is called. The default exponential delay is capped at 30 seconds between attempts; the number of attempts is not capped. If a task is active, it attempts to rebind that task during the server's grace period. Application code should keep the active workload in place and must not start duplicate execution.

Tests may replace the delay policy through the client constructor:

```js
const provider = new ProviderClient(transport, {
  reconnectDelay: attempt => Math.min(250 * 2 ** (attempt - 1), 5_000)
});
```

If rebind is no longer authorized, treat the old attempt as lost. Do not reuse old result authorization or try to complete it outside the SDK lifecycle.

During graceful shutdown:

```js
process.on("SIGTERM", () => provider.close());
process.on("SIGINT", () => provider.close());
```

Finish or fail accepted work before closing when possible. `close()` stops reconnect and closes the provider session, but it does not cancel the handler's workload. Keep an application-owned `AbortController` or equivalent cancellation mechanism if shutdown must interrupt local execution.

## Error handling

Errors from SDK methods mean the lifecycle operation did not complete. Useful handling rules are:

- enrollment failure: correct the capability definition or provider configuration before reconnecting;
- authentication failure: stop and replace the provider key or base URL;
- assignment rejection: return normally after `task.reject`;
- workload failure after acceptance: call `task.fail` once;
- result validation failure: do not replay the upload;
- connection loss: allow automatic reconnect unless shutting down.

`ProviderUploadError` describes a rejected result and exposes its HTTP `status`. `ProviderClientError` identifies invalid local lifecycle operations and exposes a stable `code`: `invalid_task_state` means a task method was called in the wrong state, while `not_connected` means manual reconnect was requested before a handler was registered. Transport, authentication, and protocol failures currently surface as ordinary `Error` instances. See the [consumer API reference](../reference/provider-api.md) for method preconditions and error handling.

## Local integration

From the repository root, trust the local certificate once and run the SDK handshake against the real API:

```text
just mutualgpu-dev-cert
just mutualgpu-local-smoke
```

The smoke command enrolls through the SDK, opens a provider session, waits for connection, and exits. It performs no GPU work.

Run a long-lived simulated provider for manual task submission:

```text
just mutualgpu-local-demo
```

Open `https://localhost:7043`, submit a task to `mutualgpu-local-demo`, and observe the SDK accept, report progress, upload a synthetic ZIP, and complete it.

Run the retained API, frontend, and SDK tests with:

```text
just mutualgpu-test
```

## Consumer checklist

- Use matching core and transport package versions.
- Supply an HTTPS base URL.
- Keep the provider key outside source control and logs.
- Enroll before connecting.
- Explicitly accept or reject every assignment.
- Keep one active task per `ProviderClient`.
- Use task methods instead of constructing API calls.
- Upload only result parts declared during enrollment.
- Complete with the receipt returned by `uploadResult`.
- Let the SDK own reconnect and task rebinding.
- Close the provider during graceful shutdown.
