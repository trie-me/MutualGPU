# MutualGPU

MutualGPU is a distributed WebGPU work exchange built on [NetCats](https://github.com/trie-me/NetCats).

It includes the pure domain, canonical capability catalogue, PostgreSQL-backed transactional workflows and provider credentials, an outbox-driven multi-replica scheduler, AWS S3 input/output artifacts, gRPC and binary-WebSocket provider connection adapters, paginated requestor/admin APIs, the additional capacity matrix, and Node.js/Chrome provider SDK packages.

The hosted partner test harness and browser SDK validator is maintained in
[MutualGPU.Providers.TestHarness](https://github.com/trie-me/MutualGPU.Providers.TestHarness).

PostgreSQL is the system of record for tasks, attempts, enrollments, capabilities, provider-key digests, partner reviews, artifact descriptors, upload receipts, audit events, and the operation outbox. AWS S3 stores only input and output bytes at deterministic object keys. The Development in-memory adapters exist for in-process tests; the local composition uses the repository’s `postgres:18` Docker service. The provider SDK supports Node.js and Chrome through a shared lifecycle and canonical Protobuf codec, with a native Node HTTPS/HTTP2 gRPC transport and Chrome WSS transport.

## How Codex accelerated MutualGPU

Codex with GPT-5.6 helped turn the original social-compute concept into an executable MVP through short design, implementation, and test loops. The work focused on MutualGPU's product and distributed-system decisions: defining requestor and provider roles, identifying which operations needed streaming, retaining native gRPC while supporting browsers through WebSockets and Protobuf, and bringing both transports behind one internal ProviderSession abstraction.

Codex also helped shape the safety and scheduling model: assignment-scoped authority, separate control traffic and large object transfers, and a scheduler limited to one active task per provider. Each decision moved quickly from discussion to working code and focused regression tests, which let us validate the architecture slice by slice and reduce delivery time. This is the MutualGPU Codex story; NetCats has a separate framework-development story, even where individual sessions overlapped.

## Demo MVP scope

The demo protects the core exchange invariants: HTTPS/WSS transport, provider-key authentication, durable task/enrollment facts, explicit acceptance, one active task per provider, bounded retry/revocation, single-use result upload tokens, valid ZIP plus SHA-256 validation, and requestor-scoped presigned downloads.

The following are intentionally deferred from the demo critical path and tracked as follow-up work rather than implied production guarantees:

- the Swift SDK release and its cross-language conformance run;
- automated browser UI/fiber-overlay smoke, stress, and visual checks (a separate task owns this);
- the complete production telemetry instrument set;
- streaming multipart uploads beyond the documented 64 MiB request / 50 MiB ZIP demo bounds;
- full JSON-Schema evaluation for optional result metadata. The MVP requires a declared metadata output and a bounded JSON object, but does not interpret a schema string beyond retaining it in the immutable capability contract;
- long-term artifact-retention policy and provider sandboxing.

These deferrals do not permit cleartext provider traffic or unauthenticated result publication.

## Run locally

Clone with the pinned NetCats dependency and restore the JavaScript workspace:

```text
git clone --recurse-submodules git@github.com:trie-me/MutualGPU.git
cd MutualGPU
npm install --prefix sdk/typescript
```

If the repository was cloned without `--recurse-submodules`, run `git submodule update --init --recursive` before restoring or building. The .NET projects target .NET 10.

Start the local PostgreSQL 18 service:

```text
just mutualgpu-postgres-up
```

The API requires PostgreSQL configuration and a base64-encoded 256-bit task-handle encryption key. The following development-only values match `compose.postgres.yaml`:

```text
export MutualGPU__Postgres__ConnectionString='Host=localhost;Port=55432;Database=mutualgpu;Username=mutualgpu;Password=mutualgpu-local'
export MutualGPU__Postgres__HandleEncryptionKey='bXV0dWFsZ3B1LWxvY2FsLWRldmVsb3BtZW50LWtleSE='
```

Provider bindings are stored as SHA-256 digests in PostgreSQL. To bind the fixed local demo provider without putting its key on a command line:

```text
export MUTUALGPU_EXECUTION_UNIT_ID='00000000-0000-0000-0000-000000000001'
export MUTUALGPU_PROVIDER_KEY='local-demo-key'
dotnet run --project tools/MutualGPU.ProviderKeyProvisioner -- --bind-environment
```

Chrome provider API origins remain an explicit allow-list:

```json
{
  "MutualGPU": {
    "ProviderCorsOrigins": ["https://provider.example"],
    "TrustForwardedProto": false
  }
}
```

`ProviderCorsOrigins` is the explicit allow-list for Chrome provider HTTP enrollment and result uploads. Leave it empty when no browser provider is used; it does not permit arbitrary origins.

The hosted **Offer compute** page at `/offer-compute.html` can mint a provider key and start a tab-scoped Chrome WebGPU provider. The provider advertises the full, unquantized FP16 `black-forest-labs/FLUX.2-klein-4B` text-to-image product at its pinned revision, fixed to the validated 1024×1024 four-step pipeline. Closing the hosting tab or clicking **Disconnect hosting** ends the provider session.

The browser model defaults to `KatzenStuff/flux-2-klein-4b-webgpu` on Hugging Face. Its entry manifest is at `models/klein-4b/pipeline-1024/manifest.json`, and its pinned Klein tokenizer files live at the repository root. The runtime resolves manifest-relative resources before enrollment and rejects mismatched model identities, revisions, component types, partition counts, and VAE precision. A deployment can replace the artifact origin before loading the module by setting `globalThis.MUTUALGPU_FLUX2_WEBGPU_MODEL_BASE_URL`; the default remains the pinned Hugging Face repository.

The first assignment downloads the Qwen encoder, four transformer partitions, and FP16 VAE. To stay within the 16 GiB target, the host encodes the prompt and releases Qwen before it loads the image pipeline. Transformer sessions remain resident for repeated generation with the same prompt; a different prompt switches back through the prompt-encoder phase.

MutualGPU rejects plaintext HTTP in every environment. For local development, install the .NET development certificate once and run an HTTPS listener:

```text
dotnet dev-certs https --trust
dotnet run --project src/MutualGPU.Api --urls https://localhost:7043
```

The repository-level `justfile` wraps the same local composition. Both commands start/wait for PostgreSQL, bind the local provider identity, wait for API readiness, and stop the API when the Node process exits or you press `Ctrl-C`:

```text
just mutualgpu-dev-cert     # one-time HTTPS development certificate setup
just mutualgpu-local-smoke  # API + real SDK Enroll/Connect handshake, then exit
just mutualgpu-local-demo   # API + long-running simulated provider for requestor UI testing
just mutualgpu-local-harness # offline browser integration harness, kept alive until Ctrl-C
```

`mutualgpu-local-demo` prints the local HTTPS URL. Open it once the simulated provider reports connected, choose **mutualgpu-local-demo**, and submit a task. The provider performs the real enrollment, gRPC session, result upload, and completion flow, but produces a synthetic ZIP rather than GPU work. Run the full API, frontend, and SDK test set with `just mutualgpu-test`.

`mutualgpu-local-harness` serves the local mirror of the provider reconnect
harness at `https://localhost:7043/local-harness/`. It keeps the remote harness's
certification controls and scenarios—SDK contract checks, accepted-task recovery,
success, terminal failure, requestor cancellation, and diagnostics—while locking
the page to the local HTTPS origin. The command runs the SDK contract suite before
hosting the page; the browser run uses PostgreSQL for durable state and local HTTPS
artifact endpoints for every input and result download. No Vercel, S3, CORS setup,
or external network access is involved.

### Administrator operations console

The read-only operations console is available at `/admin`. It shows provider sessions and their event timelines, durable task transactions, and assignment attempts. Task handles, provider credentials, upload tokens, and signed object URLs are deliberately omitted.

Admin access is disabled unless the host receives a master password through server configuration. Use a generated password of at least 24 bytes and keep it outside source control:

```text
export MUTUALGPU_ADMIN_PASSWORD="$(openssl rand -base64 32)"
MutualGPU__Admin__MasterPassword="$MUTUALGPU_ADMIN_PASSWORD" \
  dotnet run --project src/MutualGPU.Api --urls https://localhost:7043
```

Open `https://localhost:7043/admin` and enter the value held in `MUTUALGPU_ADMIN_PASSWORD`. The password is verified only on the server; the browser receives an opaque, eight-hour `HttpOnly`, `Secure`, `SameSite=Strict` session cookie. Failed logins are limited per client, responses are marked `no-store`, and restarting the API invalidates every admin session. Task transactions and attempts are read from durable storage, while the detailed provider connection timeline is a bounded process-local diagnostic journal and therefore begins again after a restart.

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

The PostgreSQL concurrency and rollback tests are opt-in so normal unit tests do not require Docker:

```text
MUTUALGPU_TEST_POSTGRES='Host=localhost;Port=55432;Database=mutualgpu;Username=mutualgpu;Password=mutualgpu-local' \
  dotnet test tests/MutualGPU.Application.Tests --filter PostgresOperationUnitOfWorkTests
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
