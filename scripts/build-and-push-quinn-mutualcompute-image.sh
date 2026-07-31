#!/usr/bin/env bash
set -euo pipefail

readonly target_profile="quinn-mutualcompute"
readonly target_login_profile="quinn-mutualcompute-login"
readonly target_login_session="arn:aws:iam::428590861908:user/mutual-ai-automation"
readonly target_account="428590861908"
readonly target_region="us-east-1"
readonly target_role_arn="arn:aws:sts::428590861908:assumed-role/MutualGPUMigrationAdmin/mutual-ai-automation"
readonly repository_name="mutualgpu-api"
readonly repository_uri="${target_account}.dkr.ecr.${target_region}.amazonaws.com/${repository_name}"

reviewed_commit="${MUTUALGPU_REVIEWED_COMMIT:?Set MUTUALGPU_REVIEWED_COMMIT to the exact reviewed PostgreSQL-capable Git commit.}"
image_tag="${MUTUALGPU_IMAGE_TAG:-postgres-${reviewed_commit:0:12}}"

if [[ "${AWS_PROFILE:-$target_profile}" != "$target_profile" ]]; then
  echo "AWS_PROFILE must be exactly ${target_profile}." >&2
  exit 1
fi
if [[ "${AWS_REGION:-$target_region}" != "$target_region" ]]; then
  echo "AWS_REGION must be exactly ${target_region}." >&2
  exit 1
fi
if [[ "$image_tag" == "latest" || ! "$image_tag" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]]; then
  echo "MUTUALGPU_IMAGE_TAG must be an immutable descriptive tag and must not be latest." >&2
  exit 1
fi

aws_target() {
  aws --profile "$target_profile" --region "$target_region" "$@"
}

configured_login_session="$(aws configure get "profile.${target_login_profile}.login_session")"
if [[ "$configured_login_session" != "$target_login_session" ]]; then
  echo "The target login profile is not bound to the reviewed automation user." >&2
  exit 1
fi

account="$(aws_target sts get-caller-identity --query Account --output text)"
arn="$(aws_target sts get-caller-identity --query Arn --output text)"
if [[ "$account" != "$target_account" || "$arn" != "$target_role_arn" ]]; then
  echo "Target identity gate failed. Refusing to build or push." >&2
  exit 1
fi

head_commit="$(git rev-parse HEAD)"
if [[ "$head_commit" != "$reviewed_commit" ]]; then
  echo "HEAD ${head_commit} does not equal reviewed commit ${reviewed_commit}." >&2
  exit 1
fi
if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
  echo "The working tree must be completely clean before producing a deployment image." >&2
  exit 1
fi

aws_target ecr describe-repositories --repository-names "$repository_name" >/dev/null
if aws_target ecr describe-images --repository-name "$repository_name" --image-ids "imageTag=${image_tag}" >/dev/null 2>&1; then
  echo "The immutable target tag ${image_tag} already exists; choose a new reviewed tag." >&2
  exit 1
fi

builder_platforms="$(docker buildx inspect --bootstrap | sed -n 's/^Platforms:[[:space:]]*//p')"
if [[ "$builder_platforms" != *"linux/amd64"* || "$builder_platforms" != *"linux/arm64"* ]]; then
  echo "The active Buildx builder must advertise both linux/amd64 and linux/arm64." >&2
  exit 1
fi

dotnet publish src/MutualGPU.Api/MutualGPU.Api.csproj \
  --configuration Release \
  --output artifacts/mutualgpu-api \
  /p:UseAppHost=false

aws_target ecr get-login-password |
  docker login \
    --username AWS \
    --password-stdin \
    "${target_account}.dkr.ecr.${target_region}.amazonaws.com" \
    >/dev/null

docker buildx build \
  --file deploy/Dockerfile \
  --platform linux/amd64,linux/arm64 \
  --provenance=true \
  --sbom=true \
  --push \
  --tag "${repository_uri}:${image_tag}" \
  .

image_digest="$(aws_target ecr describe-images \
  --repository-name "$repository_name" \
  --image-ids "imageTag=${image_tag}" \
  --query 'imageDetails[0].imageDigest' \
  --output text)"
if [[ -z "$image_digest" || "$image_digest" == "None" ]]; then
  echo "The pushed target image digest could not be resolved." >&2
  exit 1
fi

index_manifest="$(aws_target ecr batch-get-image \
  --repository-name "$repository_name" \
  --image-ids "imageDigest=${image_digest}" \
  --accepted-media-types application/vnd.oci.image.index.v1+json application/vnd.docker.distribution.manifest.list.v2+json \
  --query 'images[0].imageManifest' \
  --output text)"
amd64_count="$(jq '[.manifests[] | select(.platform.os == "linux" and .platform.architecture == "amd64")] | length' <<<"$index_manifest")"
arm64_count="$(jq '[.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64")] | length' <<<"$index_manifest")"
if [[ "$amd64_count" -ne 1 || "$arm64_count" -ne 1 ]]; then
  echo "The pushed OCI index does not contain exactly one Linux AMD64 and one Linux ARM64 image." >&2
  exit 1
fi

echo "Verified target multi-architecture image:"
echo "${repository_uri}@${image_digest}"
