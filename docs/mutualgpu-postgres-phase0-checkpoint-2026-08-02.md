# MutualGPU PostgreSQL cutover — Phase 0 checkpoint

Date: 2026-08-02
Scope: target-account read-only verification and local implementation only

This checkpoint supersedes unverified historical deployment assertions for the
facts listed below. It contains no secret values, secret identifiers, task
handles, tokens, presigned URLs, raw S3 object keys, or user identifiers.

## Boundary and identity record

- The configured login-session binding exactly matched the reviewed target
  automation identity.
- A fresh target-only STS read resolved account `428590861908` and the expected
  MutualGPU migration-admin assumed-role session in `us-east-1`.
- No `ai-quinn`/source-account call occurred.
- No AWS mutation occurred: no ECR, CloudFormation, S3, ECS, RDS, IAM, secret,
  maintenance, import, DNS, or cleanup operation was requested or performed.
- This cutover remains `aws-primary` only. No Backblaze/B2 configuration,
  diagnosis, initialization, or contact occurred.

## Live target checkpoint

| Gate | Observed target-only evidence | Status |
| --- | --- | --- |
| Foundation and service stacks | Both settled at `UPDATE_COMPLETE`. | Green |
| PostgreSQL | Private encrypted PostgreSQL 18.4 instance available; deletion protection on; single-AZ; seven-day retention. | Green for availability only |
| ECS service | One desired/running task, zero pending; primary rollout complete; both web and native gRPC target groups each have one healthy target. | Green |
| Public readiness | Anonymous HTTPS `GET /health/ready` returned HTTP 200. | Green at sampling time |
| Current release | Service task definition revision 2 uses a target-ECR image pinned by immutable digest. The candidate PostgreSQL settings and explicit write-target setting are not present in that live definition. | Red for PostgreSQL cutover readiness |
| Database secret | The target secret is present, not scheduled for deletion, and has exactly one `AWSCURRENT` version. No secret value or version identifier was read. | Green |
| Bucket baseline | Default encryption and public-access blocks are present. Bucket versioning is not enabled. | **Red** — D6 required before a deletion-capable release |
| Live browser policy | The deployed task explicitly allows arbitrary browser task-write origins. Bucket CORS is the existing permissive wildcard `GET`/`HEAD` read policy with `ETag` exposed; upload bytes still pass through the API. This behavior is intentional and must not change implicitly. | Recorded; D9 remains pending explicit production acceptance |

## Target S3 inventory

Only `mutualgpu/v3/` was listed, and only aggregates are recorded.

| Measure | Value |
| --- | ---: |
| Object count | 496 |
| Total bytes | 112,472,352 |
| Latest modification (UTC) | 2026-08-02T17:31:53Z |
| Opaque aggregate inventory SHA-256 | `78635bedb772b8e570cfe5c1e213e82e1a4f0044fb35e1e16fc001040756eaf0` |
| Capabilities | 4 |
| Commits | 120 |
| Nodes | 50 |
| Provider-key records | 23 |
| Queue records | 1 |
| Requestor task inputs | 18 |
| Requestor task results | 33 |
| Requestor facts | 120 |
| Requestor manifests | 21 |
| Other requestor-scoped records | 106 |

The hash is a canonical hash of the complete listed key/size/ETag/modified-time
inventory and is retained solely for freeze-pass comparison; no keys were
printed or persisted in this checkpoint.

## Remaining red gates

- D1–D9 remain **Pending**. Confirmation that the current permissive CORS
  behavior is intentional preserves it in the candidate; it is not an approval
  to change or tighten that policy.
- The private production database has not been queried. Consequently the
  queued/assigned/accepted/disconnected/uploading/staged counts, importer
  preflight, checksum verification, and outage estimate remain **Red** until a
  reviewed, target-only, read-only probe task/role exists.
- S3 versioning is disabled. Destructive reconciliation remains disabled and
  report-only by default; it is not eligible for activation.
- The one-off attested migration/verification runtime, independent maintenance
  ingress barrier, temporary observability, restricted candidate role, and
  restore/repoint rehearsal are cloud-dependent and remain **Red** pending
  reviewed support templates and explicit mutation approval.
- Existing target records are not evidence of PostgreSQL-import parity. No
  target data was copied, imported, altered, or verified against PostgreSQL.
- The 23 existing provider-key **digest bindings** have not yet been imported
  into PostgreSQL. Cutover remains Red until the target-only migrator reports
  exact digest-to-execution-unit and migration-ledger parity with zero
  discrepancies, followed by the restricted controlled-credential
  authentication check. Raw provider keys must never be read, copied, logged,
  or included in the report.

## Local implementation evidence

- The migrator now validates the complete provider digest-to-execution-unit map
  before import, treats malformed, duplicate, missing, changed, revoked, or
  ledger-inconsistent bindings as a nonzero-exit discrepancy, and independently
  verifies the database index after import and in read-only verification mode.
- Its JSON report contains aggregate discrepancy codes and counts only; it does
  not emit example identifiers, object keys, handles, provider-key digests, or
  raw provider keys.
- Local PostgreSQL 18 tests cover clean import plus authentication, resume with
  a missing binding, duplicate source bindings, digest mismatch, and revoked
  database bindings. These are implementation tests, not production import
  evidence.
