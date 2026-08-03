# Multi-provider artifact storage strategy

Status: partially implemented (provider registry and read routing)

Date: 2026-08-02

Scope: artifact bytes, storage-target routing, and presigned downloads

## Decisions

1. PostgreSQL owns logical artifact metadata and lifecycle state. Object-storage
   targets own artifact bytes.
2. AWS S3 remains the default storage target. Backblaze B2 and any later
   provider are explicit opt-in targets; they are never auto-detected or used as
   an implicit fallback.
3. A logical artifact can have one or more physical locations. The initial
   implementation may write exactly one location, but the schema must support
   `n` configured targets without changing the artifact identity.
4. SHA-256 is the provider-independent integrity value for a logical artifact.
   An ETag is a provider-issued, location-specific, opaque value. MutualGPU does
   not calculate an ETag or assume that it is an MD5 digest.
5. Uploads continue to pass through the API for validation and authorization.
   Downloads continue directly from object storage through short-lived
   presigned URLs.
6. Provider endpoints, bucket names, credentials, and presigned URLs are not
   stored with an artifact. PostgreSQL stores only a stable configured target ID
   and the provider-specific object key and ETag.

## Current compatibility boundary

The current schema retains provider-independent metadata and the physical object
key together in `artifacts.s3_object_key` for compatibility. Location rows are
authoritative for PostgreSQL-backed downloads: the resolver selects the one
available persisted location and resolves its configured target. The legacy
single-target `MutualGPU:S3` configuration remains supported as
`aws-primary`.

The multi-provider change is additive:

- retain `artifacts.s3_object_key` during the compatibility release;
- interpret it as the legacy opaque object key rather than as proof that the
  bytes are in AWS;
- add normalized location rows and make them authoritative for new routing;
- remove or rename the legacy column only in a later major release with a
  breaking-change notice and migration instructions.

No artifact bytes are copied by this schema change. Backfilling a location row
for an existing AWS artifact records where its bytes already reside.

## Logical and physical model

`artifacts` remains the logical record:

- `id`
- task, attempt, direction, and role ownership
- content type and length
- SHA-256
- logical lifecycle state
- creation time and result-upload association

Add `artifact_locations` for physical placement:

- `artifact_id uuid not null references artifacts(id)`
- `storage_target_id text not null`
- `object_key text not null`
- `provider_etag text null`
- `state text not null` (`staged`, `available`, `orphaned`, `deleted`, or
  `failed`)
- `created_at timestamptz not null`
- `last_verified_at timestamptz null`
- primary key `(artifact_id, storage_target_id)`
- unique `(storage_target_id, object_key)`

An artifact is logically available when it has at least one committed,
available location. Location failure does not change the artifact's SHA-256 or
identity. Deleting a single replica marks only that location deleted; logical
deletion requires the artifact lifecycle operation to account for every active
location.

`storage_target_id` names a configured logical target such as `aws-primary` or
`backblaze-primary`. It is not merely a provider enum: one provider can have
multiple buckets, regions, or accounts. A target ID is immutable once referenced
by PostgreSQL. Credential rotation may retain the ID, but moving to a different
bucket or provider requires a new ID.

## Target configuration and registry

Configuration defines the non-secret and secret material for each target and
the target selected for new writes. A representative shape is:

```text
MutualGPU:ObjectStorage:WriteTarget = aws-primary

MutualGPU:ObjectStorage:Targets:aws-primary:Provider = AwsS3
MutualGPU:ObjectStorage:Targets:aws-primary:BucketName = ...
MutualGPU:ObjectStorage:Targets:aws-primary:Region = ...

MutualGPU:ObjectStorage:Targets:backblaze-primary:Provider = BackblazeB2
MutualGPU:ObjectStorage:Targets:backblaze-primary:BucketName = ...
MutualGPU:ObjectStorage:Targets:backblaze-primary:Region = us-east-005
MutualGPU:ObjectStorage:Targets:backblaze-primary:Endpoint = https://s3.us-east-005.backblazeb2.com
MutualGPU:ObjectStorage:Targets:backblaze-primary:AccessKeyId = <B2 application key ID>
MutualGPU:ObjectStorage:Targets:backblaze-primary:SecretAccessKey = <B2 application key>
MutualGPU:ObjectStorage:Targets:backblaze-primary:BrowserCorsMode = ExternallyManaged
```

Provider credentials remain in the deployment secret boundary. They must not
appear in PostgreSQL, logs, metrics, health output, or generated documentation.

`IObjectStoreRegistry` resolves an allow-listed
`storage_target_id` to its configured `IObjectStore`, `IObjectStoreHealth`, and
browser-read CORS policy. Startup validates that:

- the selected write target exists;
- every target ID referenced by an available artifact location is configured;
- ambiguous or incomplete target configuration fails closed.

Target-ID immutability remains an operational deployment invariant: change a
bucket, account, or provider by adding a new target ID and retaining the old
one for referenced locations. An AWS-selected deployment does not initialize
or contact Backblaze merely because a B2 target is configured. A database row
referencing an unconfigured target produces an explicit unavailable-target
failure; the registry does not substitute another provider.

The B2 adapter accepts only its canonical HTTPS S3-compatible endpoint
`https://s3.<Region>.backblazeb2.com` and explicit application-key credentials.
It uses path-style addressing by default, returns provider ETags only as opaque
values, and rejects conditional writes before an SDK request. It is constructed
lazily only when its target is resolved for an authorized artifact download.

This release intentionally keeps all new artifact writes on `aws-primary`.
Configuring a B2 target is sufficient to retain readable historical B2
locations, but it does not enable B2 as a new-write target. That separate
provider-write decision requires its own reviewed release step.

## Write path

Requestor inputs and provider results currently arrive as multipart API
requests. The API validates type and structure, calculates SHA-256, and writes
the bytes to object storage. Retain that proxy boundary for the first
multi-provider release; direct object-store uploads would require a separate
authorization, CORS, size, checksum, and completion design.

At the start of an upload operation in the eventual multi-provider write
release:

1. capture the configured write target once;
2. resolve its object store through `IObjectStoreRegistry`;
3. write the object and retain the provider-issued ETag when the provider
   returns one;
4. commit the logical artifact and its `artifact_locations` row together; and
5. never retry the same logical write against a different provider implicitly.

Add `write_storage_target_id` to `result_upload_operations`. A process can fail
after output bytes are written but before artifact/location rows are committed;
the durable upload operation must identify the target that the orphan
reconciler should inspect. Input reconciliation scans each explicitly configured
target using bounded provider-specific operations.

The existing `IObjectStore.PutAsync` compatibility contract need not be broken.
If the application needs to persist the provider ETag without a follow-up read,
add an optional write-receipt capability that returns an opaque ETag and retain
the existing method for callers that do not need a receipt.

Artifact SHA-256 must be calculated at most once per accepted upload. Hash while
copying when practical, or pass an already validated digest into the storage
helper. Do not make a second pass merely to manufacture an ETag.

## Presigned download resolution

Presigned URLs remain ephemeral and are never stored. Introduce an
`IArtifactDownloadUrlResolver` above the provider adapters. Its operation is:

1. authorize the requestor or assigned provider for the logical artifact;
2. load committed, available `artifact_locations` rows;
3. select a location using an explicit configured policy;
4. resolve the target through `IObjectStoreRegistry`; and
5. ask that provider to create a bounded-lifetime HTTPS download URL.

The initial selection policy uses the artifact's single available location. A
future replica policy may choose by configured priority, residency, or health,
but it must choose before URL creation and must not contact a different provider
as an unreviewed error fallback.

The resolver returns the URL and expiry alongside the logical artifact's
content type, length, and SHA-256. The storage service supplies its native ETag
on the direct download response. Consumers may use SHA-256 for portable content
verification and ETag for location-specific caching or conditional requests.

Replace key reconstruction plus the singleton object store in:

- requestor result URL generation;
- provider input assignment and refreshed input URL generation; and
- any administrative artifact download path.

## CORS boundary

Because uploads pass through the API, any eventual object-storage browser CORS
policy is a read concern only. It remains separate from API CORS and task-write
origin/CSRF enforcement. MutualGPU currently does not enforce or synchronize
object-storage CORS when `MutualGPU:ObjectStorage:SynchronizeBrowserCors` is
false; the production AWS configuration uses that setting. A configured B2
target therefore remains externally managed until an explicit later CORS
decision is made.

## Schema rollout

1. Add `artifact_locations` and `result_upload_operations.write_storage_target_id`.
2. Backfill one `aws-primary` location from each existing
   `artifacts.s3_object_key`; this is metadata-only and does not copy bytes.
3. During the compatibility window, write both the legacy key field and the
   normalized location row in the same database operation.
4. Change reads, presigning, and reconciliation to use location rows.
5. Verify that every available artifact has at least one available configured
   location and that every `(target, key)` resolves to the expected object.
6. Stop reading the legacy column after the compatibility window. Its removal or
   provider-neutral rename is a major-release change.

## Acceptance criteria

- Existing AWS-only deployments retain their configuration and behavior.
- Backblaze is contacted only when an explicitly configured target is selected
  or an authorized artifact location references it.
- The schema can represent locations on multiple targets without duplicating
  logical metadata; the current resolver intentionally fails closed on more
  than one available location until a replica-selection policy is reviewed.
- Object-key uniqueness and ETags are scoped to a storage target.
- SHA-256 is identical across replicas of the same artifact; provider ETags may
  differ and are treated as opaque.
- Switching the write target affects new writes only. Existing locations remain
  resolvable through their recorded target IDs.
- Download authorization occurs before presigning, and URLs are never persisted.
- Orphan reconciliation routes list/delete operations to the recorded target and
  never deletes an object referenced by an available location.
- Credentials, signed URLs, and secret configuration never enter PostgreSQL or
  diagnostic output.
- No compatibility field or configuration key is removed outside a major
  release.
