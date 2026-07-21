# MutualGPU

MutualGPU is the third NetCats example: a distributed WebGPU work exchange.

It includes the pure domain, canonical capability catalogue, cold enrollment/submission workflows, an S3-compatible Backblaze adapter, gRPC and binary-WebSocket provider connection adapters, a process-local triggered scheduler, static requestor APIs, optional result artifacts, the additional capacity matrix, and Node.js/Chrome provider SDK packages.

The Development in-memory object store is for local demonstration and in-process tests only. Backblaze is the configured production adapter. The provider SDK supports Node.js and Chrome through a shared lifecycle and canonical Protobuf codec, with a native Node HTTPS/HTTP2 gRPC transport and Chrome WSS transport. Its fixture suite verifies the wire format against the .NET-generated contracts. Consumer setup and API behaviour are documented in the [provider SDK documentation](../../docs/sdk/README.md), and the target exchange behaviour is defined in [the specification](../../docs/08-mutualgpu-example-implementation.md).

## Demo MVP scope

The demo protects the core exchange invariants: HTTPS/WSS transport, provider-key authentication, durable task/enrollment facts, explicit acceptance, one active task per provider, bounded retry/revocation, single-use result upload tokens, valid ZIP plus SHA-256 validation, and requestor-scoped presigned downloads.

The following are intentionally deferred from the demo critical path and tracked as follow-up work rather than implied production guarantees:

- the Swift SDK release and its cross-language conformance run;
- automated browser UI/fiber-overlay smoke, stress, and visual checks (a separate task owns this);
- the complete production telemetry instrument set and live Backblaze contract suite;
- streaming multipart uploads beyond the documented 64 MiB request / 50 MiB ZIP demo bounds;
- full JSON-Schema evaluation for optional result metadata. The MVP requires a declared metadata output and a bounded JSON object, but does not interpret a schema string beyond retaining it in the immutable capability contract;
- multi-replica scheduler leadership, distributed repository locks, retention policy, and provider sandboxing.

These deferrals do not permit cleartext provider traffic or unauthenticated result publication.

## Run locally

The Development host uses an in-memory object store when `MutualGPU:Backblaze` is absent. Provider credentials are deliberately configuration-only:

```json
{
  "MutualGPU": {
    "Providers": [
      { "ExecutionUnitId": "00000000-0000-0000-0000-000000000001", "PresharedKey": "local-demo-key" }
    ],
    "ProviderCorsOrigins": ["https://provider.example"],
    "TrustForwardedProto": false
  }
}
```

`ProviderCorsOrigins` is the explicit allow-list for Chrome provider HTTP enrollment and result uploads. Leave it empty when no browser provider is used; it does not permit arbitrary origins.

To enable the hosted **Enroll my WebGPU** dialog, set a demo enrollment password. The endpoint is disabled unless this value is supplied. On ECS, use the corresponding `MutualGPU__WebGpuEnrollment__Password` task environment variable.

```json
"WebGpuEnrollment": {
  "Password": "choose-a-demo-password"
}
```

The dialog confirms WebGPU support before issuing a new provider key. The raw key is returned once to the browser; the durable registry stores only its binding and digest.

MutualGPU rejects plaintext HTTP in every environment. For local development, install the .NET development certificate once and run an HTTPS listener:

```text
dotnet dev-certs https --trust
dotnet run --project src/MutualGPU.Api --urls https://localhost:7043
```

The repository-level `justfile` wraps the same local composition. Both commands configure the local provider identity, wait for API readiness, use the in-memory store, and stop the API when the Node process exits or you press `Ctrl-C`:

```text
just mutualgpu-dev-cert     # one-time HTTPS development certificate setup
just mutualgpu-local-smoke  # API + real SDK Enroll/Connect handshake, then exit
just mutualgpu-local-demo   # API + long-running simulated provider for requestor UI testing
```

`mutualgpu-local-demo` prints the local HTTPS URL. Open it once the simulated provider reports connected, choose **mutualgpu-local-demo**, and submit a task. The provider performs the real enrollment, gRPC session, result upload, and completion flow, but produces a synthetic ZIP rather than GPU work. Run the full API, frontend, and SDK test set with `just mutualgpu-test`.

For a TLS-terminating platform such as Vercel, keep public traffic at HTTPS and set `MutualGPU__TrustForwardedProto=true` only when the app is reachable exclusively through that trusted ingress. The host then honors the ingress `X-Forwarded-Proto` value before applying its HTTPS policy. Do not enable it for a directly exposed process.

Native providers authenticate gRPC calls through `Authorization`; Chrome providers send the same value in the first Protobuf `ConnectRequest` frame. Browser SDK configuration must use `https://` for its API base URL and `wss://` for its provider session URL. The shared schema is [provider.proto](src/MutualGPU.Protocol/provider.proto).

Fiber diagnostics are disabled by default. Enable the optional projection and endpoints with:

```text
NetCats__FiberDiagnostics__Enabled=true
```

## Verification

The API integration tests run the host in-process, including static-file delivery, readiness, diagnostics snapshot/SSE, authenticated gRPC and WebSocket provider sessions, CORS preflight, multipart results, and authorized result descriptors:

```text
dotnet test NetCats.Examples.MutualGPU.slnx
```

The no-build-step frontend matrix and fiber-overlay lifecycle are tested directly with Node's built-in test runner:

```text
node --test tests/frontend/*.test.mjs
```

The TypeScript lifecycle, multipart uploader, and browser enrollment adapter are independently tested:

```text
npm test --prefix sdk/typescript
```

For the fastest local integration check, use the Node SDK API smoke test in the SDK README. It calls the real API's gRPC `Enroll` and `Connect` service and exits after the server handshake—no task, GPU handler, or public hosting is involved.

For a complete local requestor demonstration, the same SDK README documents `npm run demo:node`: a long-running synthetic provider that accepts and completes tasks without claiming GPU execution.

The following is a deferred Swift backlog check, not part of demo-MVP sign-off:

```text
swift build --package-path sdk/swift/MutualGPUProvider
```
