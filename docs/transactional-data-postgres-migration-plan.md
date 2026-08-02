# Transactional data Postgres migration plan

Status: implemented in source; production import/cutover requires an operator write pause
Date: 2026-07-28  
Scope: MutualGPU operational and transactional state

Implementation note: the schema, operation unit of work, PostgreSQL
repositories/readers, durable upload state, native database enums, outbox,
artifact reconciliation, AWS infrastructure, resumable importer, verification
report, pagination, and production-only cutover registrations described here
are checked in. The repository PostgreSQL 18 composition publishes host port
`55432`. No production import, write pause, or legacy-object deletion is
performed merely by applying this implementation.

## Executive decisions

1. PostgreSQL becomes the system of record for structured operational data.
2. AWS S3 remains the system of record for input and output artifact bytes.
   Artifact descriptors and lifecycle state move to PostgreSQL; artifact content
   does not.
3. Each application command runs inside an explicit operation unit of work. The
   unit of work owns one PostgreSQL transaction, repositories bound to that
   transaction, optimistic-version tracking, audit events, and outbox messages.
4. Aggregate writes use an explicit `bigint` version and compare-and-swap
   updates. PostgreSQL `xmin`, process-local locks, and last-write-wins updates
   are not concurrency contracts.
5. PostgreSQL runs at `READ COMMITTED`. Aggregate versions and database
   constraints detect conflicts; command handlers decide whether to reload and
   retry, skip, or return a conflict.
6. Queue markers, task-summary files, and commit markers are replaced by
   indexed SQL queries and database transactions. They are not copied into
   equivalent database tables.
7. Provider connections, advisory progress, NetCats fiber diagnostics, and
   short-lived caches remain process-local. Durable assignment, upload,
   completion, and recovery state moves to PostgreSQL.
8. The migration uses the current AWS S3 and CloudFormation/ECS configuration
   as its source. It must not use Backblaze/B2 or infer resources from legacy
   documentation or adapters.

## Reference interpretation

The repository contains an example implementation specification at
`docs/08-mutualgpu-example-implementation.md`; the HTML files are application
and administration user interfaces, not an architecture specification. This
plan follows the example specification's useful structural conventions:

- hydrate a fresh aggregate for each operation;
- apply domain decisions in the application layer;
- make state changes atomic inside a unit of work;
- publish projections or notifications only after a successful commit; and
- keep binary object storage behind its existing application port.

The example specification's historical Backblaze-only decision is not carried
forward. Current production storage is AWS S3.

## Goals

- Put queryable, mutable, relational state in PostgreSQL.
- Make every task and enrollment transition safe with multiple API replicas.
- Prevent stale aggregate instances from overwriting newer state.
- Make task submission idempotency a database invariant.
- Make result-upload staging and completion replay durable across restarts.
- Replace S3 prefix scans and process-local repository locks with indexed SQL
  access and database constraints.
- Preserve the existing domain model and application-layer ownership of
  business rules.
- Support a measured migration, validation, cutover, and rollback procedure.

## Non-goals

- Moving uploaded input images or generated output artifacts into PostgreSQL.
- Replacing S3 presigned upload or download URLs.
- Persisting provider sockets, gRPC streams, WebSocket instances, NetCats
  fibers, or every advisory progress update.
- Introducing event sourcing as the primary persistence model.
- Changing task, attempt, enrollment, or retry-budget business rules.
- Deleting legacy S3 state as part of the initial cutover.

## Data placement

### Move to PostgreSQL

| Current state | PostgreSQL owner | Notes |
| --- | --- | --- |
| Task manifests and latest task facts | `tasks` | Current aggregate row, immutable submission fields, status, and version. |
| Task attempt snapshots and attempt events | `task_attempts`, `task_events` | Current attempt rows plus append-only audit history. |
| Task parameters and capability snapshot | `tasks` JSONB columns | They are structured transactional inputs, not uploaded artifact bytes. |
| Queue markers | Indexed `tasks` query | Query queued rows by capability, resources, and creation order. |
| Requestor task-summary projections | Indexed `tasks` query | Do not maintain a second mutable summary model. |
| S3 commit markers | PostgreSQL transaction log | No application-level replacement table is required. |
| Capability definitions | `capabilities` | Enforce canonical-name ownership with a unique normalized name. |
| Execution-unit identities and current enrollments | `execution_units` | Keep a separate persistence version from the business enrollment version. |
| Enrollment event files | `enrollment_events` | Append in the same transaction as the current enrollment update. |
| Provider-key bindings | `provider_credentials` | Store only the current digest/binding and lifecycle fields, never the raw key. |
| Partner resource submissions and review state | `partner_resource_requests` | Approval and revocation are versioned writes. |
| Input and result artifact descriptors | `artifacts` | S3 key, role, content type, length, digest, and lifecycle only. |
| Upload-token and staged-result state | `result_upload_operations` | Store token digests, expiry, receipt, and state; never store a raw upload token. |
| Completion replay ledger | `result_upload_operations` | A repeated completion for the same receipt returns the prior success. |
| Durable application notifications | `operation_outbox` | Written atomically with aggregate changes and dispatched after commit. |

### Stay in AWS S3

- Input image bytes.
- Result ZIP bytes.
- Thumbnail and preview bytes.
- Output metadata documents and log bytes when they are declared output
  artifacts.
- Existing object keys needed to create scoped presigned URLs.
- Legacy transactional JSON during the agreed rollback and retention period.
  It becomes read-only after cutover.

PostgreSQL stores descriptors for these objects, including their S3 object key,
content type, size, checksum, role, and ownership. It does not store the object
body or a presigned URL.

### Stay process-local

- Live provider connection/session objects.
- gRPC and WebSocket send queues.
- The latest advisory task progress value. Progress remains bounded and may be
  lost on restart.
- NetCats fiber-tree diagnostic state.
- Scheduler wake-up channels and coalesced requestor SSE notifications.
- Read-through caches. A cache is never authoritative and must be rebuildable
  from PostgreSQL.

### Retire after cutover

The following S3 transactional prefixes stop receiving writes after cutover:

```text
mutualgpu/v3/capabilities/
mutualgpu/v3/provider-keys/
mutualgpu/v3/partner-resources/
mutualgpu/v3/nodes/*/identity.json
mutualgpu/v3/nodes/*/enrollments/
mutualgpu/v3/requestors/*/tasks/*/manifest.json
mutualgpu/v3/requestors/*/tasks/*/facts/
mutualgpu/v3/requestors/*/tasks/*/attempts/
mutualgpu/v3/requestors/*/projections/
mutualgpu/v3/queue/
mutualgpu/v3/commits/
```

Input and result prefixes remain active. Removal of retired objects is a
separate, explicitly authorized retention operation after rollback has expired;
it is not part of this migration.

## Target relational model

The first migration should create the following logical model. Names may be
adjusted to the repository's SQL naming convention, but the boundaries and
constraints should remain.

### `capabilities`

- `id uuid primary key`
- `name text not null`
- `normalized_name text not null unique`
- `contract_hash text not null`
- `definition jsonb not null`
- `created_at timestamptz not null`

The existing first-contract-owns-name rule is enforced by the unique normalized
name. Enrollment handles a uniqueness conflict by reloading the canonical
definition and applying the existing contract resolution rule.

### `provider_credentials`

- `digest char(64) primary key`
- `execution_unit_id uuid not null unique`
- `created_at timestamptz not null`
- `revoked_at timestamptz null`
- `version bigint not null`

Only the SHA-256 digest is retained. Raw preshared keys are returned once when
issued and are never logged or stored.

### `execution_units`

- `id uuid primary key`
- `enrollment_version bigint not null`
- `persistence_version bigint not null`
- `machine jsonb not null`
- `current_enrollment jsonb not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

`enrollment_version` remains the domain's business version.
`persistence_version` is the optimistic concurrency token.

### `execution_unit_capabilities`

- `execution_unit_id uuid not null references execution_units`
- `capability_id uuid not null references capabilities`
- `enrollment_version bigint not null`
- primary key on `(execution_unit_id, capability_id)`

This table supports operational queries without scanning enrollment JSON. It is
replaced inside the same transaction as the execution unit's current
enrollment.

### `enrollment_events`

- `id uuid primary key`
- `execution_unit_id uuid not null references execution_units`
- `enrollment_version bigint not null`
- `occurred_at timestamptz not null`
- `snapshot jsonb not null`
- unique key on `(execution_unit_id, enrollment_version)`

### `tasks`

- `id uuid primary key`
- `requestor_id uuid not null`
- `capability_id uuid not null`
- `capability_contract_hash text not null`
- `capability_snapshot jsonb not null`
- `compute_tier smallint not null`
- `memory_gib integer not null`
- `scalar_parameters jsonb not null`
- `idempotency_key text null`
- `submission_fingerprint char(64) null`
- `requestor_ip_hash text null`
- `requestor_ip_class_ab text null`
- `status text not null`
- `version bigint not null`
- `created_at timestamptz not null`
- `updated_at timestamptz not null`

Required constraints and indexes:

- unique `(requestor_id, idempotency_key)` where the key is not null;
- index `(requestor_id, created_at desc)`;
- partial queue index
  `(capability_id, compute_tier, memory_gib, created_at, id)` where
  `status = 'queued'`; and
- checks for known task status, valid resource values, and `version > 0`.

The submission fingerprint is a canonical hash of the fields currently used by
`SameSubmission`. A uniqueness conflict loads the existing task and compares
the fingerprint, returning replay or `idempotency_key_reused` deterministically.

### `task_attempts`

- `id uuid primary key`
- `task_id uuid not null references tasks`
- `execution_unit_id uuid not null references execution_units`
- `assignment_number smallint not null`
- `handle_digest char(64) not null`
- `handle_ciphertext bytea not null`
- `state text not null`
- assigned, accepted, disconnected, and terminal timestamps
- failure step and bounded failure reason
- provider session, network-correlation, display-name, and transport fields
- unique `(task_id, assignment_number)`

A partial unique index permits at most one active attempt per task where state
is `assigned`, `accepted`, or `disconnected`. The domain's maximum of four
assignments remains enforced in the domain and is backed by a check on
`assignment_number`.

The current task handle is an authorization capability. Use the digest for
constant-time lookup or comparison and application-level envelope encryption
for the recoverable value needed by post-commit assignment delivery and
recovery. Keep the encryption key outside PostgreSQL in the current secret
management boundary. An outbox assignment message contains the attempt ID and
loads this protected value after commit; it does not duplicate the handle in
the outbox payload. The handle must never appear in logs or metrics.

### `task_events`

- `id uuid primary key`
- `task_id uuid not null references tasks`
- `task_version bigint not null`
- `attempt_id uuid null references task_attempts`
- `event_type text not null`
- `occurred_at timestamptz not null`
- `payload jsonb not null`
- unique `(task_id, task_version, event_type, attempt_id)`

Events are an audit trail written with the current-state update. They are not
replayed to hydrate normal commands.

### `artifacts`

- `id uuid primary key`
- `task_id uuid not null references tasks`
- `attempt_id uuid null references task_attempts`
- `direction text not null` (`input` or `output`)
- `role text not null`
- `s3_object_key text not null unique`
- `content_type text not null`
- `length bigint not null`
- `sha256 char(64) not null`
- `state text not null` (`staged`, `available`, `orphaned`, or `deleted`)
- `created_at timestamptz not null`

The current AWS-only release resolves bucket and region from service
configuration rather than copying them into every row. The additive
multi-provider model is specified in
`docs/multi-provider-artifact-storage-strategy.md`: logical artifact metadata
remains in `artifacts`, while provider-specific target, object key, opaque ETag,
and location lifecycle move to `artifact_locations`. Presigned URLs remain
ephemeral and are never stored.

### `result_upload_operations`

- `id uuid primary key`
- `task_id uuid not null references tasks`
- `attempt_id uuid not null references task_attempts`
- `execution_unit_id uuid not null`
- `handle_digest char(64) not null`
- `token_digest char(64) null unique`
- `receipt text null unique`
- `state text not null` (`authorized`, `uploading`, `uploaded`, `completed`,
  `expired`, or `failed`)
- `expires_at timestamptz null`
- `version bigint not null`
- `created_at`, `uploaded_at`, and `completed_at`

Artifact rows are associated with this operation after S3 upload. Completion
atomically consumes the uploaded receipt, completes the attempt and task,
publishes audit/outbox records, and marks the operation completed. The same
attempt and receipt may then return the recorded success.

### `partner_resource_requests`

- current request fields;
- normalized origin;
- submitted, approved, and revoked timestamps; and
- `version bigint not null`.

Indexes support pending and currently approved queries. The in-memory CORS set
is a cache rebuilt from this table and refreshed after committed changes.

### `operation_outbox`

- `id uuid primary key`
- `operation_id uuid not null`
- `kind text not null`
- `payload jsonb not null`
- `occurred_at timestamptz not null`
- `available_at timestamptz not null`
- `attempt_count integer not null default 0`
- `processed_at timestamptz null`
- `last_error_code text null`

Payloads contain IDs and bounded routing facts, not provider keys, task handles,
upload tokens, presigned URLs, scalar parameters, or raw error text.

## Operation unit of work

### Application contract

Introduce one unit of work for application commands rather than exposing
connections or transactions to the domain:

```csharp
public interface IOperationUnitOfWork
{
    Task<T> ExecuteAsync<T>(
        Func<IOperationContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public interface IOperationContext
{
    ITaskRepository Tasks { get; }
    IExecutionUnitRepository ExecutionUnits { get; }
    ICapabilityRepository Capabilities { get; }
    IPartnerResourceRepository PartnerResources { get; }
    IResultUploadRepository ResultUploads { get; }
    IArtifactRepository Artifacts { get; }

    void Publish(OperationEvent message);
}
```

Query readers remain separate and can use pooled read-only connections. Mutation
repositories are available only through `IOperationContext`, ensuring they
share the unit of work's connection and transaction.

The infrastructure implementation, `PostgresOperationUnitOfWork`, owns:

- one pooled `NpgsqlConnection`;
- one `NpgsqlTransaction` at `READ COMMITTED`;
- an identity map scoped to the operation;
- each tracked aggregate's originally loaded persistence version;
- staged inserts, updates, deletes, audit events, and outbox messages; and
- the operation lifecycle.

Its lifecycle is:

```text
created -> active -> committed
                  \-> rolled_back
                  \-> conflicted
```

It must:

1. open the connection and transaction immediately before invoking the
   operation delegate;
2. hydrate fresh aggregates and track their expected versions;
3. reject concurrent use, nested execution, or commit after completion;
4. flush aggregate changes with compare-and-swap statements;
5. insert audit and outbox rows before commit;
6. commit only if every tracked write succeeded;
7. roll back on cancellation, domain failure, database failure, or concurrency
   conflict;
8. throw a typed `OptimisticConcurrencyException` containing only safe
   aggregate type, aggregate ID, and expected version; and
9. dispose the transaction and connection in every outcome.

Application events are durable outbox records. In-process scheduler and SSE
notifications occur only after commit or through the outbox dispatcher. They
must not run inside the database transaction.

### Repository write contract

Replace mutation-oriented `SaveAsync(aggregate)` calls on singleton repositories
with unit-of-work-scoped staging:

```csharp
public interface ITaskRepository
{
    Task<TaskRequest?> GetAsync(
        RequestorId requestorId,
        TaskId taskId,
        CancellationToken cancellationToken);

    void Add(TaskRequest task);
    void Update(TaskRequest task);
}
```

The repository does not commit. `Add` and `Update` register changes with the
current operation. Reads return fresh aggregates and never shared mutable
instances.

### Optimistic concurrency statement

Every aggregate update uses its explicit expected version:

```sql
update tasks
set status = @status,
    updated_at = @updated_at,
    version = version + 1
where id = @id
  and requestor_id = @requestor_id
  and version = @expected_version
returning version;
```

Zero returned rows means the aggregate was deleted, belongs to another
requestor, or changed since it was read. The unit of work treats the operation
as conflicted and rolls back all of its changes.

Do not use `xmin` because it is a PostgreSQL implementation detail, can wrap,
and is awkward across export/import and some maintenance operations. Do not use
`SELECT FOR UPDATE` as the ordinary aggregate concurrency mechanism.

### Conflict policy

- Task submission relies on a unique idempotency key and returns the existing
  equivalent task or a stable key-reuse conflict.
- Scheduler assignment treats a version conflict as another evaluator winning;
  it skips that candidate and continues.
- Provider accept, reject, fail, complete, and rebind reload at most once when
  the command is safely repeatable. A stale handle or now-invalid state returns
  the existing protocol-specific conflict.
- Requestor cancellation reloads once. If the task is already terminal, it
  returns the existing `task_not_cancellable` result.
- Enrollment retries after reloading canonical capabilities and the execution
  unit. The unique capability-name constraint remains authoritative.
- Partner approval and revocation reload once and return current state if the
  requested transition is already satisfied.
- Infrastructure does not blindly retry an arbitrary delegate, because it may
  already have performed a non-database side effect.

## Transaction boundary by operation

### Submit task

1. Validate the request and capability contract.
2. If there is an input file, upload bytes to a unique staged S3 key first.
3. Start a database unit of work.
4. Enforce idempotency with the unique requestor/key constraint.
5. Insert the task, an `available` input artifact descriptor, the initial task
   event, and outbox messages.
6. Commit.
7. Notify scheduling through committed outbox work.

An S3 upload that is not adopted by a committed task is an orphan, not a task.
An outbox-driven sweeper deletes expired staged objects. S3 deletion never
decides whether the database transaction commits.

### Assign task

1. Query ordered queued candidates through the partial queue index.
2. Select a currently connected provider in process.
3. Load the task in a unit of work and apply `Assign`.
4. Update with the expected task version; insert the attempt, event, and
   assignment outbox record.
5. Commit, then deliver to the live provider.
6. If delivery fails, run a new compensating unit of work that requeues the
   attempt using the new expected version.

Two schedulers may select the same row, but only one versioned assignment update
can commit.

### Provider task transitions

Accept, reject, fail, timeout revocation, disconnect-expiry revocation,
requestor cancellation, and restart recovery each use one unit of work:

- load the task and active attempt;
- verify execution-unit ownership, handle, and expected state;
- apply the domain transition;
- update task and attempt with optimistic concurrency;
- append the audit event and outbox messages; and
- commit before changing connection-registry caches.

Transient disconnect and rebind may remain process-local only while that is an
explicit product decision. Durable revocation and the authoritative accepted or
terminal state remain in PostgreSQL.

### Upload and complete result

1. Token issue inserts an `authorized` upload operation with a token digest and
   expiry.
2. Token consumption uses a compare-and-swap update from `authorized` to an
   intermediate consumed/uploading state so it is one-time across replicas.
3. Result bytes are written to unique S3 keys.
4. A unit of work writes artifact descriptors and transitions the upload
   operation to `uploaded` with its receipt.
5. Completion loads the task and upload operation, verifies ownership and
   receipt, applies `Complete`, marks artifacts available, changes the upload
   operation to `completed`, appends audit/outbox rows, and commits atomically.
6. A repeated completion with the same attempt and receipt returns success.

A crash after S3 upload but before database staging leaves collectable orphan
objects. A crash after database staging but before completion leaves a durable
uploaded receipt that can be reconciled.

### Enrollment

One unit of work:

- loads canonical capability definitions;
- inserts new definitions under the unique normalized-name constraint;
- reloads if another enrollment won the name race;
- loads or creates the execution unit;
- replaces its enrollment using its persistence version;
- replaces execution-unit capability rows;
- appends the enrollment event and scheduler outbox message; and
- commits.

This removes `IEnrollmentGate` and `RepositoryLockRegistry` from production
correctness.

### Partner review

Submission, approval, and revocation each use a versioned unit of work.
Committed approval changes publish a bounded outbox message. The CORS
synchronizer reads the full currently approved set from PostgreSQL before
updating S3 CORS, so a missed process-local cache event is recoverable.

## Delivery units

### Unit 0: freeze the persistence contract

Deliver:

- Characterization tests for current task, attempt, enrollment, idempotency,
  partner-review, upload, completion, and startup-recovery behavior.
- A versioned inventory of every structured S3 record read or written by the
  current service.
- A signed-off data classification matching this plan's move/stay boundary.

Acceptance:

- Every current S3 transactional prefix has one declared PostgreSQL destination
  or an explicit derived/retired classification.
- Input and output object bodies are absent from database fixtures.
- Tests capture all public conflict and idempotency result codes.

### Unit 1: PostgreSQL infrastructure and schema

Deliver:

- Npgsql package and infrastructure adapter.
- Versioned SQL migrations checked into source control.
- Local PostgreSQL test composition.
- AWS RDS PostgreSQL resources in the current CloudFormation architecture:
  private subnets, encryption at rest, TLS, backups/PITR, a security group
  limited to the ECS service, and credentials in Secrets Manager.
- Database liveness/readiness checks and bounded connection-pool settings.

Acceptance:

- Migrations apply to an empty database and upgrade the previous schema.
- The API starts only when required schema compatibility is present.
- No secret value is logged or returned by diagnostics.
- S3 health remains required because artifact transfer still depends on it.

Exact live resource names must be resolved from current CloudFormation/ECS
configuration. Every AWS CLI or control-plane command used during delivery must
include `--profile ai-quinn`.

### Unit 2: operation unit of work and concurrency

Deliver:

- `IOperationUnitOfWork`, `IOperationContext`, and
  `PostgresOperationUnitOfWork`.
- Transaction-scoped mutation repositories.
- Explicit task, execution-unit, partner-request, and upload-operation
  persistence versions.
- Typed optimistic concurrency outcomes.
- Database outbox writer and dispatcher lease/claim logic.

Acceptance:

- Two writers loading the same task version cannot both commit.
- A conflict in any tracked aggregate rolls back attempts, artifacts, audit
  events, and outbox rows from that operation.
- Unit-of-work disposal rolls back an uncommitted transaction.
- Query readers cannot accidentally enlist and mutate an operation.

### Unit 3: PostgreSQL repositories and readers

Deliver:

- Task, queued-task, task-summary, admin-task, capability, execution-unit,
  provider-credential, partner-resource, upload, and artifact adapters.
- Direct indexed queries replacing S3 listing and projection reads.
- Aggregate mapping tests in both directions.

Acceptance:

- Hydrating then persisting an unchanged aggregate preserves all domain data.
- Queue and requestor ordering match current behavior.
- No repository performs an S3 list to answer a transactional query.
- Provider authentication performs an indexed digest lookup and stores no raw
  credential.

### Unit 4: application operation refactor

Deliver:

- Submission, enrollment, scheduling, provider transitions, cancellation,
  upload/completion, partner review, and startup recovery moved to the unit of
  work.
- Post-commit assignment delivery and compensating requeue.
- Post-commit scheduler, SSE, and CORS notifications through the outbox or an
  equivalent durable dispatcher.

Acceptance:

- Application handlers contain no manual connection or transaction handling.
- No state-changing handler calls an object-store transactional repository.
- A process crash after commit but before notification is recovered by the
  outbox.
- A process crash before commit publishes no authoritative notification.

### Unit 5: artifact boundary and reconciliation

Deliver:

- PostgreSQL artifact descriptors.
- Unique staged S3 object keys for input and output publication.
- Durable upload-token, receipt, and completion state.
- A bounded orphan-object reconciler with age and ownership safeguards.

Acceptance:

- Database rollback never deletes an object still referenced by a committed
  row.
- Completion replay for the same receipt is successful after restart.
- An uploaded but unstaged object is reported and later collectable.
- Presigned URLs are generated from a committed descriptor and current S3
  configuration, not stored in PostgreSQL.

### Unit 6: importer and verification

Deliver:

- A repeatable, resumable S3-to-PostgreSQL importer with a migration ledger.
- Importers for provider bindings, capabilities, execution units/enrollment
  history, tasks/attempts/events, partner resources, and artifact descriptors.
- A read-only verification report with row counts, IDs, statuses, versions,
  checksums, and discrepancy classifications.

Source rules:

- Resolve the application-data and provider-key buckets from current
  CloudFormation/ECS configuration.
- Read only AWS S3.
- Use the current `mutualgpu/v3` layout from code.
- Treat full task facts as current-state snapshots; use manifests only where no
  fact exists.
- Cross-check commit markers and attempt events and report inconsistencies
  rather than silently discarding them.
- Do not copy queue markers or task-summary projections; recompute and compare
  them with SQL query results.
- Do not download or copy artifact bodies. Validate descriptors with object
  metadata or narrowly scoped existence checks.

Acceptance:

- Re-running the importer creates no duplicate logical records.
- Every imported row records its source key or migration-ledger reference.
- Counts and state distributions match, with every difference explicitly
  classified.
- No migration output contains credentials, handles, tokens, presigned URLs, or
  raw user parameters.

### Unit 7: cutover

Use a controlled write pause rather than independent dual writes:

1. Deploy PostgreSQL infrastructure, schema, and application support dark.
2. Run the bulk import while S3 remains authoritative.
3. Enable maintenance mode for state-changing endpoints and stop new scheduler
   assignments.
4. Allow bounded in-flight requests to finish, then stop all transactional
   writes.
5. Run an incremental import from the migration ledger's watermark.
6. Run verification and require zero unexplained discrepancies.
7. Deploy PostgreSQL as the transactional source of truth.
8. Rebuild caches, reconcile active attempts under the existing restart policy,
   and reopen mutations.
9. Verify task queries, enrollment, provider authentication, scheduling,
   uploads, completion, partner approval, and artifact downloads.
10. Mark legacy S3 transactional prefixes read-only by application behavior;
    retain them for rollback.

Do not use application-level dual writes to S3 and PostgreSQL as two peers.
There is no atomic transaction across those stores, so such a design introduces
split-brain rather than reducing risk.

### Unit 8: observation, rollback, and retirement

During the observation window monitor:

- transaction latency and rollback rate;
- optimistic conflict rate by operation type;
- connection-pool saturation;
- queue depth and assignment collisions;
- outbox age, retries, and dead letters;
- staged upload age and orphan counts;
- task/status counts compared with the cutover baseline; and
- S3 and database readiness.

Rollback is a controlled migration, not a connection-string flip:

1. Re-enter maintenance mode and stop assignments.
2. Preserve a database snapshot.
3. Export post-cutover database changes into the legacy model with the reviewed
   rollback tool, or correct forward if export cannot preserve them.
4. Verify the export before restoring the legacy application.
5. Reopen writes only after one store is again authoritative.

After the agreed retention window and explicit approval:

- remove object-store transactional repository registrations and code;
- remove process-local repository locks and startup projection rebuilds;
- reduce S3 permissions to artifact prefixes only;
- remove the separate provider-key S3 dependency if no longer used; and
- schedule legacy transactional object deletion under a separately reviewed
  data-retention operation.

## Verification matrix

### Concurrency

- Two scheduler instances race for one queued task; exactly one attempt commits.
- Provider acceptance races requestor cancellation; only one legal terminal
  ordering commits and the loser receives a stable conflict.
- Completion races disconnect-expiry revocation; result and task state cannot
  disagree.
- Two enrollments introduce the same capability name with different contracts;
  one canonical definition owns the name.
- Two partner reviewers approve or revoke the same request; the version
  conflict cannot resurrect a revoked origin.

### Atomicity and failures

- Failure after task update but before attempt insert rolls back both.
- Failure after audit insert but before commit leaves no audit row.
- Failure after commit but before notification is recovered from outbox.
- Failure after S3 upload but before artifact staging produces no completed
  task and is visible to reconciliation.
- Failure after upload staging but before completion preserves a retryable
  receipt.

### Idempotency

- Repeated equivalent task submission returns the original task.
- Reusing an idempotency key for a different submission returns the existing
  stable conflict.
- Repeated result completion for the same attempt and receipt succeeds.
- A different receipt or stale handle cannot claim a completed attempt.
- Importer restart resumes without duplicates.

### Data and query parity

- Requestor task list and detail DTOs match pre-migration fixtures.
- Admin task ordering, attempt counts, failure fields, and result descriptors
  match.
- SQL queued-task ordering matches `Scheduling.Order`.
- Enrollment and capability catalogue behavior match.
- Every committed artifact descriptor resolves to the expected S3 object.

### Security

- Raw provider keys, task handles, upload tokens, receipts, presigned URLs, and
  scalar inputs are absent from logs and metrics.
- PostgreSQL is not publicly reachable.
- ECS receives only the database secret it needs.
- Migration and runtime roles have least-privilege S3 and database access.
- Artifact download authorization still verifies requestor ownership through
  PostgreSQL before issuing a scoped URL.

## Completion criteria

The migration is complete when:

- PostgreSQL is the sole writer and reader of structured transactional state;
- all state-changing application operations use
  `IOperationUnitOfWork`;
- every mutable aggregate has explicit optimistic concurrency;
- S3 contains and serves artifact bytes but receives no transactional JSON,
  queue marker, projection, commit marker, provider binding, or partner-review
  writes;
- durable upload and completion recovery work across service restarts;
- outbox lag and optimistic conflicts are observable;
- the migration verification report has no unexplained discrepancies; and
- legacy S3 transactional data is retained or removed according to an approved
  rollback and retention decision.
