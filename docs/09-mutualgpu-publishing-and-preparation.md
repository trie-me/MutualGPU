# MutualGPU publishing and cutover preparation

Status: current
Date: 2026-07-28

This document is the release gate for the PostgreSQL transactional-data
cutover. PostgreSQL is the only structured operational store. AWS S3 stores
input and output bytes at predictable keys and PostgreSQL stores the queryable
artifact descriptors.

## Storage boundary

The production API requires both:

- PostgreSQL for tasks, attempts, capabilities, execution units, provider-key
  digests, partner reviews, upload authorization/receipt state, artifact
  descriptors, audit records, the migration ledger, and the outbox; and
- AWS S3 for input payload bytes and output artifact bytes.

No request path lists S3 to answer a task, queue, requestor, admin, partner, or
artifact query. List operations are limited to the migration utility and the
bounded orphan-object reconciler.

Output object keys are deterministic:

```text
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/results/{attempt-n}/result.zip
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/results/{attempt-n}/metadata.json
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/results/{attempt-n}/thumbnail.{ext}
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/results/{attempt-n}/preview.{ext}
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/results/{attempt-n}/logs.txt
```

Inputs retain a unique artifact identifier:

```text
mutualgpu/v3/requestors/{requestor-n}/tasks/{task-n}/inputs/{artifact-n}.{ext}
```

The database stores the exact object key, role, ownership, media type, length,
SHA-256 digest, and lifecycle state. Presigned URLs are generated on demand and
are never persisted.

## Build and configuration gate

Build and test the immutable image from the repository root:

```bash
dotnet restore NetCats.Examples.MutualGPU.slnx
dotnet test NetCats.Examples.MutualGPU.slnx --no-restore
dotnet publish src/MutualGPU.Api/MutualGPU.Api.csproj \
  --configuration Release \
  --output artifacts/publish
```

Production startup fails closed unless `MutualGPU:Postgres` and durable object
storage (`MutualGPU:S3`, or the explicit `MutualGPU:ObjectStorage` registry)
are configured. The current production release continues to select
`aws-primary` for all new writes. Ordinary local development also requires
PostgreSQL. The old object-store persistence path can be enabled only by the
explicit Development characterization-test setting
`MutualGPU:Testing:AllowLegacyObjectStorePersistence=true`.

Required secret material:

- database password from `mutualgpu/postgres`;
- a base64-encoded 256-bit `taskHandleEncryptionKey` in
  `mutualgpu/deployment`; and
- the existing administrator and requestor-diagnostics secrets.

Task handles are envelope-encrypted before storage. Provider keys and upload
tokens are SHA-256 digested; their raw values are returned or accepted only at
the protocol boundary and are never stored or logged.

All identifiers minted by the service or database use UUIDv7. JSON enums use
`JsonStringEnumConverter`; durable lifecycle enums use native PostgreSQL enum
types.

## Local PostgreSQL 18

The repository composition publishes PostgreSQL on host port `55432`:

```bash
just mutualgpu-postgres-up
```

```bash
export MutualGPU__Postgres__ConnectionString='Host=localhost;Port=55432;Database=mutualgpu;Username=mutualgpu;Password=mutualgpu-local'
export MutualGPU__Postgres__HandleEncryptionKey='MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY='
```

The API applies embedded, checksummed migrations under a PostgreSQL advisory
lock. Readiness requires a compatible schema, a database connection, and S3
health.

## Upload protocol boundary

The provider protocol is not changed by the database cutover:

1. the provider asks for an upload authorization;
2. the API returns the existing opaque one-time token;
3. the provider uploads the existing multipart result;
4. the API returns the existing receipt; and
5. the provider completes the task with that receipt.

Only the backing state changes. PostgreSQL stores the token digest, expiry,
one-time consumption state, receipt, artifact descriptors, and completion
replay state. It never stores the raw token. Result bytes go directly to their
predictable S3 keys. Staging descriptors and the receipt are committed
atomically; completion advances upload, attempt, task, audit, artifacts, and
outbox state in one database transaction.

## Import and verification

The migration command resolves the current buckets from CloudFormation using
the mandatory `ai-quinn` profile. Database connection and handle-encryption
settings must already be present in the environment.

```bash
scripts/import-transactional-data.sh import
scripts/import-transactional-data.sh verify
```

The importer:

- reads only AWS S3;
- never downloads or copies artifact bodies;
- selects the latest task fact, falling back to the manifest;
- imports provider digests, capabilities, enrollment history, current
  execution units, tasks/attempts/events, partner requests, and artifact
  descriptors;
- records each source key and ETag in `migration_ledger`;
- treats queue markers and projections as derived verification inputs; and
- emits only counts, IDs, status distributions, and discrepancy codes.

It is resumable. A source key already recorded with the same ETag is skipped,
and database uniqueness constraints prevent duplicate logical records. A
changed ETag or conflict is reported rather than silently overwritten.

## Controlled cutover

Do not dual-write structured state to S3 and PostgreSQL.

1. Deploy the RDS schema and application support dark.
2. Run the bulk import.
3. Enable the write pause and stop new assignments.
4. Drain bounded in-flight operations.
5. Run the incremental import.
6. Run verification and require no unexplained discrepancies.
7. deploy the PostgreSQL-only application;
8. allow startup recovery and outbox dispatch to settle; and
9. exercise task pagination/detail, provider authentication/enrollment,
   assignment, upload/completion replay, partner review, and artifact download.

Legacy structured S3 objects remain read-only for the approved rollback window.
Deleting them is a separate, explicitly approved retention operation.

## Release checklist

- [ ] Empty-database and upgrade migrations pass against PostgreSQL 18.
- [ ] Concurrent-write and rollback integration tests pass.
- [ ] Full .NET, SDK, and frontend test suites pass.
- [ ] CloudFormation validates and RDS is private, encrypted, TLS-only, and
      backed up.
- [ ] Runtime S3 permissions cover only input/result prefixes and bucket CORS.
- [ ] Bulk and incremental import reports contain no unexplained discrepancy.
- [ ] No credential, task handle, upload token, receipt, presigned URL, or raw
      scalar parameter appears in logs or migration output.
- [ ] Two ECS replicas pass readiness and outbox recovery checks.
