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
readonly controller_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"

reviewed_commit="${MUTUALGPU_REVIEWED_COMMIT:?Set MUTUALGPU_REVIEWED_COMMIT to the exact reviewed release commit.}"
image_tag="${MUTUALGPU_REVIEWED_RELEASE_TAG:?Set MUTUALGPU_REVIEWED_RELEASE_TAG to the immutable reviewed release tag.}"
if [[ ! "$reviewed_commit" =~ ^[0-9a-f]{40}$ ]]; then
  echo "MUTUALGPU_REVIEWED_COMMIT must be an exact 40-character lowercase Git commit." >&2
  exit 1
fi
if [[ ! "$image_tag" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]]; then
  echo "MUTUALGPU_REVIEWED_RELEASE_TAG must be a valid immutable container tag." >&2
  exit 1
fi
source_worktree_input="${MUTUALGPU_SOURCE_WORKTREE:?Set MUTUALGPU_SOURCE_WORKTREE to a clean detached worktree at ${reviewed_commit}.}"
source_worktree="$(cd "$source_worktree_input" && pwd -P)"
if [[ ! -f "$source_worktree/vendor/NetCats/src/NetCats.Core/NetCats.Core.csproj" ]]; then
  git -C "$source_worktree" submodule update --init --recursive
fi

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
  echo "Source-worktree HEAD ${head_commit} does not equal reviewed commit ${reviewed_commit}." >&2
  exit 1
fi
if git -C "$source_worktree" symbolic-ref --quiet HEAD >/dev/null; then
  echo "The source worktree must be detached at the reviewed release commit." >&2
  exit 1
fi
tag_commit="$(git -C "$source_worktree" rev-parse "${image_tag}^{commit}" 2>/dev/null || true)"
if [[ "$tag_commit" != "$reviewed_commit" ]]; then
  echo "Release tag ${image_tag} must resolve exactly to reviewed commit ${reviewed_commit}." >&2
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
  echo "The immutable release tag ${image_tag} already exists; refusing to overwrite or silently replace it." >&2
  exit 1
fi

builder_platforms="$(docker buildx inspect --bootstrap | sed -n 's/^Platforms:[[:space:]]*//p')"
if [[ "$builder_platforms" != *"linux/amd64"* || "$builder_platforms" != *"linux/arm64"* ]]; then
  echo "The active Buildx builder must advertise both linux/amd64 and linux/arm64." >&2
  exit 1
fi

build_context="$(mktemp -d "${TMPDIR:-/tmp}/mutualgpu-release.XXXXXX")"
cleanup() {
  rm -rf -- "$build_context"
}
trap cleanup EXIT

dotnet publish "$source_worktree/src/MutualGPU.Api/MutualGPU.Api.csproj" \
  --configuration Release \
  --output "$build_context/artifacts/mutualgpu-api" \
  --artifacts-path "$build_context/dotnet-artifacts" \
  /p:UseAppHost=false

dotnet publish "$source_worktree/tools/MutualGPU.TransactionalDataMigrator/MutualGPU.TransactionalDataMigrator.csproj" \
  --configuration Release \
  --output "$build_context/artifacts/mutualgpu-transactional-data-migrator" \
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
  --label "com.mutualgpu.release.commit=${reviewed_commit}" \
  --label "com.mutualgpu.release.tag=${image_tag}" \
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
verification="$(MUTUALGPU_REVIEWED_COMMIT="$reviewed_commit" \
  MUTUALGPU_REVIEWED_RELEASE_TAG="$image_tag" \
  "$controller_root/scripts/deploy-aws-service.sh" verify-release-image "$image_uri")"
receipt="$(jq -n \
  --arg kind "MutualGPUReviewedRelease" \
  --argjson verification "$verification" \
  '{
     Kind: $kind,
     Verification: $verification
   }')"
receipt_hash="$(jq -cS . <<<"$receipt" | shasum -a 256 | awk '{print $1}')"

echo "$receipt"
echo "Build receipt SHA-256: ${receipt_hash}"
