# Backblaze contest execution plan

Status: draft for iteration; planning only

Scope: make Backblaze B2 the future primary writer for MutualGPU artifact bytes
while preserving provider-pinned historical reads and current public API/SDK
contracts.

## Outcome

Ship a separately reviewed storage release in which:

- new input and result bytes are written to an explicitly selected B2 target;
- each artifact remains retrievable from its recorded AWS or B2 location;
- a missing or unhealthy recorded target fails explicitly, without
  cross-provider fallback;
- MutualGPU authenticates and validates uploads at the API boundary; and
- authorized input and result downloads go directly to the recorded provider
  through short-lived provider-native presigned URLs.

File/object-storage providers are used only for input and result bytes.
PostgreSQL remains authoritative for logical artifact metadata and lifecycle
state. Provider configuration, credentials, and presigned URLs remain outside
artifact records.

## Starting boundary

- Production writes to `aws-primary` today. The current PostgreSQL write-target
  selection deliberately rejects any other target.
- `artifact_locations` records the immutable storage target ID, object key,
  opaque provider ETag, and location state. PostgreSQL-backed download routing
  uses the one recorded available location and does not substitute providers.
- The B2 adapter and lazy target registry support explicitly addressed B2
  reads. Merely retaining a B2 target in configuration must not initialize or
  contact it.
- Current requestor inputs and provider results arrive as API multipart
  payloads. The API validates them, calculates SHA-256, and writes their bytes
  through the selected object store.
- Existing permissive API CORS and disabled object-storage CORS synchronization
  remain unchanged unless a separate security review approves a change.
- The legacy `artifacts.s3_object_key` field and legacy AWS configuration remain
  compatible. Removing or renaming either is outside this additive release.

## Do SDK uploads transit the API?

Yes. Requestor and provider SDKs send input/result multipart payloads to
MutualGPU. MutualGPU authenticates the caller, validates content and limits,
calculates the provider-independent SHA-256, and writes the accepted bytes to
the explicitly selected provider. SDKs do not receive direct object-store
upload credentials or upload URLs. Downloads remain direct: after MutualGPU
authorizes access, it returns a short-lived presigned URL from the artifact's
recorded provider.

## Execution sequence

### 1. Freeze the additive contract

- Name the future target with an immutable ID such as `backblaze-primary`; bind
  that ID to one reviewed B2 account, bucket, region, and canonical HTTPS S3
  endpoint.
- Preserve artifact identity, roles, response shapes, endpoints, multipart
  fields, SDK methods, checksums, receipts, and completion behavior.
- Define the write switch as forward-only for new uploads. It does not copy,
  migrate, synchronize, or rewrite existing AWS or B2 artifact locations.
- Keep credentials in the deployment secret boundary. Store only target ID,
  object key, opaque ETag, and lifecycle timestamps in PostgreSQL.

Exit: the target identity, activation owner, rollback rule, retention policy,
and download lifetime are approved without changing a public contract.

### 2. Generalize the write path

- Replace the AWS-only `ArtifactStorageTargetSelection` guard with validation
  that requires one explicitly configured, allow-listed write target.
- Capture the selected target exactly once when an input upload or result-upload
  operation begins. Persist that target on durable result-upload state before
  writing result bytes.
- Resolve writes only through that captured target. Never retry a failed logical
  write against another provider.
- Write the bytes, retain any provider-issued ETag as opaque metadata, and
  commit the logical artifact plus its `artifact_locations` row in the same
  PostgreSQL operation. Continue populating compatibility fields during the
  additive release.
- Route input and result orphan reporting/reconciliation only to the recorded
  target. Keep deletion disabled until provider-specific safety and recovery
  gates are separately approved.

Exit: selecting `backblaze-primary` sends every new artifact write to B2 and
records that exact target; selecting `aws-primary` preserves current behavior.

### 3. Complete provider-pinned download behavior

- Authorize the requestor or assigned provider against the logical artifact
  before resolving any storage target.
- Load committed, available locations and require the reviewed selection policy.
  For this release, require exactly one available location.
- Ask only that location's provider for a bounded-lifetime HTTPS presigned URL
  for requestor results, provider task inputs, refreshed input URLs, and any
  authorized administrative download.
- Return logical content type, length, and SHA-256 with the ephemeral URL where
  the existing contract already does so. Never persist or log signed URLs.
- Return an explicit unavailable-target/location error when resolution fails;
  do not probe AWS after B2 failure or B2 after AWS failure.

Exit: AWS-recorded and B2-recorded fixtures download from their own providers,
and negative tests prove that no fallback provider is contacted.

### 4. Prove safety and compatibility

Add automated coverage for:

- requestor input and provider result multipart uploads through MutualGPU,
  including authentication, content validation, size limits, SHA-256, receipt,
  completion replay, and provider ETag handling;
- concurrent write-target configuration changes, proving an in-flight upload
  remains pinned to the target captured at operation start;
- B2 failure after byte publication but before database commit, plus bounded
  provider-pinned orphan reporting;
- AWS and B2 historical reads, unconfigured targets, missing locations,
  multiple locations, expired URLs, and explicit no-fallback behavior;
- lazy registry behavior proving an unselected/unreferenced B2 target is not
  constructed or contacted;
- unchanged REST, gRPC/WebSocket, browser, and shipped SDK behavior; and
- unchanged API CORS plus no object-storage CORS synchronization when the
  existing production settings are retained.

Run the existing unit and integration suites and add contract tests with fake
AWS/B2 stores that record every attempted operation. Real-provider validation,
if later authorized, must use a dedicated non-production B2 target and
non-sensitive fixtures.

Exit: all tests pass, compatibility review finds no breaking API/SDK change,
and no test requires direct SDK upload credentials or URLs.

### 5. Prepare the contest release and evidence

- Document the architecture as API-mediated uploads, PostgreSQL metadata, and
  direct provider-native downloads.
- Demonstrate two historical artifacts pinned to different providers and one
  new artifact written to B2; show target IDs and aggregate telemetry only, not
  object keys, credentials, or signed URLs.
- Capture evidence for successful B2 write/read, successful retained AWS read,
  failed B2 read with zero AWS fallback calls, and B2 remaining untouched when
  an AWS-only artifact is downloaded.
- Prepare operator guidance for target configuration, secret rotation,
  readiness, explicit activation, rollback, and disabling deletion-capable
  reconciliation.

Exit: the contest narrative, demo script, evidence checklist, security review,
and release checklist are approved.

### 6. Activate explicitly and observe

This phase requires a separate reviewed deployment authorization; this plan
does not grant it.

- Verify the production deployment still selects `aws-primary` before the
  change and that retained AWS target configuration can resolve all referenced
  AWS locations.
- Install B2 configuration and secrets without changing the selected writer.
  Configuration alone must not contact B2.
- Run only the approved non-mutating readiness checks, then change the writer to
  `backblaze-primary` in one explicit deployment revision.
- Perform one authorized canary input/result cycle, verify its recorded B2
  location and presigned downloads, and verify a retained AWS artifact still
  downloads from AWS.
- Observe write failures, presign failures, target-specific latency, orphan
  reports, and location consistency without logging sensitive values.

Rollback changes the writer for subsequent uploads back to `aws-primary`; it
does not rewrite B2 locations created while B2 was active. Those artifacts stay
readable from B2, so the B2 target and credentials must remain configured while
referenced.

## Release gates

- No breaking public API or SDK change. If one becomes necessary, stop and
  prepare a major-version breaking-change notice and migration instructions.
- No B2 contact unless an authorized artifact references B2 or B2 is explicitly
  selected for an approved operation.
- No implicit cross-provider read, write, retry, reconciliation, or diagnostic
  fallback.
- No artifact-byte migration or replication is implied by activating B2.
- No credential, object key, presigned URL, token, handle, or user payload in
  logs, metrics, documentation evidence, or contest media.
- Existing permissive API CORS and disabled storage-CORS synchronization remain
  unchanged unless separately reviewed.

## Unresolved decisions for iteration

1. Confirm the immutable B2 target ID (proposed: `backblaze-primary`) and the
   dedicated bucket, region, endpoint, and account ownership.
2. Choose the provider-native presigned URL lifetime and refresh behavior for
   requestor results and provider inputs.
3. Approve B2 bucket retention, lifecycle, versioning/file-lock, and recovery
   settings before any deletion-capable reconciliation is considered.
4. Confirm that B2 browser-read CORS remains externally managed while storage
   CORS synchronization stays disabled, and record the required read origins.
5. Decide the activation window, canary artifacts, observation period, rollback
   owner, and thresholds that return subsequent writes to AWS.
6. Decide whether replication is a later product feature. It is not part of
   this contest release; if added, it needs an explicit multi-location selection
   and lifecycle policy.
7. Select the contest demo format and the non-sensitive evidence that best
   demonstrates B2 primary writes, retained AWS reads, and zero fallback.
