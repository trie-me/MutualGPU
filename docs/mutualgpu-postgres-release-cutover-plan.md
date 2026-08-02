# MutualGPU PostgreSQL release migration and go-live plan

- Status: **approved for Phase 0 read-only verification and local implementation;
  no AWS mutation, import, release, or cleanup is authorized**
- Prepared: 2026-08-02 (Europe/London)
- Local-worker dispatch approved by the user: 2026-08-02
- Plan marker: `mutualgpu.postgres_release_cutover_plan.v1`
- Production target: Quinn-MutualCompute AWS account `428590861908`, `us-east-1`
- Public site: `https://mutualgpu.com`
- Intended execution worker after approval: `gpt-5.6-terra`, reasoning effort `xhigh`

This is the release and cutover runbook for moving the already-live
Quinn-MutualCompute site from the structured-S3 application release to the
PostgreSQL application release. It complements, rather than replaces,
[`transactional-data-postgres-migration-plan.md`](transactional-data-postgres-migration-plan.md),
which defines the persistence design.

The plan is deliberately fail-closed. An unresolved condition is **RED**, not
implicitly acceptable. The user authorized a Terra worker to perform target-only
read-only Phase 0 verification and local implementation/testing. That dispatch
does not approve D1–D9 as production decisions and does not authorize an AWS
mutation, data import, source-account read, release, maintenance activation,
snapshot restore, public opening, or cleanup. Each later gate still requires the
review and approval stated below.

## 1. Intended outcome

After a successful cutover:

- PostgreSQL is the only system of record for structured transactional state:
  tasks, attempts, enrollments, capabilities, provider-key digests, partner
  reviews, artifact descriptors, upload receipts, audit events, and outbox rows.
- The existing target-account S3 bucket remains the system of record only for
  input and output artifact bytes.
- The public hostname, TLS certificate, ALB, WAF, requestor/provider APIs, and
  browser behavior remain compatible.
- The service runs the exact reviewed Linux ARM64 release by immutable ECR
  digest on the existing singleton ECS/EC2 deployment.
- Legacy structured S3 records are retained, not deleted, for the reviewed
  observation period.
- No application data is read or copied from the old `ai-quinn` account.

This is a binary red/green acceptance protocol, not parallel blue/green
infrastructure. The current fixed host ports and singleton `t4g.nano` force a
stop-before-start deployment; the old and new services cannot safely run side by
side on the existing host.

## 2. Current evidence state (must be repaired and reverified in Phase 0)

The shared branch advanced while this plan was being prepared. The
pre-correction review checkpoint was branch `alpha` at
`6d6b68441d472d1995211951a02f33310dbddda9`, one commit ahead of
`mutualgpu/alpha`, with a clean working tree. The root reviewer then made
intentional, uncommitted plan-only corrections to this document and the
multi-provider strategy; preserve them. The pre-work commit includes this plan,
the reviewed opt-in storage boundary, migration `0004`, additive
artifact-location repository changes, and tests. It is a starting point, not an
approved PostgreSQL release candidate.

The currently tracked
[`mutualgpu-quinn-mutualcompute-deployment-record.md`](mutualgpu-quinn-mutualcompute-deployment-record.md)
and
[`mutualgpu-quinn-mutualcompute-migration-handoff.md`](mutualgpu-quinn-mutualcompute-migration-handoff.md)
are stale: they still say no target cloud mutation occurred and no target stack
exists. Earlier task-local evidence reported a completed target deployment with
the following checkpoint:

- `mutualgpu-foundation` `UPDATE_COMPLETE` and `mutualgpu-service`
  `CREATE_COMPLETE`;
- ECS desired/running/pending `1/1/0` on one ARM64 `t4g.nano`;
- target image index
  `sha256:c2ab95cdfbee41b0bd47e94722df94f7fbb9b275d9d289a8dbb4ea4d131cf9d8`,
  rebuilt from structured-S3 commit
  `f96854d9429b398421fe15bbce89a8a741e1c769`;
- a private Single-AZ RDS PostgreSQL 18 `db.t4g.micro`, retained target S3
  bucket, ALB/WAF, and public `mutualgpu.com` cutover.

Those values are **unsupported pending Phase 0** because the task-local
continuation record that contained them is no longer present. Do not copy them
into an AWS mutation or release command. Phase 0 must resolve the live state
read-only, restore or replace the stale deployment evidence, and obtain review
before any target mutation.

The checked-in infrastructure currently describes a private Single-AZ RDS
PostgreSQL 18 instance, 20 GiB gp3 storage, seven-day backups, encryption,
deletion protection, an application pool of one to five connections, and a
retained encrypted S3 bucket without object versioning. These are template facts,
not proof of deployed state.

A production image must be built from a clean, reviewed release commit in a
separate detached worktree. The active shared checkout may contain this plan and
execution evidence; it is not required to be globally clean and must not be
reset, switched, or overwritten.

## 3. Non-negotiable boundaries

1. Every MutualGPU AWS CLI call names `--profile quinn-mutualcompute` or
   `--profile ai-quinn`; no default profile is permitted.
2. `quinn-mutualcompute-login` is authentication-only. After `aws login`, its
   `login_session` must be exactly
   `arn:aws:iam::428590861908:user/mutual-ai-automation`.
3. Before each target mutation batch, `quinn-mutualcompute` must resolve to
   account `428590861908` and ARN
   `arn:aws:sts::428590861908:assumed-role/MutualGPUMigrationAdmin/mutual-ai-automation`.
4. Region is fixed to `us-east-1`.
5. `ai-quinn` remains strictly read-only and out of scope for this cutover. This
   plan does not authorize reading or importing old-account PostgreSQL, S3,
   providers, tasks, approvals, or artifacts.
6. The only possible data-preservation path in this plan is **current target S3
   to current target RDS**, after explicit user approval.
7. This cutover's selected object-storage target is AWS S3 `aws-primary`.
   Backblaze/B2 must not be configured, initialized, or contacted during this
   cutover. Any later B2 use requires an explicitly selected, separately reviewed
   opt-in configuration.
8. Resource identifiers are resolved from live CloudFormation/ECS configuration,
   not inferred from legacy documentation or adapters.
9. Credentials, secret values, task handles, upload tokens, provider keys,
   session tokens, passcodes, and administrator passwords must never enter logs,
   tool output, plans, reports, commits, or chat responses.
10. DNS is unchanged by this release. No DNS mutation is planned.
11. Legacy S3 structured records and artifacts are not deleted by this cutover.
    Cleanup requires a separate reviewed retention operation.
12. No breaking public API change is released outside a major version. If a
    breaking change proves necessary, produce and approve a breaking-change
    notice and migration instructions before publication.

## 4. Decisions for review

The recommendations below are not approvals. Record the user's decision in the
last column before execution.

| ID | Decision | Recommendation | Alternative and implication | Approval |
| --- | --- | --- | --- | --- |
| D1 | Existing target data | Preserve current target structured S3 state with a target-only, one-shot import performed after a hard write freeze. | Start PostgreSQL empty only if all target providers, tasks, approvals, histories, and old artifact discoverability are explicitly disposable. Empty start is unsafe until orphan cleanup is disabled. | Pending |
| D2 | Availability during cutover | Accept a full write pause/site maintenance window. Use an ingress barrier independent of the task image, then scale ECS to zero. | A no-downtime cutover requires a tested convergent importer and a different deployment topology; neither exists. | Pending |
| D3 | Active work at freeze | Require zero assigned, accepted, or disconnected production tasks and zero in-flight result uploads. Queued tasks may remain only when exact queue/order parity is proven. | Importing active work has unproven reconnect/upload semantics and can strand accepted attempts. | Pending |
| D4 | Rollback after writes reopen | After the first operator-canary application write, recovery requires the tested post-import snapshot/endpoint-switch path or a correct-forward fix. After the first public write, use correct-forward only and never return to the structured-S3 image. | Rolling back to S3 discards or hides PostgreSQL writes unless a reverse exporter is first built and proven. | Pending |
| D5 | Observation window | Keep the legacy S3 records, pre-cutover snapshot, release image, and task definition for at least 48 hours and through one controlled restart. | A shorter window reduces recovery evidence; a longer window costs little but should have an explicit end date. | Pending |
| D6 | S3 deletion safety | Keep PostgreSQL orphan reconciliation disabled for the entire initial observation window and enable S3 versioning before any deletion-capable PostgreSQL release starts. | If versioning is declined, the cleanup kill switch remains mandatory because current S3 deletes are not recoverable. | Pending |
| D7 | Release compatibility | Preserve the existing no-query REST behavior on the server and make pagination opt-in; also paginate correctly in shipped clients/UI and test high-cardinality history. | Treat the endpoint behavior change itself as a major-version breaking change and publish migration guidance. | Pending |
| D8 | Release cleanup | Continue deferring ECS/ECR retention cleanup until the PostgreSQL release passes the observation window. | Cleanup during cutover removes recovery evidence and is not recommended. | Pending |
| D9 | Browser-origin policy | Explicitly decide the intended task-write origin rule and S3 CORS policy. Prefer an allow-list and idempotent CORS synchronization. | Retain arbitrary browser task writes and wildcard S3 `GET/HEAD` CORS only under a time-bounded, documented security acceptance. | Pending |

## 5. Data migration scope and implications

### 5.1 Data placement

| State | Cutover treatment | Important implication |
| --- | --- | --- |
| Target S3 task manifests/facts/attempt events | Import the latest canonical state into PostgreSQL after writers stop. | The current importer is safe only as a one-shot import into a known-empty destination unless it is made convergent. |
| Target S3 capabilities and enrollments | Import definitions, execution units, enrollment history, and provider-key digests. | Raw provider keys are never imported or logged. Existing provider authentication depends on complete digest/binding parity. |
| Target S3 partner-resource reviews | Import current approval/revocation state. | Missing approvals can change browser behavior and S3 CORS synchronization at startup. |
| Input/result/preview/metadata/log bytes | Leave bytes at their existing S3 keys; import PostgreSQL descriptors. | Do not copy, persist elsewhere, re-upload, or rewrite object bodies. A target-only read-only verifier may stream bodies to compute SHA-256; include the time/cost in the outage estimate. |
| Queue markers and requestor projections | Do not copy as authoritative rows; recompute from PostgreSQL and compare. | Membership **and ordering** must match. |
| Commit markers | Cross-check against imported members, then retain only as legacy evidence. | Any unaccounted member is RED. |
| Provider sockets, SSE connections, caches, advisory progress, admin sessions | Do not migrate; they are process-local. | Providers reconnect, progress can reset, and administrators must log in again after restart. |
| Legacy upload-token/staged-result state | Not currently migratable. | Freeze only with no in-flight upload; otherwise the operation must be explicitly abandoned/reconciled. |
| Old-account `ai-quinn` state | Excluded. | No read, list, import, copy, dump, restore, or synchronization is permitted. |

### 5.2 Encryption-key continuity

Imported task handles are encrypted before PostgreSQL storage. The importer and
the API must use the same existing target `taskHandleEncryptionKey`. Every stored
attempt is decrypted while hydrating task state, so rotating or losing this key
can make historical task reads fail. Do not rotate it as part of this release.

Database credentials remain target-generated. They are injected through ECS
secrets and must not be read into command output. The migration task should use a
dedicated read-only S3 task role and receive only the database secret and handle
key it needs.

Before import, record the deployment-secret ARN and the safe Secrets Manager
`AWSCURRENT` version ID for the task-handle key without reading its value. Recheck
the same ARN/version immediately before the migration task and candidate API
start. Any version change is RED until continuity is proven.

### 5.3 One-shot freeze requirement

The checked-in importer is resumable only for unchanged S3 objects. When a
previously imported key has a new ETag, it reports `source_changed_after_import`
and skips it; differing task or partner state becomes a destination conflict.
Therefore this plan does **not** use the old “bulk while live, then incremental”
sequence.

The approved path is:

1. Place an ingress maintenance barrier in front of every public path.
2. Terminate existing API tasks and provider streams by scaling ECS to zero.
3. Confirm running/pending tasks are `0/0` and target S3 has a stable aggregate
   inventory across two passes.
4. Import once into an empty, checksum-verified schema.
5. Run a separate verification pass and require zero unexplained discrepancies.

If the estimated one-shot import exceeds the approved outage window, stop. Do
not improvise a partial live import.

### 5.4 Authority and recovery boundaries

There is no reverse PostgreSQL-to-legacy-S3 exporter and no dual write. Record
three distinct boundaries:

1. **Post-import baseline:** schema/import writes exist in RDS, but no
   PostgreSQL application command has run. The old S3 release remains a safe
   application fallback while maintenance is closed because target S3 is still
   authoritative and unchanged.
2. **First operator-canary application write:** RG-6 creates deliberately
   disposable PostgreSQL rows and S3 artifacts. From this point, returning to
   the exact post-import baseline requires the separately rehearsed RDS
   snapshot-restore/new-endpoint/repoint procedure plus explicit classification
   of canary S3 objects. Otherwise correct forward.
3. **First unbounded/public application write:** returning to the legacy image
   makes public writes invisible and can create split-brain state. Recovery is
   to restore maintenance, preserve evidence, and correct forward.

Record timestamps and operation IDs for both application-write boundaries. The
operator must announce the public point of no return before opening traffic.

## 6. Release blockers that must be closed before production

All items in this section are RED today.

### B1. Production release provenance is not implemented

[`../scripts/build-and-push-quinn-mutualcompute-image.sh`](../scripts/build-and-push-quinn-mutualcompute-image.sh)
admits only the pinned historical `f96854d9` release.
[`../scripts/deploy-aws-service.sh`](../scripts/deploy-aws-service.sh) verifies
that release plus the incubation hotfix before creating or executing a service
change set.

Required closure:

- add a backward-compatible, release-generic path for an exact reviewed commit,
  immutable release tag, Linux AMD64/ARM64 index, source labels, and digest;
- preserve the existing identity, architecture, repository, change-set approval,
  and immutable-digest checks;
- reject dirty worktrees, mutable tags, unreviewed commits, and wrong accounts;
- emit a review sheet containing commit, tag, index digest, child digests, task
  definition, schema migration checksums, and image scan status.

### B2. The migrator targets the wrong account and has no production runtime

[`../tools/MutualGPU.TransactionalDataMigrator/Program.cs`](../tools/MutualGPU.TransactionalDataMigrator/Program.cs)
and
[`../scripts/import-transactional-data.sh`](../scripts/import-transactional-data.sh)
hard-code `ai-quinn`. The RDS endpoint is private, and the API image contains
only API publish output.

Required closure:

- create an explicit **target-only** mode guarded to STS account/role, account
  `428590861908`, region `us-east-1`, the exact CloudFormation-resolved bucket
  with expected bucket owner `428590861908`, and the exact reviewed RDS
  host/database/version;
- perform those attestations before opening a writable database connection or
  applying a schema migration;
- use the ECS task-role credential chain, never a source profile, in that mode;
- reject shared-profile/credential overrides in target-task mode;
- package the exact reviewed migrator in the release image or a separately
  pinned migration image;
- define a one-off ECS/EC2 migration task with no public ports, target-private RDS
  reachability, secret injection, CloudWatch logging, and a dedicated role with
  only List/Get on the reviewed target S3 prefixes plus narrowly scoped
  `cloudformation:DescribeStacks`, `rds:DescribeDBInstances`,
  `secretsmanager:DescribeSecret`, and STS identity access needed for the
  attestations above; it must have no S3 write/delete permission;
- alternatively supply immutable expected resource/secret-version metadata in a
  signed, operator-reviewed attestation manifest when an AWS describe permission
  is deliberately omitted;
- run it while the singleton API service is stopped so the `t4g.nano` has enough
  capacity and no S3 writer remains.

### B3. Discrepancies do not fail the migration process

The migrator currently serializes `HasUnexplainedDiscrepancies` but exits
normally. Required closure:

- version the report schema;
- return a nonzero exit code for any unexplained discrepancy, malformed source,
  schema/checksum mismatch, target drift, or partial verification;
- classify expected superseded snapshots separately;
- keep ordinary task logs count/code-only; capture a restricted detailed report
  with pseudonymized identifiers and without secrets, URLs, handles, tokens,
  keys, raw scalar inputs, or raw user/object identifiers;
- add importer tests for corruption, duplicates, interruption/resume, changed
  ETags, wrong account/bucket, and strict failure behavior.

### B4. Verification is incomplete

Current verification compares queue membership, not order, and checks artifact
existence/length, not full descriptor integrity. Required closure:

- compare every imported aggregate field and state distribution;
- compare queue order, not only membership;
- compare artifact key, role, direction, size, content type, SHA-256, owner, task,
  attempt, and availability state;
- for migration `0004`, require exactly one `aws-primary` location for every
  artifact, with object-key and lifecycle-state parity with the compatibility
  artifact row, every result upload write target equal to `aws-primary`, and zero
  missing, duplicate, unknown, or non-AWS target IDs;
- allow the verifier to stream target S3 bodies read-only to compute hashes, but
  never copy, persist, move, or rewrite them;
- prove every legacy input/result object is either represented by a committed
  descriptor or explicitly classified without enabling deletion;
- validate provider digest bindings, enrollment versions, partner approvals,
  pagination, audit/history expectations, and schema migration checksums;
- require zero unexplained discrepancies;
- run verification with a DB read-only identity/transaction and an S3 List/Get
  role, assert `transaction_read_only`, and independently compare source and
  destination rather than trusting only the import ledger or repair-capable code.

### B5. Orphan cleanup can delete legacy S3 artifacts

[`../src/MutualGPU.Infrastructure/Postgres/PostgresOrphanArtifactReconciler.cs`](../src/MutualGPU.Infrastructure/Postgres/PostgresOrphanArtifactReconciler.cs)
runs immediately. It deletes old `/inputs/` objects without PostgreSQL
descriptors and result objects associated with expired upload operations. It is
registered unconditionally whenever PostgreSQL is configured. The current
bucket has no versioning.

Required closure:

- add an explicit configuration/feature gate that defaults **off** for the first
  production PostgreSQL release;
- keep reconciliation disabled through migration, canary, restart, and the
  observation window;
- gate every expiration, collection, and deletion path and expose a safe runtime
  assertion showing that destructive reconciliation is off;
- add report-only inventory and tests proving legacy objects cannot be deleted
  while the gate is off;
- enable destructive reconciliation only in a later, explicitly approved
  retention operation after descriptor parity and recovery are proven.

### B6. There is no independent maintenance/write barrier

Scaling the service to zero stops writers but gives no safe way to validate the
new service before exposing it. ECS circuit-breaker rollback could also restore
the old S3 writer if the candidate fails.

Required closure:

- add a reviewable CloudFormation maintenance mode at ALB/WAF ingress that is
  independent of the API task definition;
- terminate existing connections by scaling the service to zero after the
  barrier is visibly active;
- support an exact temporary operator IPv4 `/32` allow path, or use an approved
  private test path, so the candidate can be tested through normal TLS and ALB
  routing while all other users receive a maintenance response;
- cover web, native gRPC, and WebSocket routes; a default HTTP action alone must
  not leave the existing gRPC listener rule public;
- add a machine-readable, unpaginated freeze probe that accounts for every task
  state, requires zero `authorized`, `uploading`, or `uploaded` result-upload
  operations, and separately requires zero `staged` artifacts; the current
  process-local legacy upload registry and one-page admin view are not
  authoritative freeze evidence;
- fail closed if the operator source address changes or the barrier cannot be
  proven externally.

### B7. Public list compatibility is unresolved

The PostgreSQL release defaults requestor task lists to 50 records and admin
lists to 100 records, exposing continuation through
`X-MutualGPU-Next-Cursor`. The shipped requestor UI, admin UI, and hosted browser
SDK currently fetch one page and ignore the cursor. Migrated users can silently
lose visible history.

Required closure:

- preserve the old no-query REST behavior at the endpoint and make pagination
  opt-in, while also consuming pages correctly in shipped clients/UI; otherwise
  release a reviewed major-version breaking change;
- add compatibility tests with more than 50 requestor tasks and more than 100
  admin tasks/pending/approved records;
- verify stable ordering, no duplicates, no omissions, and cursor replay.

### B8. PostgreSQL-backed host and migration coverage is incomplete

The main API integration suite explicitly selects the Development legacy path.
The opt-in PostgreSQL tests cover core transactions but not a real
PostgreSQL-backed HTTP host, import fixtures, real S3, or production restart.

Required closure:

- make `just mutualgpu-test` a mandatory gate and fail if any environment-gated
  PostgreSQL test is skipped;
- add a PostgreSQL-backed API integration suite;
- test empty schema and previous-schema upgrade on PostgreSQL 18;
- test target-format import fixtures, high-cardinality pagination, S3 descriptor
  parity, outbox retry/drain, startup failure/restart, graceful replacement, and
  abrupt task loss;
- use deterministic fixtures for more than 50/100 records; production gates test
  the highest-cardinality real principals actually present rather than creating
  artificial user history;
- prove accepted work cannot remain stranded after an abrupt process kill, or
  fix recovery before release;
- test representative and near-maximum 50 MiB result uploads inside the actual
  ARM64 `384 MiB` container limit.

### B9. Readiness and observability are insufficient on their own

`/health/ready` is a one-shot latch. A transient startup failure is logged once
without retry; after it turns ready it does not become unready when PostgreSQL or
S3 later fails. Application metrics have no configured exporter, Container
Insights is disabled, RDS enhanced monitoring is disabled, and no alarms or
outbox backlog metric exist.

Required closure:

- add or document bounded startup retry/restart behavior;
- add continuous dependency health suitable for the go-live observation gate,
  without exposing secrets;
- provide temporary cutover queries/metric filters for DB connections/timeouts,
  ALB/ECS errors, outbox age/retries, task conflicts, recovery activity, and S3
  deletion attempts;
- approve the numeric thresholds, sampling interval, evaluation window, evidence
  location, and responder in the RG-8 table before maintenance begins;
- do not call the release green solely because `/health/ready` returned 200.

### B10. The first PostgreSQL release has no compatible predecessor

Migrations auto-apply synchronously at API startup, are checksummed and
transactional, and have no down migrations. There is no previous PostgreSQL task
definition in production; the live predecessor is structured-S3. Required
closure:

- either build and pin a deliberately minimal PostgreSQL-compatible recovery
  image and test it against the exact migrated schema, or explicitly approve a
  correct-forward-only first release;
- never rely on CloudFormation/ECS rollback to undo a database migration;
- use expand/contract migrations for every later PostgreSQL release and test the
  immediately previous PostgreSQL image against the new schema;
- never roll back to the legacy structured-S3 image after an application write.

### B11. Database transport and browser-origin policy need explicit acceptance

The current connection uses `SslMode.Require`, which does not explicitly require
full server identity verification, and the API uses the generated database user.
The current service template also enables arbitrary browser task-write origins,
and candidate startup unconditionally writes an S3 CORS policy whose
implementation ignores approved origins and uses wildcard `GET/HEAD` CORS.

Required closure:

- either move to certificate-verifying TLS and a least-privilege DB runtime user,
  or record a time-bounded security acceptance;
- read-only snapshot the live bucket CORS policy and live task-write-origin flag;
- resolve D9, make CORS synchronization idempotent and explicitly configurable,
  and review any startup CORS mutation before candidate launch;
- test allowed, rejected, approved, and revoked origins through real API and
  browser presigned-object preflights.

### B12. The PostgreSQL runtime can still mutate frozen legacy prefixes

The current shared API task role can get, put, and delete every object under
`mutualgpu/v3/*`. Disabling the known reconciler is necessary but does not stop a
regression from modifying retained structured records.

Required closure:

- create a PostgreSQL candidate task role that can write only reviewed input and
  output artifact prefixes and cannot put/delete legacy capabilities, nodes,
  provider bindings, partner reviews, queue, projections, facts, attempts, or
  commit markers;
- keep the old role attached only to the old pre-public fallback task definition;
- give any approved CORS synchronization its narrow bucket-level permission
  without broadening object mutation;
- test denied writes/deletes against every frozen legacy prefix.

### B13. Snapshot recovery needs a tested endpoint-switch runbook

RDS does not restore a snapshot over an existing instance. Restore creates a new
instance and endpoint. Before relying on a snapshot for RG-6 recovery:

- rehearse restore to a new private instance, secret/handle-key continuity,
  security groups, schema verification, task-definition endpoint repoint, and
  controlled service restart;
- define how the old instance is preserved and how canary S3 objects are
  classified before the restored database serves traffic;
- record exact change-set/command templates without executing production restore;
- if this path is not proven, make RG-6 recovery correct-forward-only rather than
  claiming an immediate snapshot rollback.

### B14. Artifact-location pre-work is not yet cutover-safe

Commit `6d6b6844` adds migration `0004`, `artifact_locations`, and upload target
metadata. The forward migration and repository tests pass against PostgreSQL 18,
but the cutover verifier still ignores location rows, new writes can silently
default to AWS, task completion can manufacture an AWS location for a different
target, location-level updates can be lost or over-promoted, and a second
artifact read in one unit of work can discard a pending update. Runtime download
paths also still use the singleton AWS store and reconstructed keys.

Required closure for this AWS cutover:

- explicitly scope the release to the `aws-primary` compatibility slice; fail
  startup, import, and verification on any other target ID without initializing
  or contacting B2;
- retain the metadata-only AWS backfill, but require every new upload and
  location to name an allow-listed target explicitly rather than relying on a
  database, domain, or repository fallback;
- make task completion target-consistent and either implement explicit
  per-location lifecycle/concurrency semantics or reject multi-location mutation
  until those semantics exist;
- preserve tracked mutations across repeated repository reads and add a
  load-update-reload-commit regression test;
- update importer summaries and independent verification with the B4 location
  parity gates, including a deliberately corrupted-location failure test;
- test empty-schema startup through the production data-source factory, migration
  idempotency/checksums, positive AWS reconciliation, and every default-off
  cleanup path;
- before destructive reconciliation is ever enabled, bind `aws-primary` to an
  attested immutable provider/account/bucket/region identity and fail closed on
  drift;
- record under B10 that a three-migration PostgreSQL image rejects the
  four-migration schema; this first release has no implicit PostgreSQL binary
  rollback.

## 7. Implementation sequence

### Phase 0 — Reverify and approve the plan

Read `AGENTS.md`, this plan, the account-migration handoff, the deployment
record, and the transactional migration design in full. Treat the two historical
deployment documents as stale until reconciled with live read-only evidence.
Then:

1. Run `git status --short --branch` and preserve all existing work.
2. Verify the login profile binding.
3. Read-only verify target account/ARN/region, stack states, parameters, outputs,
   current task definition, exact image digest, target groups, RDS status/version,
   target bucket, bucket versioning/CORS, task-write-origin setting, and secret
   ARNs/version IDs without reading secret values.
4. Read-only inventory only the **target** `mutualgpu/v3` prefixes. Record counts,
   total bytes, state distributions, latest modification time, and a salted or
   opaque aggregate inventory hash; do not print object keys or user identifiers.
5. Record queued/assigned/accepted/disconnected/uploading counts and estimate the
   one-shot outage duration.
6. Reconcile the stale deployment/handoff records or create a new authoritative
   live checkpoint with the verified values.
7. Present D1–D9 and every security acceptance with the reconciled evidence;
   retain their `Pending` state until the user gives explicit production
   decisions.
8. Record the Phase 0 checkpoint and distinguish gates that block cloud
   progression from work that can be completed and tested locally.

Any identity mismatch, unexpected stack drift, source-account dependency,
unreviewed data scope, or unresolved decision is RED for cloud progression; it
does not prevent safe local blocker implementation.

### Phase 1 — Close code, safety, and compatibility blockers

Implement the local portions of B1–B14 as backward-compatible changes. Leave
cloud-dependent validation and rehearsals visibly pending. At minimum, deliver:

- generic immutable release build/deploy provenance;
- target-only one-off migrator task and IAM;
- strict migration/verification failure semantics and tests;
- orphan-cleanup kill switch defaulted off;
- independent ingress maintenance mode;
- pagination compatibility;
- PostgreSQL-backed API/import/restart coverage;
- restricted PostgreSQL runtime S3 IAM plus local snapshot endpoint-switch
  runbook/templates/tests, with the cloud rehearsal pending approval;
- temporary go-live observability and a production canary orchestrator.
- AWS-only artifact-location parity, fail-closed target selection, and repository
  regression coverage for migration `0004`.

Do not mutate AWS application infrastructure in this phase. Local tests may use
the repository PostgreSQL 18 composition.

### Phase 2 — Freeze and prove the release candidate

1. Select a clean reviewed commit and immutable release tag.
2. Build from a detached clean worktree.
3. Run `just mutualgpu-test`; verify PostgreSQL tests ran rather than skipped.
4. Run importer, upgrade, pagination, crash/restart, and ARM64 memory tests.
5. Diff public contracts/protobuf/SDK behavior against the deployed release.
6. Build Linux AMD64/ARM64 image(s) locally and verify labels/children.
7. Before any ECR push or change-set creation, reverify target identity and
   obtain explicit approval for that mutation batch; image publication and
   change-set creation are target mutations.
8. Push the approved immutable image, review ECR scan, and pin the exact index
   digest.
9. Create but do not execute separate CloudFormation change sets for inert
   maintenance/migration/observability support and for the reviewed service image.
10. Present exact change-set ARNs and resource changes for user approval.

### Phase 3 — Install inert support and establish the reversible baseline

After fresh target identity verification and explicit mutation approval:

1. Execute only the approved support change set while maintenance remains off,
   the old task definition remains live, and desired count remains one. It may
   install the inactive maintenance control, migration/verification task
   definitions and roles, restricted candidate runtime role, freeze probe, and
   temporary observability. It must not run the migrator or change traffic/data.
2. Prove the live service, image, routing, CORS, desired count, and user behavior
   are unchanged after support installation.
3. Enable S3 versioning if D6 approves it; verify status.
4. Create a manual pre-import RDS snapshot; wait for `available`.
5. Record the exact old task definition/image and pre-public fallback change set
   without executing it.
6. Confirm orphan reconciliation is disabled in every candidate task definition.
7. Confirm migration and verification roles cannot put/delete S3 objects, and
   the candidate runtime role cannot mutate frozen legacy prefixes.
8. Confirm the B13 restore/repoint rehearsal passed or record correct-forward-only
   recovery.
9. Confirm legacy release retention cleanup remains disabled.

### Phase 4 — Freeze the target S3 authority

1. Activate the independent maintenance barrier and verify it from a
   non-operator client on web, gRPC, and WebSocket paths.
2. Verify the operator-only path, if used, is exactly the approved `/32`.
3. Require the D3 active-work gate.
4. Update ECS desired count to zero through an approved change set.
5. Wait for ECS running/pending `0/0`, both target groups drained, and all old
   streams terminated.
6. Run two target S3 inventory passes after shutdown and require identical
   aggregate counts/hash. Any change is RED.
7. Record the freeze timestamp. No legacy writer may restart after this point
   unless the cutover is explicitly abandoned.

### Phase 5 — Import and verify

1. Run a read-only parse/reference/classification preflight over the frozen S3
   manifest before the first destination write.
2. Require an empty destination restored from the approved baseline. Resume is
   allowed only when the frozen manifest matches every ledger key/ETag and every
   already-written row is independently equivalent; otherwise restore/recreate
   and restart the one-shot import.
3. Inside the migration task, attest STS account/role, region, bucket/owner, RDS
   host/database/version, and secret version IDs **before** opening a writable DB
   connection or applying schema.
4. Run the exact reviewed one-off migrator by immutable digest.
5. Require ECS task exit code zero and a complete, schema-versioned report.
6. Run a separate verification task with read-only DB and S3 identities and
   assert the database transaction is read-only.
7. Require zero unexplained discrepancies and all parity tests in B4.
8. Create a post-import, pre-application-write RDS snapshot; wait for `available`.
9. Preserve both reports and snapshot identifiers in the deployment record.

Do not start the API when verification is incomplete.

### Phase 6 — Deploy GREEN behind maintenance

1. Execute the approved service change set with the exact candidate digest and
   desired count one while public maintenance remains active.
2. Allow ECS circuit-breaker rollback only during this pre-public phase.
3. Reverify handle-key secret ARN/`AWSCURRENT` version and candidate runtime role.
4. Require one stable primary deployment, desired/running/pending `1/1/0`, exact
   task definition/image/ARM64 architecture, and both ALB target groups healthy.
5. Verify schema checksums, S3 health, PostgreSQL health, startup recovery, and
   five consecutive ready checks over at least one minute.
6. Run the RG-0 through RG-6 protocol below through the operator-only path.
7. Record the first operator-canary application write timestamp/operation ID.
8. Keep public users behind maintenance until all pre-open gates are green.

### Phase 7 — Open public writes

1. Announce that removing maintenance establishes the unbounded/public
   PostgreSQL point of no return; the operator-canary boundary is already recorded.
2. Execute the small approved change set that removes maintenance/operator-only
   ingress without replacing the tested task definition.
3. Record the exact opening time and first accepted **public** PostgreSQL
   operation.
4. Run RG-7 public tests immediately.
5. On any RED, restore maintenance and correct forward; do not restore the
   structured-S3 writer.

### Phase 8 — Observe and hand off

Run RG-8 for the D5 observation window. Keep:

- legacy target S3 structured records;
- S3 versions if enabled;
- pre-import and post-import RDS snapshots;
- the old legacy image/task definition as evidence, not an automatic rollback;
- the new image/task definition and migration reports.

Do not run ECS/ECR retention cleanup, S3 cleanup, orphan reconciliation, schema
contract cleanup, or additional privilege reduction as part of initial go-live.

## 8. Binary red/green go-live protocol

For every gate, record timestamp, operator, exact release digest, evidence
location, and result. There is no amber state.

### RG-0 — Identity and provenance

**GREEN**

- Target account, role ARN, region, stacks, RDS, bucket, ECS service, and ALB
  match the reviewed sheet.
- Candidate commit/tag/index digest/child digest/task definition/migration
  checksums match the approved immutable release.
- The detached release worktree and build inputs are clean and reproducible. The
  shared checkout may contain this plan or unrelated preserved work.

**RED**

- Any mismatch, mutable reference, uncommitted release input, wrong profile,
  unexpected resource, or source-account dependency.

**RED action:** stop without mutation.

### RG-1 — Build, compatibility, and security

**GREEN**

- All .NET, frontend, SDK, PostgreSQL, importer, schema-upgrade, pagination,
  restart, and ARM64 resource-limit tests pass with zero skips in required suites.
- Existing public contracts remain compatible, including histories over 50/100
  records and the hosted browser bundle.
- No secret or sensitive capability value appears in image layers, logs, reports,
  metrics, or test artifacts.
- Image scan has no unreviewed critical/high finding.

**RED:** any failure, required skip, truncation, breaking change without a major
release notice, secret exposure, or unreviewed scan finding.

**RED action:** do not publish or deploy; fix locally.

### RG-2 — Write freeze and rollback baseline

**GREEN**

- Maintenance is externally proven for ordinary clients across HTTP, gRPC, and
  WebSocket paths.
- ECS is `0/0`, target groups are drained, the unpaginated freeze probe reports
  no assigned/accepted/disconnected task, zero `authorized`, `uploading`, or
  `uploaded` result-upload operations, and zero `staged` artifacts; queued-task
  ordering is recorded, and two post-stop target S3 inventories are identical.
- Pre-import snapshot is available and the exact old release evidence is saved.

**RED:** any continuing writer/stream, changing S3 inventory, active-work policy
violation, missing snapshot, or unproven maintenance path.

**RED action:** keep maintenance on; either drain/fix or abandon before import.

### RG-3 — Import and parity

**GREEN**

- Target-only migration task exits zero.
- Schema identity/checksums match the release.
- An independently read-only verification task asserts a read-only DB
  transaction and exits zero with `hasUnexplainedDiscrepancies=false`.
- Aggregate fields, counts, statuses, versions, provider bindings, enrollments,
  partner reviews, queue order, and artifact descriptors/checksums match.
- Every artifact has exactly one `aws-primary` location matching its compatibility
  key/state, every upload write target is `aws-primary`, and there are zero
  missing, duplicate, unknown, or non-AWS target IDs.
- Every legacy object is represented or explicitly retained/classified; no S3
  object was deleted, copied, moved, or overwritten.
- Post-import/pre-application-write snapshot is available.

**RED:** any partial report, mismatch, conflict, missing object/descriptor,
unexpected DB row, S3 mutation, or nonzero task exit.

**RED action:** do not start GREEN. Preserve reports and diagnose. Because public
writes remain closed, restore/replace the database only under a separately
reviewed action or return to the old release.

### RG-4 — Infrastructure and dependency health

**GREEN**

- ECS has one completed primary deployment at `1/1/0` on the exact ARM64 digest.
- Web and gRPC target groups report only healthy targets.
- TLS hostname/certificate/chain, HTTP-to-HTTPS redirect, WAF, security groups,
  private RDS reachability, S3 endpoint access, and task credential/IMDS isolation
  pass.
- `/health/live` and `/health/ready` pass five times over at least one minute,
  plus independent continuous S3/PostgreSQL checks pass.
- Logs contain no migration checksum, startup recovery, authentication,
  credential-chain, timeout, pool, or unhandled errors.

**RED:** wrong digest/architecture, unstable deployment, unhealthy target,
one-shot readiness without independent health, dependency failure, or secret in
logs.

**RED action:** keep maintenance on. Pre-public circuit-breaker rollback may
return to the old image; verify that no PostgreSQL user write occurred.

### RG-5 — Historical read parity

**GREEN**

- A sampled and boundary-complete set of migrated requestors can list every task,
  page through history, open task detail, see attempts/failure fields, and
  retrieve authorized results.
- Admin login/overview/logout works; all tasks and provider bindings are visible
  without secret handles/keys/tokens/URLs in the response.
- The highest-cardinality real requestor/admin/partner histories show no omission
  or duplication. Deterministic >50/>100 coverage has already passed RG-1 even
  when production has no principal at those cardinalities.
- A specifically controlled imported provider credential authenticates. Every
  other binding is verified by digest/identity parity; capability and enrollment
  versions match.
- Partner-origin/API/S3 CORS behavior matches approved D9.
- A wrong requestor cannot obtain a result URL from the API. A URL issued to the
  owner downloads the expected size/SHA-256 and is treated as a bearer secret
  until expiry; S3 cannot identify a MutualGPU requestor cookie.

**RED:** hidden/lost history, pagination error, auth failure, wrong ownership,
descriptor mismatch, bad download, CORS regression, or sensitive response.

**RED action:** keep maintenance on and correct import/application behavior.

### RG-6 — Operator-only new-write canary and restart

Use a dedicated canary capability/identity and a protected credential source.
Never print its key. Record created IDs only in the restricted execution record.

**GREEN**

1. Provider enrollment and both native gRPC and browser WSS connect paths pass.
2. Requestor submits a task with an input artifact and idempotency key.
3. Repeating the same submission returns the same task; changing the payload
   under the same key returns the stable conflict.
4. Provider receives, accepts, reports progress, downloads and hashes input,
   uploads ZIP plus preview/metadata/log artifacts, and completes.
5. Replaying the completion receipt succeeds; reusing/wrong token, handle,
   or provider key is rejected with the expected stable code. A wrong requestor
   cookie cannot obtain task detail/result authorization.
6. Requestor list/detail/SSE/result and every S3 download pass size/hash checks.
7. Outbox drains: no available unprocessed row is older than 30 seconds after the
   canary settles; no retry loop remains.
8. No legacy structured S3 prefix receives a post-freeze write. Only expected
   input/output artifact keys are added.
9. Orphan reconciliation remains disabled and S3 object count has no unexpected
   decrease.
10. Record the first operator-canary application write timestamp/operation ID.
11. After the first canary completes, create a second canary and hold it in the
    accepted state. Restart the candidate **once**. The completed canary, result,
    idempotency replay, provider authentication, admin view, and downloads must
    survive; the provider must reconnect/rebind the held attempt and complete it.
    Any stranded accepted attempt is RED.

**RED:** failure of any lifecycle or negative-authorization assertion, outbox
backlog/retry, legacy structured write, unexpected S3 deletion, stranded work,
restart data loss, OOM, or secret leakage.

**RED action:** keep maintenance on and correct forward. If B13 was rehearsed and
explicitly approved, restore the post-import/pre-application-write snapshot to a
new RDS instance, repoint the service, and classify canary S3 objects. Snapshot
restore is not an in-place immediate action. Do not expose public traffic.

### RG-7 — Public opening

**GREEN**

- Maintenance is removed without changing the tested task definition.
- Public homepage, static assets, provider page, CSP/security headers, TLS,
  readiness, admin login/logout, REST, SSE, native gRPC, browser WSS, CORS, and a
  small full task lifecycle pass through ordinary public resolution.
- The first **public** PostgreSQL operation timestamp/ID is recorded and no
  legacy structured S3 write appears after it.
- ALB errors/latency stay within the RG-8 numeric thresholds and WAF behaves as
  reviewed.

**RED:** public routing mismatch, any protocol failure, unexpected 5xx, wrong
image, legacy write, or material difference from operator-only behavior.

**RED action:** immediately restore maintenance, record the accepted-write
boundary, and correct forward. Do not reactivate the S3 release.

### RG-8 — Observation window

**GREEN** throughout the D5 window

The execution worker is the initial responder and records sanitized CloudWatch,
SQL, dependency-probe, and S3 evidence in the dated execution record. Any RED
immediately pages/tags the user, restores maintenance, and starts evidence
preservation. Approve replacements before maintenance begins if these defaults
do not fit the verified baseline.

| Signal | Sample/evidence | GREEN | RED |
| --- | --- | --- | --- |
| ECS service | ECS/CloudWatch every 60 seconds | desired/running/pending `1/1/0`; no OOM stop | Any OOM; or deviation for two consecutive samples outside the one planned restart |
| Web and gRPC targets | ALB every 60 seconds | healthy `1`, unhealthy `0` in each group | Any unhealthy target for two consecutive samples |
| Continuous S3/PostgreSQL probe | Dedicated probe every 30 seconds | Both pass | Two consecutive failures |
| ALB target 5xx | CloudWatch one-minute sum | `0`, excluding an exact recorded negative canary request | Any unexplained target 5xx |
| ALB target latency | CloudWatch p95 each minute | Under 2 seconds and no more than 2× the approved pre-open baseline | Breach for five consecutive samples |
| PostgreSQL connections/pool | Role-scoped SQL, RDS, and app logs every 60 seconds | Application-role connections at or below the configured pool maximum of 5; total RDS connections at or below 80% of `max_connections`; no pool wait/command timeout | Application role above 5 or total above 80% for two samples, or any pool wait/command timeout |
| RDS capacity | CloudWatch every 60 seconds | CPU below 80%, freeable memory at least 128 MiB, free storage at least 4 GiB | CPU/memory breach for five samples, or storage breach once |
| Outbox | Read-only SQL every 60 seconds | No available unprocessed row older than 30 seconds; attempt count below 6; zero pending within 60 seconds of quiet | Age/attempt breach for two samples or failure to drain after quiet |
| Task invariants | Read-only SQL every 5 minutes | Zero duplicate active attempts; zero accepted/disconnected task without a live/recovering provider beyond 180 seconds | Any violating row |
| Frozen S3 prefixes | Target-only inventory hourly | Zero new/changed structured legacy keys and zero delete markers/deletions after freeze | Any unapproved structured write, overwrite, delete marker, or deletion |
| Error/security logs | Continuous metric filters | Zero migration, checksum, credential, unhandled, OOM, deletion-attempt, or secret-leak events | Any matching event |

- Every table row remains GREEN.
- Task/status deltas reconcile to known traffic and canary IDs.
- A daily historical-read check and small canary lifecycle pass.

**RED:** any table threshold breach, unexplained task-count delta, data mismatch,
legacy write/deletion, or security regression.

**RED action:** enter maintenance and correct forward. Extend the observation
window after the fix.

## 9. Rollback and recovery matrix

| State | Safe response | Forbidden/default-unsafe response |
| --- | --- | --- |
| Before maintenance | Make no change; reschedule. | Starting any import or schema mutation without approved baseline. |
| Maintenance active, before import | Restore ingress and old service if the window is abandoned. | Leaving partial infrastructure drift unexplained. |
| Import complete, before any PostgreSQL application write | Keep maintenance; old S3 release may be restored because S3 is still authoritative. Retain or restore RDS only through an approved action. | Treating an import report with discrepancies as usable. |
| Operator-only canary writes | Keep maintenance and correct forward. Only if B13 was rehearsed and separately approved, restore the post-import/pre-application-write snapshot to a new RDS instance, repoint the service, and classify every canary-created S3 object before retrying. | Treating an RDS snapshot as an in-place rewind, or opening public traffic after a failed canary. |
| Public PostgreSQL writes accepted | Re-enter maintenance, snapshot/preserve evidence, and correct forward or use a previously proven PostgreSQL-compatible recovery image. | Returning to the legacy S3 writer, running an unproven reverse conversion, or accepting silent data loss. |
| Observation complete | Continue PostgreSQL; schedule separate cleanup/security/retention plans. | Deleting legacy S3 or snapshots merely because the window elapsed. |

Automatic ECS/CloudFormation rollback is not a data rollback. It may change an
image, but it cannot undo schema or convert PostgreSQL writes back into the
legacy object model.

## 10. Evidence and execution record

Create a dated execution record under `docs/` before production work and append,
without secrets:

- approved D1–D9 decisions and security acceptances;
- reconciliation of the stale handoff/deployment records against the Phase 0
  live read-only inventory;
- target identity/region and resolved resource identifiers;
- old and new commits/tags/image indexes/child digests/task definitions;
- CloudFormation change-set ARNs and reviewed diffs;
- the inert support-stack deployment, maintenance control, candidate runtime
  role, and exact secret ARN/version identifiers used by the task definition;
- schema migration names/checksums;
- S3 inventory counts/hash before freeze and after each gate;
- active task/upload counts;
- RDS snapshot identifiers and statuses;
- the B13 snapshot-restore/new-endpoint/repoint rehearsal result;
- migration/verification report hashes and sanitized summaries;
- RG-0 through RG-8 evidence/results;
- RG-8 threshold samples and any explicitly excluded negative-test requests;
- maintenance/freeze/open timestamps;
- first operator-canary application write and first public application write
  timestamps/IDs;
- the approved browser task-origin/CORS policy and its security-test evidence;
- every RED event, response, waiver, and user approval.

Never include secret values, raw keys, task handles, tokens, presigned URLs,
provider passcodes, administrator passwords, or raw user scalar parameters.

## 11. Terra xhigh execution handoff

The user approved Phase 0 target-only read-only verification and local
implementation on 2026-08-02. This is not approval for an AWS mutation or any
later production gate. The worker's starting instruction should include:

> Use `gpt-5.6-terra` with `xhigh` reasoning. Read `AGENTS.md`,
> `docs/mutualgpu-postgres-release-cutover-plan.md`,
> `docs/mutualgpu-quinn-mutualcompute-migration-handoff.md`,
> `docs/mutualgpu-quinn-mutualcompute-deployment-record.md`, and
> `docs/transactional-data-postgres-migration-plan.md`, and
> `docs/multi-provider-artifact-storage-strategy.md` completely. Preserve the
> shared checkout and build release artifacts only from a clean detached
> release worktree. Treat handoff/deployment claims as stale until Phase 0
> target-only read-only AWS evidence reconciles them. Record D1–D9 for later
> production approval and implement/test the local portions of B1–B14, leaving
> cloud-dependent validation explicitly pending. For this cutover, select only
> `aws-primary`; do not configure, initialize, or contact Backblaze/B2. Never
> access old-account application data. Use `ai-quinn` only if a newly reviewed
> plan explicitly authorizes a minimum descriptive read; otherwise make no
> source-account call. Present local test evidence and exact proposed template
> diffs/parameters first. Before ECR push or CloudFormation change-set creation,
> pause for explicit target-mutation approval. After creation, present exact
> image digests and change-set ARNs for separate execution approval. Do not
> accept the first public PostgreSQL application write until RG-0 through RG-6
> are green and the user approves the point of no return.

The worker may perform target-only read-only Phase 0 checks and implement/test
local changes autonomously within this plan. It must pause for user review before
any source read, new data scope, target mutation batch (including ECR push or
change-set creation), secret-handling exception, maintenance activation, import,
snapshot creation/restore, public opening, or destructive cleanup.
