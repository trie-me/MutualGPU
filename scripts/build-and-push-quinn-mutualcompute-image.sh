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
readonly live_source_release_commit="f96854d9429b398421fe15bbce89a8a741e1c769"
readonly live_source_release_tag="cors-all-origins-20260727-r2"
readonly live_source_index_digest="sha256:519b123233e0940c5f4b579e119ec8de3d4e168b6385af19b1c15d9f9d97ac31"
readonly reviewed_commit="$live_source_release_commit"
readonly image_tag="$live_source_release_tag"
readonly controller_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"

if [[ -n "${MUTUALGPU_REVIEWED_COMMIT:-}" || -n "${MUTUALGPU_IMAGE_TAG:-}" ]]; then
  echo "Commit and tag overrides are disabled for the first Quinn-MutualCompute release." >&2
  exit 1
fi
source_worktree_input="${MUTUALGPU_SOURCE_WORKTREE:?Set MUTUALGPU_SOURCE_WORKTREE to a clean detached worktree at ${live_source_release_commit}.}"
source_worktree="$(cd "$source_worktree_input" && pwd -P)"

if [[ "${AWS_PROFILE:-$target_profile}" != "$target_profile" ]]; then
  echo "AWS_PROFILE must be exactly ${target_profile}." >&2
  exit 1
fi
if [[ "${AWS_REGION:-$target_region}" != "$target_region" ]]; then
  echo "AWS_REGION must be exactly ${target_region}." >&2
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

head_commit="$(git -C "$source_worktree" rev-parse HEAD)"
if [[ "$head_commit" != "$reviewed_commit" ]]; then
  echo "Source-worktree HEAD ${head_commit} does not equal the live release commit ${reviewed_commit}." >&2
  exit 1
fi
if [[ -n "$(git -C "$source_worktree" status --porcelain --untracked-files=all)" ]]; then
  echo "The detached source worktree must be completely clean before producing the first deployment image." >&2
  exit 1
fi
if [[ ! -f "$source_worktree/deploy/Dockerfile" ||
      ! -f "$source_worktree/src/MutualGPU.Api/MutualGPU.Api.csproj" ]]; then
  echo "The detached source worktree is missing the reviewed API build inputs." >&2
  exit 1
fi

aws_target ecr describe-repositories --repository-names "$repository_name" >/dev/null
if aws_target ecr describe-images --repository-name "$repository_name" --image-ids "imageTag=${image_tag}" >/dev/null 2>&1; then
  echo "The immutable first-release tag ${image_tag} already exists; refusing to overwrite or silently replace it." >&2
  exit 1
fi

builder_platforms="$(docker buildx inspect --bootstrap | sed -n 's/^Platforms:[[:space:]]*//p')"
if [[ "$builder_platforms" != *"linux/amd64"* || "$builder_platforms" != *"linux/arm64"* ]]; then
  echo "The active Buildx builder must advertise both linux/amd64 and linux/arm64." >&2
  exit 1
fi

build_context="$(mktemp -d "${TMPDIR:-/tmp}/mutualgpu-first-release.XXXXXX")"
cleanup() {
  rm -rf -- "$build_context"
}
trap cleanup EXIT

dotnet publish "$source_worktree/src/MutualGPU.Api/MutualGPU.Api.csproj" \
  --configuration Release \
  --output "$build_context/artifacts/mutualgpu-api" \
  --artifacts-path "$build_context/dotnet-artifacts" \
  /p:UseAppHost=false

aws_target ecr get-login-password |
  docker login \
    --username AWS \
    --password-stdin \
    "${target_account}.dkr.ecr.${target_region}.amazonaws.com" \
    >/dev/null

docker buildx build \
  --file "$source_worktree/deploy/Dockerfile" \
  --platform linux/amd64,linux/arm64 \
  --provenance=true \
  --sbom=true \
  --label "org.opencontainers.image.revision=${reviewed_commit}" \
  --label "org.opencontainers.image.version=${image_tag}" \
  --label "com.mutualgpu.live-source.index=${live_source_index_digest}" \
  --label "com.mutualgpu.live-source.tag=${live_source_release_tag}" \
  --push \
  --tag "${repository_uri}:${image_tag}" \
  "$build_context"

image_digest="$(aws_target ecr describe-images \
  --repository-name "$repository_name" \
  --image-ids "imageTag=${image_tag}" \
  --query 'imageDetails[0].imageDigest' \
  --output text)"
if [[ -z "$image_digest" || "$image_digest" == "None" ]]; then
  echo "The pushed target image digest could not be resolved." >&2
  exit 1
fi

manifest_response="$(aws_target ecr batch-get-image \
  --repository-name "$repository_name" \
  --image-ids "imageDigest=${image_digest}" \
  --output json)"
if ! jq -e --arg digest "$image_digest" \
  '(.failures | length) == 0
   and ([.images[]? | select(.imageId.imageDigest == $digest)] | length) == 1
   and ([.images[]?
         | select(.imageId.imageDigest == $digest)
         | .imageManifestMediaType]
        | any(. == "application/vnd.oci.image.index.v1+json"
              or . == "application/vnd.docker.distribution.manifest.list.v2+json"))' \
  <<<"$manifest_response" \
  >/dev/null; then
  echo "The pushed target digest is missing or is not a supported multi-architecture image index." >&2
  jq '{Failures: [.failures[]? | {ImageId: .imageId, FailureCode: .failureCode, FailureReason: .failureReason}]}' \
    <<<"$manifest_response" \
    >&2
  exit 1
fi
image_uri="${repository_uri}@${image_digest}"
verification="$("$controller_root/scripts/deploy-aws-service.sh" \
  verify-live-source-release-image \
  "$image_uri")"
receipt="$(jq -n \
  --arg kind "MutualGPUQuinnMutualComputeFirstRelease" \
  --argjson verification "$verification" \
  '{
     Kind: $kind,
     Verification: $verification
   }')"
receipt_hash="$(jq -cS . <<<"$receipt" | shasum -a 256 | awk '{print $1}')"

echo "$receipt"
echo "Build receipt SHA-256: ${receipt_hash}"
