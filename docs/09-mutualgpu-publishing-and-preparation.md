# MutualGPU publishing and preparation

## Purpose

This document defines how the MutualGPU API becomes a deployable release artifact and how its Backblaze B2 persistence is proven before publication. It stops at an immutable container image and a release-ready configuration contract.

AWS resource creation and deployment of that image are covered separately in [MutualGPU AWS infrastructure provisioning](10-mutualgpu-aws-infrastructure-provisioning.md).

The current demo deployment region is `us-east-1`.

MutualGPU is a demo MVP, not a production-ready multi-tenant service. The release nevertheless must preserve its core guarantees: public transport is TLS-only, provider credentials remain secret, durable data uses Backblaze rather than process memory, and only one API process operates on the store.

## 1. API build and packaging

### Current state

The deployable entry point is `src/MutualGPU.Api/MutualGPU.Api.csproj`. It targets .NET 10 and includes the static requestor UI in `wwwroot`.

The repository contains the container build assets:

```text
deploy/
├── Dockerfile
└── aws/                          # infrastructure defined in the AWS document
.dockerignore
```

Build from the repository root. The Dockerfile receives the root build context so it can copy the host-published API artifact from `artifacts/mutualgpu-api`. `.dockerignore` excludes local AWS credentials, environment files, certificates, build outputs, and package caches from that context.

### Container contract

The Dockerfile should:

1. publish the framework-dependent API project on the host using the repository's working Protobuf generator configuration;
2. copy that output from `artifacts/mutualgpu-api` into a pinned `mcr.microsoft.com/dotnet/aspnet:10.0.8` runtime image;
3. let Buildx assemble the same managed publish output into `linux/amd64` and `linux/arm64` runtime images, without invoking `Grpc.Tools` inside a Linux ARM build container;
4. run as a non-root user with a read-only root filesystem where practical;
5. expose port `8080` for HTTP/1.1 and port `8081` for cleartext HTTP/2 inside the deployment network;
6. start `MutualGPU.Api.dll` directly.

Use two Kestrel endpoints because browser WebSocket upgrades require HTTP/1.1 while native gRPC needs HTTP/2 between the load balancer and container:

```text
Kestrel__Endpoints__Web__Url=http://0.0.0.0:8080
Kestrel__Endpoints__Web__Protocols=Http1
Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:8081
Kestrel__Endpoints__Grpc__Protocols=Http2
```

The container does not contain or terminate the public certificate. TLS terminates at the trusted AWS ingress, which forwards the original HTTPS scheme. Neither container port may be directly exposed to the internet.

### Repeatable release build

The release pipeline, or equivalent manual commands, should perform:

```bash
export AWS_REGION=us-east-1
export AWS_PROFILE=ecs-fargate
export ECR_REPOSITORY=mutualgpu-api
export AWS_ACCOUNT_ID="$(aws sts get-caller-identity --profile "$AWS_PROFILE" --query Account --output text)"
export ECR_REGISTRY="${AWS_ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
export GIT_COMMIT="$(git rev-parse HEAD)"

dotnet restore NetCats.Examples.MutualGPU.slnx
dotnet test NetCats.Examples.MutualGPU.slnx \
  --configuration Release --no-restore
node --test tests/frontend/*.test.mjs
npm test --prefix sdk/typescript

dotnet publish src/MutualGPU.Api/MutualGPU.Api.csproj \
  --configuration Release \
  --no-restore \
  --disable-build-servers \
  --output artifacts/mutualgpu-api \
  /p:UseAppHost=false

aws ecr get-login-password \
  --region "$AWS_REGION" \
  --profile "$AWS_PROFILE" \
  | docker login \
      --username AWS \
      --password-stdin "$ECR_REGISTRY"

docker buildx build \
  --platform linux/amd64 \
  --file deploy/Dockerfile \
  --tag "$ECR_REGISTRY/$ECR_REPOSITORY:$GIT_COMMIT" \
  --push \
  .

export ECR_DIGEST="$(aws ecr describe-images \
  --repository-name "$ECR_REPOSITORY" \
  --image-ids imageTag="$GIT_COMMIT" \
  --region "$AWS_REGION" \
  --profile "$AWS_PROFILE" \
  --query 'imageDetails[0].imageDigest' \
  --output text)"
printf 'Published %s\n' "${ECR_REGISTRY}/${ECR_REPOSITORY}@${ECR_DIGEST}"
```

The release machine needs a working Docker BuildKit/buildx installation with `linux/amd64` support. A plain native `docker build` on an ARM workstation is not an equivalent validation of the Fargate image.

Do not use `latest` as the deployment identity. Tag the image with the Git commit SHA, push it to a private ECR repository, and record the immutable ECR digest. Rollback must select a previously verified digest rather than rebuild old source.

### Container acceptance checks

- The container starts without the source tree or build SDK.
- The application writes only to explicitly writable temporary locations.
- `GET /health/live` returns `200` through port `8080`.
- `GET /health/ready` does not return `200` until storage health and startup projection recovery complete. With the current blocking hosted-service startup, the listener may not accept connections during recovery; a storage failure prevents the host becoming ready.
- Native gRPC enrollment and a long-running `Connect` stream work through port `8081`.
- Static UI, REST, SSE, and WebSocket connections work through port `8080`.
- No Backblaze key, provider key, provider-key pepper, or TLS private key exists in an image layer.

## 2. Release configuration contract

The published image receives configuration through ASP.NET Core environment-variable binding. Non-secret deployment settings are:

```text
ASPNETCORE_ENVIRONMENT=Production
MutualGPU__TrustForwardedProto=true
MutualGPU__Backblaze__Endpoint=https://s3.<b2-region>.backblazeb2.com
MutualGPU__Backblaze__BucketName=<private-bucket-name>
MutualGPU__ProviderCorsOrigins__0=https://<chrome-provider-origin>
NetCats__FiberDiagnostics__Enabled=false
```

The following are secrets and must be injected at deployment time rather than stored in source, image metadata, or a task definition:

```text
MutualGPU__Backblaze__KeyId
MutualGPU__Backblaze__ApplicationKey
```

Provider shared keys are not task-definition secrets. They are durable, SHA-256-addressed records in the dedicated AWS S3 bucket `mutualgpu-preshared-keys`, under `mutualgpu/v3/provider-keys/`; their existence is the authentication binding. Keys are generated with 256 bits of randomness, and the raw key never appears in an object name or record body.

Issue a batch through the operator-only provisioner, using the standard AWS credential chain and the same pepper as the API. Capture the one-time plaintext output in a restricted file, never CI or terminal logs:

```text
umask 077
AWS_PROFILE=ai-quinn \
MutualGPU__ProviderKeyS3__BucketName=mutualgpu-preshared-keys \
MutualGPU__ProviderKeyS3__Region=us-east-1 \
dotnet run --project tools/MutualGPU.ProviderKeyProvisioner -- --count 20 > provider-keys.txt
```

It writes one create-only record per key and emits each raw provider key exactly once. Send the output through an approved secret-distribution channel; do not write it to source, CI logs, task definitions, or deployment secrets. The Node or Chrome provider receives only its own key.

Before publication, add a production startup guard: when `ASPNETCORE_ENVIRONMENT=Production` and `MutualGPU:Backblaze` is absent, the API must fail startup rather than silently select `InMemoryObjectStore`. An explicit local/demo override can preserve the credential-free local composition.

Fiber observability has two release profiles:

- normal public release: `NetCats__FiberDiagnostics__Enabled=false`, so the observer and projection are not created;
- presentation release: `NetCats__FiberDiagnostics__Enabled=true` and `NetCats__FiberDiagnostics__EnableInProduction=true`. Set `MutualGPU__Demo__SimulateForest=true` only when canned forest simulations are wanted.

The presentation profile makes `/_netcats/*` reachable unless ingress adds protection. Treat that as an explicit demo decision.

## 3. Backblaze preparation

### Bucket and application key

1. Enable B2 and create a private bucket in the Backblaze region nearest the selected AWS region. Use a lowercase, hyphenated name without periods for maximum S3 compatibility. Backblaze documents the current naming rules in [Cloud Storage buckets](https://www.backblaze.com/docs/cloud-storage-buckets).
2. Record the bucket's S3 endpoint in the form `https://s3.<region>.backblazeb2.com`. Do not include the bucket name in `MutualGPU__Backblaze__Endpoint`. Backblaze accepts S3-compatible traffic only over HTTPS and documents the endpoint in [Call the S3-compatible API](https://www.backblaze.com/docs/en/cloud-storage-call-the-s3-compatible-api).
3. Create a dedicated application key rather than using a master key. Restrict it to this bucket and, if supported for all required operations, the `mutualgpu/v3/` file-name prefix.
4. Grant `listFiles`, `readFiles`, `writeFiles`, and `deleteFiles`. Backblaze notes that both write and delete capabilities can be needed for S3 delete behavior; see [S3-compatible application keys](https://www.backblaze.com/docs/cloud-storage-s3-compatible-app-keys).
5. Store the returned key ID and application key immediately in the deployment secret store. The application-key secret is shown only once.
6. Keep the bucket private. Presigned GET URLs are supported by the S3-compatible API and are the only intended direct object access; see [Backblaze S3-compatible presigned URLs](https://www.backblaze.com/docs/cloud-storage-s3-compatible-api).
7. Do not enable Object Lock for the demo bucket. The application deletes queue markers and abandoned staged inputs.

Backblaze buckets retain file versions by default. Define a lifecycle policy for old or hidden versions after choosing the demo retention window, but do not expire current `mutualgpu/v3/` objects while they remain authoritative. The MVP has no application-level retention job.

### Browser CORS

The requestor UI opens result URLs as downloads, and native Node providers are not subject to browser CORS. A Chrome provider fetching an input from a presigned Backblaze URL is subject to browser CORS.

If Chrome providers are part of the release, add a narrow bucket CORS rule allowing only:

- the exact Chrome provider origin;
- `GET` and `HEAD`;
- required request headers only;
- no credential exposure.

This is separate from `MutualGPU:ProviderCorsOrigins`, which controls browser calls to the MutualGPU API. SDK consumers never configure or authenticate to Backblaze.

## 4. Backblaze integration-test release gate

### What is currently proven

The unit and API integration suites use `InMemoryObjectStore`. They prove repository behavior and API flows against the abstraction. They do not prove AWS SDK configuration, B2 authentication, B2 ETag behavior, presigned URLs, list pagination, deletion/version behavior, or conditional writes against a live bucket.

Live Backblaze testing is therefore required before the image is approved for deployment with B2 as its durable store.

### Highest-risk compatibility check

`BackblazeObjectStore.PutAsync` sends `If-None-Match: *` for create-only writes and can send `If-Match` for ETag-conditional writes. Backblaze's current `PutObject` reference lists supported headers but does not explicitly list either conditional header in [S3 Put Object](https://www.backblaze.com/apidocs/s3-put-object).

The first live contract test must prove:

1. the first create-only write succeeds;
2. a second create-only write to the same key fails atomically;
3. the original bytes remain unchanged;
4. concurrent create-only writes produce exactly one winner.

If B2 ignores or rejects the conditional header, publication is blocked until the persistence protocol is revised. The in-process repository lock does not replace object-store atomicity across crashes or future processes.

### Test isolation

Never point integration tests at the deployment bucket. Use a dedicated private integration bucket and restricted key. The current object-key root is hard-coded as `mutualgpu/v3`, so an isolated end-to-end suite should either:

- use a disposable or dedicated test bucket initially; or
- first make the object root configurable and give every run a prefix such as `integration/<run-id>/mutualgpu/v3/`.

The configurable-root approach is preferred for CI, but every list and recovery path must use the injected root consistently. Cleanup must account for B2 versioning; deleting a current object name may leave older versions until lifecycle rules remove them.

### Live adapter contract suite

Add an opt-in `MutualGPU.Backblaze.IntegrationTests` project or test category that requires explicit environment variables and otherwise skips. Cover:

- health check against an empty private bucket;
- put/get round trip with exact bytes, length, and ETag;
- missing-object behavior;
- create-only and concurrent create-only semantics;
- expected-ETag success and stale-ETag rejection;
- prefix filtering and continuation-token pagination;
- deletion followed by missing-current-object behavior;
- presigned GET without credentials and rejection after expiry;
- cancellation and representative B2 error mapping;
- an object near the demo upload limit.

Every test key must contain a unique run ID, cleanup must execute in `finally`, and CI credentials must never appear in output.

### Deployed end-to-end proof

After the adapter contract passes, the eventual AWS deployment must prove:

1. the API becomes ready against an empty deployment bucket;
2. the real Node SDK enrolls and connects through public gRPC;
3. the requestor submits the baseline TripoSplat form with an image and resource selection;
4. enrollment, input, manifest, facts, projection, queue marker, attempt events, and result artifacts appear below `mutualgpu/v3/`;
5. the provider downloads its input through a presigned URL;
6. the synthetic provider completes and the requestor downloads the result through a presigned URL;
7. an API restart rebuilds capability and task projections from B2;
8. task submission and scheduling still work after recovery;
9. a temporary deployment with invalid B2 credentials fails closed and never receives traffic.

The infrastructure document owns the mechanics for running this proof.

## Publication order

1. Implement and pass the live B2 adapter contract, especially conditional writes.
2. Add the production fail-closed storage guard.
3. Add and verify the multi-stage Docker image and dual Kestrel endpoints.
4. Run the complete Release test matrix from a clean checkout.
5. Push the commit-tagged image to private ECR and record its digest.
6. Hand the digest and required configuration contract to the AWS provisioning process.
7. Approve the release only after the deployed end-to-end proof passes.

## Publication checklist

- [ ] Release tests pass from a clean checkout.
- [ ] Live B2 conditional-write, presigned-download, deletion, and listing tests pass.
- [ ] Production cannot silently select the in-memory store.
- [ ] Container ports and protocols match the documented contract.
- [ ] No credentials or private certificates exist in image layers or source.
- [ ] Image is tagged with the commit and identified by an immutable ECR digest.
- [ ] Backblaze bucket is private and its application key is bucket-scoped.
- [ ] Chrome-provider CORS is either narrowly configured or explicitly unnecessary.
- [ ] Diagnostics and simulations are explicitly enabled or disabled for the release profile.
- [ ] The previous verified image digest is recorded for rollback.
