# MutualGPU Quinn-MutualCompute deployment record

- Status: Phase 0 read-only verification and Phase 1 local implementation
- Observed: 2026-07-31T00:02:40Z
- Target account: `428590861908`
- Target region: `us-east-1`
- Repository branch: `alpha`
- Repository starting commit: `f1042bb04aa6ccc13c68eb21b6aa36640cd7efc3`
- Cloud mutation status: none

## Clean-start boundary

This deployment creates an empty target data plane. It does not migrate, copy,
restore, import, synchronize, or otherwise transfer source PostgreSQL data, S3
objects, provider identities, tasks, approvals, audit history, secret values, or
application state. It creates no source-to-target transfer policy, database path,
dump job, import job, or synchronization process.

The `ai-quinn` account is strictly read-only. It is used only to identify the
currently deployed image and non-secret runtime configuration. Backblaze/B2 is
not part of MutualGPU and must never be accessed.

## Repository preservation

The initial working tree contained:

```text
 M AGENTS.md
?? docs/mutualgpu-quinn-mutualcompute-migration-handoff.md
?? docs/mutualgpu-source-resource-manifest.json
?? docs/mutualgpu-source-resource-teardown-manifest.md
```

The two source-resource manifests were inspected and preserved unchanged. They
are dated discovery/teardown aids, not authorization to migrate source data or
tear down the source account. Any teardown wording that refers to data migration
is superseded for this deployment by the clean-start boundary above.

## Verified identities

| Purpose | Verified value |
|---|---|
| Target login binding | `arn:aws:iam::428590861908:user/mutual-ai-automation` |
| Target account | `428590861908` |
| Target operation ARN | `arn:aws:sts::428590861908:assumed-role/MutualGPUMigrationAdmin/mutual-ai-automation` |
| Source account | `351791602105` |
| Source read-only ARN | `arn:aws:iam::351791602105:user/ai_quinn` |

The target migration role trusts only the reviewed automation user, but its
trust policy does not currently include an MFA condition. The automation user
and migration role currently have no ownership tags. These are review findings;
no IAM changes were made.

## Live source discovery

| Identifier | Live value |
|---|---|
| Foundation stack | `arn:aws:cloudformation:us-east-1:351791602105:stack/mutualgpu-foundation/0eb89e80-84c5-11f1-b2b6-0affca082aef` |
| Service stack | `arn:aws:cloudformation:us-east-1:351791602105:stack/mutualgpu-service/7aa6a0f0-84df-11f1-bd32-12db278e9a53` |
| Source VPC | `vpc-0ddcf2b09606ad7b2`, `10.42.0.0/16` |
| ECS cluster | `arn:aws:ecs:us-east-1:351791602105:cluster/mutualgpu-demo` |
| ECS service | `arn:aws:ecs:us-east-1:351791602105:service/mutualgpu-demo/mutualgpu-api` |
| Active task definition | `arn:aws:ecs:us-east-1:351791602105:task-definition/mutualgpu-api:53` |
| Active service shape | Fargate, desired `1`, running `1`, `awsvpc`, public task IP |
| Image URI | `351791602105.dkr.ecr.us-east-1.amazonaws.com/mutualgpu-api@sha256:519b123233e0940c5f4b579e119ec8de3d4e168b6385af19b1c15d9f9d97ac31` |
| Image tag | `cors-all-origins-20260727-r2` |
| Application Git commit | `f96854d9429b398421fe15bbce89a8a741e1c769` |
| Linux AMD64 child | `sha256:d9108af687fabc6fd308dbf9ef0d08cc3862ee30ed17074604d565f767261355` |
| Attestation child | `sha256:8437e5cf064df5440508282d33b3f73d2a3d3dbc6aadb8989574aa070fd332db` |
| Linux ARM64 child | absent |
| Public hostname | `mutualgpu.com` |
| Source ALB | `mutualgpu-demo-1634136555.us-east-1.elb.amazonaws.com` |
| Provider enrollment WAF rate | `10` requests per source IP per minute |

The second index child is explicitly annotated as
`vnd.docker.reference.type=attestation-manifest` and references the AMD64 child.
It is not an ARM64 image. The active task also has legacy structured-S3
configuration and no `MutualGPU__Postgres__*` configuration. Source Link metadata
inside the live application maps the MutualGPU source to exact Git commit
`f96854d9429b398421fe15bbce89a8a741e1c769`.

That S3-exclusive application is the required first target release. It must be
rebuilt from the exact clean commit above as a Linux AMD64 plus Linux ARM64 OCI
index because the live index lacks ARM64. It keeps tag
`cors-all-origins-20260727-r2`; its new target index digest is expected to differ
from the source digest because a second runtime architecture is added. No build
or push has occurred. The source digest remains recorded as immutable
provenance, and RDS remains in the target infrastructure for a future
PostgreSQL application release.

The live non-secret provider origins are:

```text
https://huggingface.co
https://vercel.com
https://yosun-triposplat-webgpu-demo.static.hf.space
```

Public DNS is an apex record. Its authoritative nameservers are:

```text
launch1.spaceship.net
launch2.spaceship.net
```

At observation time, `mutualgpu.com` resolved to the same two addresses as the
source ALB. The exact Spaceship account/zone identifier and change mechanism
remain a user confirmation. All certificate-validation and application-record
DNS changes are manual user operations: Codex will present the exact values,
pause, and tag the user rather than operating Spaceship through Brave or an API.

## Target availability and name checks

The target has no MutualGPU stacks, RDS identifier, ECS cluster, ECR repository,
log group, ALB, target groups, WAF web ACL, application IAM roles/profile,
application secrets, or ACM certificate using the proposed fixed names.

The proposed bucket
`mutualgpu-data-428590861908-us-east-1` returned S3 `404 Not Found`, indicating
that it was globally unused at observation time. Recheck immediately before a
change set because S3 names are global and time-sensitive.

Target AZ mapping and availability:

| AZ | AZ ID | `t4g.nano` offered | PostgreSQL 18.4 `db.t4g.micro` offered |
|---|---|---:|---:|
| `us-east-1a` | `use1-az1` | yes | yes |
| `us-east-1b` | `use1-az2` | yes | yes |
| `us-east-1c` | `use1-az4` | yes | yes |
| `us-east-1d` | `use1-az6` | yes | yes |
| `us-east-1e` | `use1-az3` | no | no |
| `us-east-1f` | `use1-az5` | yes | yes |

Proposed pending confirmation: primary `us-east-1a`, secondary `us-east-1b`.

The current public ECS AMI parameter resolves to:

```text
ami-0d52a9965700d5237
al2023-ami-ecs-hvm-2023.0.20260727-kernel-6.1-arm64
```

It is ARM64 and available. Its root snapshot is 30 GiB, so the launch template
keeps the snapshot size rather than attempting to shrink it, while requiring an
encrypted gp3 root volume with deletion on termination. The public AMI parameter
is time-sensitive and must be re-resolved and inspected during change-set review.

PostgreSQL `18.4` is available for Single-AZ `db.t4g.micro` with 20 GiB gp3 in
the proposed AZs. The local template defaults to a fixed 20 GiB volume and makes
storage autoscaling an explicit parameter. It selects Database Insights Standard
with seven-day Performance Insights retention; AWS currently documents that
combination as free, while Advanced mode and paid retention are not selected.

The target currently has no AWS Budget. It has one default service cost-anomaly
monitor and one daily subscription that requires both at least `$100` absolute
impact and at least `40%` percentage impact. Subscriber addresses were not
displayed. The credit balances and expiry dates remain a console-only
verification gate; Brave must not be used until the user is present and the
target account is visibly confirmed. AWS currently documents budget monitoring
and notifications without actions as free; no budget action or budget report is
proposed.

## Proposed fixed configuration for review

| Item | Proposed value |
|---|---|
| Foundation stack | `mutualgpu-foundation` |
| Service stack | `mutualgpu-service` |
| Region | `us-east-1` |
| Public hostname | `mutualgpu.com` |
| Application bucket | `mutualgpu-data-428590861908-us-east-1` |
| VPC CIDR | `10.42.0.0/16` |
| Public subnets | `10.42.0.0/20`, `10.42.16.0/20` |
| Database subnets | `10.42.32.0/20`, `10.42.48.0/20` |
| Primary / secondary AZ | `us-east-1a` / `us-east-1b` |
| ECS cluster | `mutualgpu-demo` |
| ECS service / ECR repository | `mutualgpu-api` |
| Capacity provider | `mutualgpu-api-ec2` |
| Launch template | `mutualgpu-ecs-host` |
| Auto Scaling group | `mutualgpu-ecs-hosts` |
| ECS host | one `t4g.nano`, Linux ARM64, standard CPU credits |
| Initial/final ASG count | `0` for association bootstrap, then `1` |
| Task | CPU `256`, reservation `256 MiB`, hard limit `384 MiB` |
| ECS desired count | `1` |
| ECS release retention | At most the last five successful target `mutualgpu-api` task-definition revisions |
| ECR release retention | At most five target multi-architecture release indexes, plus only their required platform and attestation artifacts |
| Deployment percentages | minimum healthy `0`, maximum `100` |
| Network mode | `bridge`, fixed host/container ports `8080` and `8081` |
| RDS | PostgreSQL `18.4`, `db.t4g.micro`, Single-AZ, private |
| RDS storage | proposed fixed 20 GiB gp3 |
| RDS backup / maintenance | `03:00-04:00` UTC / Sunday `05:00-06:00` UTC |
| Database pool | minimum `1`, maximum `5` |
| Log group / retention | `/ecs/mutualgpu-api`, 7 days |
| Database secret | `mutualgpu/postgres` |
| Deployment secret | `mutualgpu/deployment` |
| ALB / target groups | `mutualgpu-demo`, `mutualgpu-web`, `mutualgpu-grpc` |
| WAF | `mutualgpu-provider-enrollment`, rate `10` per minute |
| NAT | none |
| S3 gateway endpoint | enabled on the public route table |

The proposed target VPC CIDR overlaps the live source VPC. That is acceptable
only for the approved clean start with no peering or source-to-target data path.
Choose a different CIDR before foundation creation if future peering is likely.

Every supported persistent resource uses:

```text
Application=MutualGPU
Project=MutualGPU
Environment=Production
ManagedBy=CloudFormation
Owner=Quinn-MutualCompute
Component=<resource-specific>
Lifecycle=Persistent
```

The EC2 launch template tags instances, EBS volumes, and network interfaces; ASG
tags propagate at launch; ECS managed tags and service-tag propagation are
enabled; and RDS copies tags to snapshots.

The existing manually bootstrapped identities remain untagged. A separate
reviewed target change should apply:

| Identity | Proposed manual-bootstrap tags |
|---|---|
| `mutual-ai-automation` | `Application=MutualGPU`, `Project=MutualGPU`, `Environment=Production`, `ManagedBy=ManualBootstrap`, `Owner=Quinn-MutualCompute`, `Component=Identity`, `Lifecycle=Persistent` |
| `MutualGPUMigrationAdmin` | the same tags except `Lifecycle=Temporary`, plus a fresh reviewed `ReviewAfter=<ISO-8601 date>` |

This local implementation does not choose a `ReviewAfter` date and did not tag
either identity.

## Local implementation

- `src/MutualGPU.Api/Program.cs` now requires an explicit, validated HTTPS S3
  image origin whenever S3 is configured and emits only that origin in CSP.
- The API integration test requires the proposed target S3 origin and rejects
  the obsolete source bucket origin.
- `deploy/aws/foundation.yaml` now creates the clean S3 bucket, S3 gateway
  endpoint, explicitly isolated database route table, private PostgreSQL, and
  empty-bootstrap EC2 ECS capacity provider. Only the ALB accepts public ingress;
  its egress is restricted to API ports 8080/8081, the API host accepts those
  ports only from the ALB security group, and PostgreSQL accepts 5432 only from
  the API security group.
- `deploy/aws/service.yaml` now uses EC2/ARM64/bridge networking, instance target
  groups, one stop-before-start task, `/health/ready`, and a five-connection
  PostgreSQL pool.
- `scripts/deploy-aws-service.sh` is target-only, identity-gated, change-set
  based, and clean-start guarded. A first service-stack creation and its execution
  both reverify the exact recorded source commit/tag/index provenance in each
  target platform image. Its post-release checks verify the running release and
  deployed security boundaries using target reads only. After a stable service
  release, its separate retention workflow produces a content-hashed preview;
  only an explicit match of that SHA-256 can mark the release successful and
  delete older revisions and ECR artifacts outside the corresponding five
  multi-architecture releases.
- `scripts/build-and-push-quinn-mutualcompute-image.sh` is locked to the first
  release commit and tag, rejects overrides, and requires an explicit separate
  clean Git worktree at `f96854d9429b398421fe15bbce89a8a741e1c769`.
  It publishes from a fresh temporary context, verifies both target platform
  configs carry the recorded source provenance, and emits a canonical receipt.

The release-retention operation refuses to proceed if the service is unstable,
if another service/cluster or standalone task consumes the reserved task family,
if a live task falls outside the last five successful releases, if any retained
image is not digest-pinned in the exact target repository, or if a retained
index lacks either Linux AMD64 or Linux ARM64. It recursively protects index
descriptors and OCI referrers, rechecks live consumers before ECR deletion, and
checks each AWS deletion response for per-item failures. Failed or manually
registered task definitions do not displace successful rollback releases.

Five multi-architecture releases require more than five raw ECR manifest
entries: each logical release has an OCI index and child platform/attestation
manifests. The symmetry enforced here is five ECS releases to no more than five
ECR release indexes; only the dependency closure of those indexes may remain.
No independent age/tag lifecycle rule is configured because it cannot verify ECS
rollback references; the reviewed explicit retention plan is the only cleanup
mechanism.

This policy applies only to the new target account. The 53 historical source
task-definition revisions and all source ECR entries remain untouched under the
strictly read-only `ai-quinn` boundary.

Both CloudFormation templates passed target `ValidateTemplate`. Both shell
scripts passed shell syntax validation. No change set was created or executed,
no image was built or pushed, and no AWS application resource was mutated.

## Approval gates before any target infrastructure mutation

1. Confirm `mutualgpu.com` remains the public hostname.
2. Confirm that the user will perform the Spaceship certificate-validation and
   apex-record changes manually from the exact values Codex presents.
3. Approve or replace the proposed target bucket name.
4. Approve or replace primary `us-east-1a` and secondary `us-east-1b`.
5. Approve the overlapping `10.42.0.0/16` target VPC or choose a future-proof
   non-overlapping CIDR before creation.
6. Approve a new `adminMasterPassword` or operational reuse; create a fresh
   target task-handle encryption key in either case.
7. Confirm stop-before-start deployment downtime.
8. Confirm fixed 20 GiB RDS storage rather than autoscaling to 100 GiB.
9. Confirm the WAF rate remains 10 per source IP per minute.
10. Confirm the fixed resource names and the backup/maintenance windows above.
11. Confirm the first image is the exact live S3-exclusive application at
    `f96854d9429b398421fe15bbce89a8a741e1c769`, built from a separate clean
    worktree without switching the shared migration working tree.
12. Confirm the source remains untouched, accept DNS-propagation overlap between
    the independent environments, and accept that clean-target writes are lost
    if DNS rollback returns users to the source.
13. Supply the desired monthly budget threshold and notification address.
14. Reverify credit balances/expiry in the Billing console with the user
    present.
15. Review the exact foundation change set with `EcsHostCount=0` before
    execution; then separately review the update to `EcsHostCount=1`.
16. Approve the manual-bootstrap identity tags and a fresh migration-role
    `ReviewAfter` date.
