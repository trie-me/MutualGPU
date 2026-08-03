#!/usr/bin/env bash
set -euo pipefail

readonly target_profile="quinn-mutualcompute"
readonly target_login_profile="quinn-mutualcompute-login"
readonly target_login_session="arn:aws:iam::428590861908:user/mutual-ai-automation"
readonly target_account="428590861908"
readonly target_region="us-east-1"
readonly target_role_arn="arn:aws:sts::428590861908:assumed-role/MutualGPUMigrationAdmin/mutual-ai-automation"
readonly foundation_stack="${MUTUALGPU_FOUNDATION_STACK:-mutualgpu-foundation}"
readonly service_stack="${MUTUALGPU_SERVICE_STACK:-mutualgpu-service}"
readonly ecs_cluster_name="mutualgpu-demo"
readonly ecs_service_name="mutualgpu-api"
readonly ecs_capacity_provider_name="mutualgpu-api-ec2"
readonly ecs_task_family="mutualgpu-api"
readonly ecs_releases_to_keep=5
readonly ecs_task_definition_prefix="arn:aws:ecs:${target_region}:${target_account}:task-definition/${ecs_task_family}:"
readonly ecs_cluster_arn="arn:aws:ecs:${target_region}:${target_account}:cluster/${ecs_cluster_name}"
readonly release_status_tag_key="ReleaseStatus"
readonly release_status_tag_value="Successful"
readonly ecr_repository_name="mutualgpu-api"
readonly ecr_repository_uri="${target_account}.dkr.ecr.${target_region}.amazonaws.com/${ecr_repository_name}"
readonly live_source_release_commit="f96854d9429b398421fe15bbce89a8a741e1c769"
readonly live_source_release_tag="cors-all-origins-20260727-r2"
readonly live_source_index_digest="sha256:519b123233e0940c5f4b579e119ec8de3d4e168b6385af19b1c15d9f9d97ac31"
readonly incubation_hotfix_tag="incubation-arbitrary-origins-20260731-r1"
readonly incubation_hotfix_kind="incubation-arbitrary-browser-task-write-origins"
readonly incubation_hotfix_patch_sha256="fd2de5e4bf5d4c01282a55793dd4954f52f9ba8847ce627d5be4cc60c4ad4696"
readonly reviewed_alb_ssl_policy="ELBSecurityPolicy-TLS13-1-2-Res-2021-06"

usage() {
  cat >&2 <<'USAGE'
Usage:
  scripts/deploy-aws-service.sh validate
  scripts/deploy-aws-service.sh create-foundation-change-set <change-set-name>
  scripts/deploy-aws-service.sh create-service-change-set <change-set-name>
  scripts/deploy-aws-service.sh execute-change-set <stack-name> <change-set-name-or-arn>
  scripts/deploy-aws-service.sh verify-release-image <target-image-uri>
  scripts/deploy-aws-service.sh verify-live-source-release-image <target-image-uri>
  scripts/deploy-aws-service.sh verify-deployed-live-source-release
  scripts/deploy-aws-service.sh verify-deployed-security-boundaries
  scripts/deploy-aws-service.sh plan-release-retention
  scripts/deploy-aws-service.sh apply-release-retention

This target-only tool never reads or changes the ai-quinn source account. It creates
reviewable CloudFormation change sets and never deletes failed stacks. Execution
requires MUTUALGPU_APPROVED_CHANGE_SET_ARN to equal the exact reviewed change-set ARN.
The first service creation is accepted only when its target ECR image is a
Linux AMD64 plus Linux ARM64 rebuild of the exact release observed live in
ai-quinn. Verification uses only immutable evidence recorded during Phase 0 and
target-account reads; it never contacts the source account.
After a successful target service release, preview the retention plan and pass
its exact SHA-256 approval to apply-release-retention. The explicit cleanup keeps
the last five successful mutualgpu-api task-definition revisions and their
complete target ECR artifact closures.
USAGE
  exit 2
}

[[ $# -ge 1 ]] || usage
action="$1"
shift

if [[ "${AWS_PROFILE:-$target_profile}" != "$target_profile" ]]; then
  echo "AWS_PROFILE must be exactly ${target_profile}." >&2
  exit 1
fi
if [[ "${AWS_REGION:-$target_region}" != "$target_region" ]]; then
  echo "AWS_REGION must be exactly ${target_region}." >&2
  exit 1
fi
if [[ -n "${MUTUALGPU_SOURCE_PROFILE:-}" ||
      -n "${MUTUALGPU_SOURCE_BUCKET:-}" ||
      -n "${MUTUALGPU_DATA_MIGRATION:-}" ||
      -n "${MUTUALGPU_IMPORT_SOURCE_DATA:-}" ]]; then
  echo "Source or data-migration settings are forbidden for the clean-start target deployment." >&2
  exit 1
fi

aws_target() {
  aws --profile "$target_profile" --region "$target_region" "$@"
}

verify_target_identity() {
  local configured_login_session account arn
  configured_login_session="$(aws configure get "profile.${target_login_profile}.login_session")"
  if [[ "$configured_login_session" != "$target_login_session" ]]; then
    echo "The target login profile is not bound to the reviewed automation user." >&2
    exit 1
  fi

  account="$(aws_target sts get-caller-identity --query Account --output text)"
  arn="$(aws_target sts get-caller-identity --query Arn --output text)"
  if [[ "$account" != "$target_account" || "$arn" != "$target_role_arn" ]]; then
    echo "Target identity gate failed. Refusing to continue." >&2
    echo "Expected account: ${target_account}" >&2
    echo "Expected ARN: ${target_role_arn}" >&2
    echo "Observed account: ${account}" >&2
    echo "Observed ARN: ${arn}" >&2
    exit 1
  fi
}

verify_release_image() {
  [[ $# -eq 1 ]] || usage
  local image_uri="$1"
  local reviewed_commit="${MUTUALGPU_REVIEWED_COMMIT:?Set MUTUALGPU_REVIEWED_COMMIT to the exact reviewed release commit.}"
  local reviewed_tag="${MUTUALGPU_REVIEWED_RELEASE_TAG:?Set MUTUALGPU_REVIEWED_RELEASE_TAG to the immutable reviewed release tag.}"
  local image_digest image_details scan_status manifest_response index_manifest
  local amd64_digest arm64_digest architecture child_manifest_response child_manifest config_digest download_url config_json

  if [[ ! "$reviewed_commit" =~ ^[0-9a-f]{40}$ ]]; then
    echo "MUTUALGPU_REVIEWED_COMMIT must be an exact 40-character lowercase Git commit." >&2
    exit 1
  fi
  if [[ ! "$reviewed_tag" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]]; then
    echo "MUTUALGPU_REVIEWED_RELEASE_TAG must be a valid immutable container tag." >&2
    exit 1
  fi
  if [[ "$image_uri" != "${ecr_repository_uri}@sha256:"* ]]; then
    echo "The release image must use the exact target repository and an immutable digest." >&2
    exit 1
  fi
  image_digest="${image_uri##*@}"
  if [[ ! "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "The release image has an invalid immutable digest." >&2
    exit 1
  fi

  image_details="$(aws_target ecr describe-images \
    --repository-name "$ecr_repository_name" \
    --image-ids "imageDigest=${image_digest}" \
    --query imageDetails \
    --output json)"
  if ! jq -e --arg digest "$image_digest" --arg tag "$reviewed_tag" \
    'length == 1
     and .[0].imageDigest == $digest
     and ((.[0].imageTags // []) | sort == [$tag])
     and (.[0].imageScanStatus.status // "COMPLETE") != "IN_PROGRESS"' \
    <<<"$image_details" \
    >/dev/null; then
    echo "The release digest must have exactly the reviewed immutable tag and a settled image-scan status." >&2
    exit 1
  fi
  scan_status="$(jq -r '.[0].imageScanStatus.status // "UNAVAILABLE"' <<<"$image_details")"

  manifest_response="$(aws_target ecr batch-get-image \
    --repository-name "$ecr_repository_name" \
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
    echo "The release digest is missing or is not a supported multi-architecture image index." >&2
    exit 1
  fi
  index_manifest="$(jq -r --arg digest "$image_digest" \
    '.images[] | select(.imageId.imageDigest == $digest) | .imageManifest' \
    <<<"$manifest_response")"
  if ! jq -e \
    '([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "amd64")] | length) == 1
     and ([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "arm64")] | length) == 1
     and ([.manifests[]?
           | select((.platform.os // "unknown") != "unknown")
           | select(.platform.os != "linux"
                    or (.platform.architecture != "amd64"
                        and .platform.architecture != "arm64"))]
          | length) == 0' \
    <<<"$index_manifest" \
    >/dev/null; then
    echo "The release index must contain exactly one Linux AMD64 and one Linux ARM64 runtime image." >&2
    exit 1
  fi
  amd64_digest="$(jq -r '.manifests[] | select(.platform.os == "linux" and .platform.architecture == "amd64") | .digest' <<<"$index_manifest")"
  arm64_digest="$(jq -r '.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest' <<<"$index_manifest")"
  if ! jq -e \
    --arg amd64 "$amd64_digest" \
    --arg arm64 "$arm64_digest" \
    '
      def attests($digest):
        [.manifests[]?
         | select((.platform.os // "") == "unknown"
                  and (.platform.architecture // "") == "unknown"
                  and .annotations["vnd.docker.reference.type"] == "attestation-manifest"
                  and .annotations["vnd.docker.reference.digest"] == $digest)]
        | length;
      attests($amd64) == 1 and attests($arm64) == 1
    ' \
    <<<"$index_manifest" \
    >/dev/null; then
    echo "The release index must include one Buildx attestation manifest for each runtime image." >&2
    exit 1
  fi

  for architecture in amd64 arm64; do
    local child_digest
    if [[ "$architecture" == "amd64" ]]; then child_digest="$amd64_digest"; else child_digest="$arm64_digest"; fi
    child_manifest_response="$(aws_target ecr batch-get-image \
      --repository-name "$ecr_repository_name" \
      --image-ids "imageDigest=${child_digest}" \
      --output json)"
    if ! jq -e --arg digest "$child_digest" \
      '(.failures | length) == 0
       and ([.images[]? | select(.imageId.imageDigest == $digest)] | length) == 1' \
      <<<"$child_manifest_response" \
      >/dev/null; then
      echo "The ${architecture} child manifest is missing from target ECR." >&2
      exit 1
    fi
    child_manifest="$(jq -r --arg digest "$child_digest" \
      '.images[] | select(.imageId.imageDigest == $digest) | .imageManifest' \
      <<<"$child_manifest_response")"
    config_digest="$(jq -r '.config.digest // empty' <<<"$child_manifest")"
    if [[ ! "$config_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      echo "The ${architecture} child has no valid OCI configuration digest." >&2
      exit 1
    fi
    download_url="$(aws_target ecr get-download-url-for-layer \
      --repository-name "$ecr_repository_name" \
      --layer-digest "$config_digest" \
      --query downloadUrl \
      --output text)"
    if [[ -z "$download_url" || "$download_url" == "None" ]]; then
      echo "Target ECR did not provide the ${architecture} configuration blob." >&2
      exit 1
    fi
    if ! config_json="$(curl --fail --silent --location "$download_url")"; then
      echo "The ${architecture} configuration blob could not be read from target ECR." >&2
      exit 1
    fi
    download_url=''
    if ! jq -e \
      --arg architecture "$architecture" \
      --arg revision "$reviewed_commit" \
      --arg version "$reviewed_tag" \
      '.os == "linux"
       and .architecture == $architecture
       and .config.Labels["org.opencontainers.image.revision"] == $revision
       and .config.Labels["org.opencontainers.image.version"] == $version
       and .config.Labels["com.mutualgpu.release.commit"] == $revision
       and .config.Labels["com.mutualgpu.release.tag"] == $version' \
      <<<"$config_json" \
      >/dev/null; then
      echo "The ${architecture} image does not carry the reviewed release provenance." >&2
      exit 1
    fi
  done

  jq -n \
    --arg imageUri "$image_uri" \
    --arg targetDigest "$image_digest" \
    --arg releaseTag "$reviewed_tag" \
    --arg sourceCommit "$reviewed_commit" \
    --arg scanStatus "$scan_status" \
    --arg amd64Digest "$amd64_digest" \
    --arg arm64Digest "$arm64_digest" \
    '{
       ImageUri: $imageUri,
       TargetIndexDigest: $targetDigest,
       ReviewedRelease: {Commit: $sourceCommit, Tag: $releaseTag},
       ImageScanStatus: $scanStatus,
       TargetPlatformDigests: {LinuxAmd64: $amd64Digest, LinuxArm64: $arm64Digest}
     }'
}

verify_live_source_release_image() {
  [[ $# -eq 1 ]] || usage
  local image_uri="$1"
  local image_digest image_details release_tag release_kind manifest_response index_manifest
  local amd64_digest arm64_digest architecture child_manifest_response
  local child_manifest config_digest download_url config_json

  if [[ "$image_uri" != "${ecr_repository_uri}@sha256:"* ]]; then
    echo "The first target image must use the exact target repository and an immutable digest." >&2
    exit 1
  fi
  image_digest="${image_uri##*@}"
  if [[ ! "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "The first target image has an invalid immutable digest." >&2
    exit 1
  fi

  image_details="$(aws_target ecr describe-images \
    --repository-name "$ecr_repository_name" \
    --image-ids "imageDigest=${image_digest}" \
    --query imageDetails \
    --output json)"
  release_tag="$(jq -r \
    --arg digest "$image_digest" \
    --arg source_tag "$live_source_release_tag" \
    --arg hotfix_tag "$incubation_hotfix_tag" \
    '
      if length == 1 and .[0].imageDigest == $digest then
        ([.[]?.imageTags[]? | select(. == $source_tag or . == $hotfix_tag)] | unique) as $approved
        | if ($approved | length) == 1 then $approved[0] else empty end
      else
        empty
      end
    ' \
    <<<"$image_details")"
  if [[ -z "$release_tag" ]]; then
    echo "The target digest is missing exactly one approved immutable release tag." >&2
    exit 1
  fi
  if [[ "$release_tag" == "$incubation_hotfix_tag" ]]; then
    release_kind="$incubation_hotfix_kind"
  else
    release_kind="exact-live-source-release"
  fi

  manifest_response="$(aws_target ecr batch-get-image \
    --repository-name "$ecr_repository_name" \
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
    echo "The first target digest is missing or is not a multi-architecture image index." >&2
    exit 1
  fi
  index_manifest="$(jq -r --arg digest "$image_digest" \
    '.images[] | select(.imageId.imageDigest == $digest) | .imageManifest' \
    <<<"$manifest_response")"
  if ! jq -e \
    '([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "amd64")] | length) == 1
     and ([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "arm64")] | length) == 1
     and ([.manifests[]?
           | select((.platform.os // "unknown") != "unknown")
           | select(.platform.os != "linux"
                    or (.platform.architecture != "amd64"
                        and .platform.architecture != "arm64"))]
          | length) == 0' \
    <<<"$index_manifest" \
    >/dev/null; then
    echo "The first target index must contain exactly one Linux AMD64 and one Linux ARM64 runtime image." >&2
    exit 1
  fi

  amd64_digest="$(jq -r \
    '.manifests[] | select(.platform.os == "linux" and .platform.architecture == "amd64") | .digest' \
    <<<"$index_manifest")"
  arm64_digest="$(jq -r \
    '.manifests[] | select(.platform.os == "linux" and .platform.architecture == "arm64") | .digest' \
    <<<"$index_manifest")"
  if ! jq -e \
    --arg amd64 "$amd64_digest" \
    --arg arm64 "$arm64_digest" \
    '
      def attests($digest):
        [.manifests[]?
         | select((.platform.os // "") == "unknown"
                  and (.platform.architecture // "") == "unknown"
                  and .annotations["vnd.docker.reference.type"] == "attestation-manifest"
                  and .annotations["vnd.docker.reference.digest"] == $digest)]
        | length;
      attests($amd64) == 1 and attests($arm64) == 1
    ' \
    <<<"$index_manifest" \
    >/dev/null; then
    echo "The first target index must include one Buildx attestation manifest for each runtime image." >&2
    exit 1
  fi

  for architecture in amd64 arm64; do
    if [[ "$architecture" == "amd64" ]]; then
      local child_digest="$amd64_digest"
    else
      local child_digest="$arm64_digest"
    fi
    child_manifest_response="$(aws_target ecr batch-get-image \
      --repository-name "$ecr_repository_name" \
      --image-ids "imageDigest=${child_digest}" \
      --output json)"
    if ! jq -e --arg digest "$child_digest" \
      '(.failures | length) == 0
       and ([.images[]? | select(.imageId.imageDigest == $digest)] | length) == 1' \
      <<<"$child_manifest_response" \
      >/dev/null; then
      echo "The ${architecture} child manifest is missing from target ECR." >&2
      exit 1
    fi
    child_manifest="$(jq -r --arg digest "$child_digest" \
      '.images[] | select(.imageId.imageDigest == $digest) | .imageManifest' \
      <<<"$child_manifest_response")"
    config_digest="$(jq -r '.config.digest // empty' <<<"$child_manifest")"
    if [[ ! "$config_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      echo "The ${architecture} child has no valid OCI configuration digest." >&2
      exit 1
    fi
    download_url="$(aws_target ecr get-download-url-for-layer \
      --repository-name "$ecr_repository_name" \
      --layer-digest "$config_digest" \
      --query downloadUrl \
      --output text)"
    if [[ -z "$download_url" || "$download_url" == "None" ]]; then
      echo "Target ECR did not provide the ${architecture} configuration blob." >&2
      exit 1
    fi
    if ! config_json="$(curl --fail --silent --location "$download_url")"; then
      echo "The ${architecture} configuration blob could not be read from target ECR." >&2
      exit 1
    fi
    download_url=''
    if ! jq -e \
      --arg architecture "$architecture" \
      --arg revision "$live_source_release_commit" \
      --arg version "$release_tag" \
      --arg source_index "$live_source_index_digest" \
      --arg source_tag "$live_source_release_tag" \
      --arg hotfix_tag "$incubation_hotfix_tag" \
      --arg hotfix_kind "$incubation_hotfix_kind" \
      --arg hotfix_patch "$incubation_hotfix_patch_sha256" \
      '.os == "linux"
       and .architecture == $architecture
       and .config.Labels["org.opencontainers.image.revision"] == $revision
       and .config.Labels["org.opencontainers.image.version"] == $version
       and .config.Labels["com.mutualgpu.live-source.index"] == $source_index
       and .config.Labels["com.mutualgpu.live-source.tag"] == $source_tag
       and (
         $version == $source_tag
         or (
           $version == $hotfix_tag
           and .config.Labels["com.mutualgpu.hotfix.base-revision"] == $revision
           and .config.Labels["com.mutualgpu.hotfix.kind"] == $hotfix_kind
           and .config.Labels["com.mutualgpu.hotfix.patch-sha256"] == $hotfix_patch
         )
       )' \
      <<<"$config_json" \
      >/dev/null; then
      echo "The ${architecture} image does not carry the reviewed source/hotfix provenance." >&2
      exit 1
    fi
  done

  jq -n \
    --arg imageUri "$image_uri" \
    --arg targetDigest "$image_digest" \
    --arg releaseTag "$release_tag" \
    --arg releaseKind "$release_kind" \
    --arg hotfixPatchSha256 "$incubation_hotfix_patch_sha256" \
    --arg sourceCommit "$live_source_release_commit" \
    --arg sourceTag "$live_source_release_tag" \
    --arg sourceIndexDigest "$live_source_index_digest" \
    --arg amd64Digest "$amd64_digest" \
    --arg arm64Digest "$arm64_digest" \
    '{
       ImageUri: $imageUri,
       TargetIndexDigest: $targetDigest,
       ReviewedRelease: {
         Tag: $releaseTag,
         Kind: $releaseKind,
         PatchSha256: (
           if $releaseKind == "exact-live-source-release"
           then null
           else $hotfixPatchSha256
           end
         )
       },
       SourceEvidence: {
         Commit: $sourceCommit,
         Tag: $sourceTag,
         IndexDigest: $sourceIndexDigest
       },
       TargetPlatformDigests: {
         LinuxAmd64: $amd64Digest,
         LinuxArm64: $arm64Digest
       }
     }'
}

verify_deployed_live_source_release() {
  [[ $# -eq 0 ]] || usage
  local service_description task_definition_arn task_definition image_uri
  local verification task_arns tasks running_digest arm64_digest
  local container_instance_arn container_instance ec2_instance_id instance ec2_image
  local web_target_group grpc_target_group web_health grpc_health
  service_description="$(aws_target ecs describe-services \
    --cluster "$ecs_cluster_name" \
    --services "$ecs_service_name" \
    --query 'services[0]' \
    --output json)"
  if [[ "$service_description" == "null" ]] || ! jq -e \
    --arg capacity_provider "$ecs_capacity_provider_name" \
    '(.deployments | length) == 1
     and .deployments[0].status == "PRIMARY"
     and (.deployments[0].rolloutState // "COMPLETED") == "COMPLETED"
     and .desiredCount == 1
     and .runningCount == 1
     and .pendingCount == 0
     and (.capacityProviderStrategy | length) == 1
     and .capacityProviderStrategy[0].capacityProvider == $capacity_provider
     and .capacityProviderStrategy[0].base == 1
     and .capacityProviderStrategy[0].weight == 1' \
    <<<"$service_description" \
    >/dev/null; then
    echo "The target service is absent or is not stable at desired/running count one." >&2
    exit 1
  fi
  task_definition_arn="$(jq -r '.taskDefinition' <<<"$service_description")"
  task_definition="$(aws_target ecs describe-task-definition \
    --task-definition "$task_definition_arn" \
    --query taskDefinition \
    --output json)"
  if ! jq -e \
    '.requiresCompatibilities == ["EC2"]
     and .networkMode == "bridge"
     and .runtimePlatform.cpuArchitecture == "ARM64"
     and .runtimePlatform.operatingSystemFamily == "LINUX"' \
    <<<"$task_definition" \
    >/dev/null; then
    echo "The deployed task definition is not the reviewed ARM64 EC2/bridge shape." >&2
    exit 1
  fi
  image_uri="$(jq -r '.containerDefinitions[] | select(.name == "api") | .image' \
    <<<"$task_definition")"
  verification="$(verify_live_source_release_image "$image_uri")"
  arm64_digest="$(jq -r '.TargetPlatformDigests.LinuxArm64' <<<"$verification")"
  if [[ "$(jq -r '.ReviewedRelease.Kind' <<<"$verification")" == "$incubation_hotfix_kind" ]] &&
     ! jq -e \
       '.containerDefinitions[]
        | select(.name == "api")
        | any(.environment[];
            .name == "MutualGPU__Security__AllowArbitraryBrowserTaskWriteOrigins"
            and .value == "true")' \
       <<<"$task_definition" \
       >/dev/null; then
    echo "The incubation hotfix is not paired with its explicit arbitrary-origin runtime flag." >&2
    exit 1
  fi

  task_arns="$(aws_target ecs list-tasks \
    --cluster "$ecs_cluster_name" \
    --service-name "$ecs_service_name" \
    --desired-status RUNNING \
    --query taskArns \
    --output json)"
  if [[ "$(jq 'length' <<<"$task_arns")" -ne 1 ]]; then
    echo "The target service must have exactly one running task." >&2
    exit 1
  fi
  tasks="$(aws_target ecs describe-tasks \
    --cluster "$ecs_cluster_name" \
    --tasks "$(jq -r '.[0]' <<<"$task_arns")" \
    --query tasks \
    --output json)"
  running_digest="$(jq -r '.[0].containers[] | select(.name == "api") | .imageDigest // empty' \
    <<<"$tasks")"
  if [[ "$running_digest" != "$arm64_digest" &&
        "$running_digest" != "$(jq -r '.TargetIndexDigest' <<<"$verification")" ]]; then
    echo "The running API container does not report the reviewed target index or its ARM64 child digest." >&2
    exit 1
  fi
  if ! jq -e \
    --arg capacity_provider "$ecs_capacity_provider_name" \
    'length == 1
     and .[0].lastStatus == "RUNNING"
     and .[0].capacityProviderName == $capacity_provider
     and (.[0].containerInstanceArn // "") != ""' \
    <<<"$tasks" \
    >/dev/null; then
    echo "The running task is not on the reviewed EC2 capacity provider." >&2
    exit 1
  fi
  container_instance_arn="$(jq -r '.[0].containerInstanceArn' <<<"$tasks")"
  container_instance="$(aws_target ecs describe-container-instances \
    --cluster "$ecs_cluster_name" \
    --container-instances "$container_instance_arn" \
    --query 'containerInstances[0]' \
    --output json)"
  ec2_instance_id="$(jq -r '.ec2InstanceId // empty' <<<"$container_instance")"
  if ! jq -e '.status == "ACTIVE" and .agentConnected == true' \
    <<<"$container_instance" \
    >/dev/null; then
    echo "The only registered ECS container instance is not active and connected." >&2
    exit 1
  fi
  instance="$(aws_target ec2 describe-instances \
    --instance-ids "$ec2_instance_id" \
    --query 'Reservations[0].Instances[0]' \
    --output json)"
  ec2_image="$(aws_target ec2 describe-images \
    --image-ids "$(jq -r '.ImageId // empty' <<<"$instance")" \
    --query 'Images[0]' \
    --output json)"
  if [[ "$instance" == "null" || "$ec2_image" == "null" ]] || ! jq -e \
    '.State.Name == "running" and .InstanceType == "t4g.nano"' \
    <<<"$instance" \
    >/dev/null || ! jq -e '.Architecture == "arm64"' \
    <<<"$ec2_image" \
    >/dev/null; then
    echo "The running task is not hosted on the reviewed ARM64 t4g.nano." >&2
    exit 1
  fi

  web_target_group="$(stack_resource_id "$service_stack" WebTargetGroup)"
  grpc_target_group="$(stack_resource_id "$service_stack" GrpcTargetGroup)"
  web_health="$(aws_target elbv2 describe-target-health \
    --target-group-arn "$web_target_group" \
    --query TargetHealthDescriptions \
    --output json)"
  grpc_health="$(aws_target elbv2 describe-target-health \
    --target-group-arn "$grpc_target_group" \
    --query TargetHealthDescriptions \
    --output json)"
  if ! jq -e --arg instance "$ec2_instance_id" \
    'length == 1
     and .[0].Target.Id == $instance
     and .[0].Target.Port == 8080
     and .[0].TargetHealth.State == "healthy"' \
    <<<"$web_health" \
    >/dev/null || ! jq -e --arg instance "$ec2_instance_id" \
    'length == 1
     and .[0].Target.Id == $instance
     and .[0].Target.Port == 8081
     and .[0].TargetHealth.State == "healthy"' \
    <<<"$grpc_health" \
    >/dev/null; then
    echo "The reviewed web and gRPC ALB targets are not both healthy on the running ECS host." >&2
    exit 1
  fi

  jq -n \
    --arg taskDefinitionArn "$task_definition_arn" \
    --arg runningDigest "$running_digest" \
    --arg ecsHost "$ec2_instance_id" \
    --arg webTargetGroup "$web_target_group" \
    --arg grpcTargetGroup "$grpc_target_group" \
    --argjson image "$verification" \
    '{
       Status: "Verified",
       TaskDefinitionArn: $taskDefinitionArn,
       RunningContainerDigest: $runningDigest,
       EcsHost: $ecsHost,
       HealthyTargetGroups: [$webTargetGroup, $grpcTargetGroup],
       Image: $image
     }'
}

stack_output() {
  local stack_name="$1"
  local output_key="$2"
  local stack_description value
  stack_description="$(aws_target cloudformation describe-stacks \
    --stack-name "$stack_name" \
    --query 'Stacks[0]' \
    --output json)"
  if [[ "$stack_description" == "null" ]] || ! jq -e \
    '.StackStatus
     | test("^(CREATE_COMPLETE|UPDATE_COMPLETE|UPDATE_ROLLBACK_COMPLETE)$")' \
    <<<"$stack_description" \
    >/dev/null; then
    echo "Stack ${stack_name} is missing or not in a settled successful state." >&2
    exit 1
  fi
  value="$(jq -r --arg key "$output_key" \
    '.Outputs[]? | select(.OutputKey == $key) | .OutputValue // empty' \
    <<<"$stack_description")"
  if [[ -z "$value" ]]; then
    echo "Stack ${stack_name} has no ${output_key} output." >&2
    exit 1
  fi
  echo "$value"
}

assert_stack_settled() {
  local stack_name="$1"
  local stack_description
  stack_description="$(aws_target cloudformation describe-stacks \
    --stack-name "$stack_name" \
    --query 'Stacks[0]' \
    --output json)"
  if [[ "$stack_description" == "null" ]] || ! jq -e \
    '.StackStatus
     | test("^(CREATE_COMPLETE|UPDATE_COMPLETE|UPDATE_ROLLBACK_COMPLETE)$")' \
    <<<"$stack_description" \
    >/dev/null; then
    echo "Stack ${stack_name} is missing or not in a settled successful state." >&2
    exit 1
  fi
}

stack_parameter() {
  local stack_name="$1"
  local parameter_key="$2"
  local stack_description value
  stack_description="$(aws_target cloudformation describe-stacks \
    --stack-name "$stack_name" \
    --query 'Stacks[0]' \
    --output json)"
  if [[ "$stack_description" == "null" ]] || ! jq -e \
    '.StackStatus
     | test("^(CREATE_COMPLETE|UPDATE_COMPLETE|UPDATE_ROLLBACK_COMPLETE)$")' \
    <<<"$stack_description" \
    >/dev/null; then
    echo "Stack ${stack_name} is missing or not in a settled successful state." >&2
    exit 1
  fi
  value="$(jq -r --arg key "$parameter_key" \
    '.Parameters[]? | select(.ParameterKey == $key) | .ParameterValue // empty' \
    <<<"$stack_description")"
  if [[ -z "$value" ]]; then
    echo "Stack ${stack_name} has no ${parameter_key} parameter." >&2
    exit 1
  fi
  echo "$value"
}

stack_resource_id() {
  local stack_name="$1"
  local logical_id="$2"
  local physical_id
  physical_id="$(aws_target cloudformation describe-stack-resource \
    --stack-name "$stack_name" \
    --logical-resource-id "$logical_id" \
    --query StackResourceDetail.PhysicalResourceId \
    --output text)"
  if [[ -z "$physical_id" || "$physical_id" == "None" ]]; then
    echo "Stack ${stack_name} has no physical resource for ${logical_id}." >&2
    exit 1
  fi
  echo "$physical_id"
}

security_group_rules() {
  local group_id="$1"
  aws_target ec2 describe-security-group-rules \
    --filters "Name=group-id,Values=${group_id}" \
    --query SecurityGroupRules \
    --output json
}

verify_deployed_security_boundaries() {
  [[ $# -eq 0 ]] || usage
  local vpc_id alb_group api_group database_id database bucket_name load_balancer_arn
  local public_subnet_a public_subnet_b
  local certificate_arn public_api_host provider_rate_limit certificate
  local expected_database_group database_group expected_database_subnet_a
  local expected_database_subnet_b database_route_table_id database_route_table
  local database_parameter_group database_parameters
  local alb_rules api_rules database_rules subnet_id
  local subnet route_tables container_instances container_instance ec2_instance_id instance
  local expected_instance_profile root_device_name volume_id volume
  local credit_specification nat_gateways
  local api_enis alb_enis load_balancer_suffix expected_alb_subnets
  local load_balancer listeners target_groups web_acl public_access policy_status
  local web_target_group grpc_target_group http_listener https_listener grpc_rule
  local web_health grpc_health listener_rule listener_attributes expected_csp website_result
  local web_acl_physical expected_web_acl_name expected_web_acl_id expected_web_acl_scope

  assert_stack_settled "$foundation_stack"
  assert_stack_settled "$service_stack"
  vpc_id="$(stack_output "$foundation_stack" VpcId)"
  public_subnet_a="$(stack_output "$foundation_stack" PublicSubnetAId)"
  public_subnet_b="$(stack_output "$foundation_stack" PublicSubnetBId)"
  alb_group="$(stack_output "$foundation_stack" AlbSecurityGroupId)"
  api_group="$(stack_output "$foundation_stack" ApiSecurityGroupId)"
  bucket_name="$(stack_output "$foundation_stack" ApplicationDataBucketName)"
  database_id="$(stack_resource_id "$foundation_stack" Database)"
  expected_database_group="$(stack_resource_id "$foundation_stack" DatabaseSecurityGroup)"
  expected_database_subnet_a="$(stack_resource_id "$foundation_stack" DatabaseSubnetA)"
  expected_database_subnet_b="$(stack_resource_id "$foundation_stack" DatabaseSubnetB)"
  database_route_table_id="$(stack_resource_id "$foundation_stack" DatabaseRouteTable)"
  database_parameter_group="$(stack_resource_id "$foundation_stack" DatabaseParameterGroup)"
  expected_instance_profile="$(stack_resource_id "$foundation_stack" ContainerInstanceProfile)"
  load_balancer_arn="$(stack_resource_id "$service_stack" LoadBalancer)"
  certificate_arn="$(stack_parameter "$service_stack" CertificateArn)"
  public_api_host="$(stack_parameter "$service_stack" PublicApiHost)"
  provider_rate_limit="$(stack_parameter "$service_stack" ProviderEnrollmentRateLimit)"
  if [[ ! "$provider_rate_limit" =~ ^[0-9]+$ ]]; then
    echo "The deployed WAF rate parameter is not an integer." >&2
    exit 1
  fi
  web_target_group="$(stack_resource_id "$service_stack" WebTargetGroup)"
  grpc_target_group="$(stack_resource_id "$service_stack" GrpcTargetGroup)"
  http_listener="$(stack_resource_id "$service_stack" HttpListener)"
  https_listener="$(stack_resource_id "$service_stack" HttpsListener)"
  grpc_rule="$(stack_resource_id "$service_stack" GrpcRule)"
  web_acl_physical="$(stack_resource_id "$service_stack" ProviderEnrollmentWebAcl)"
  IFS='|' read -r expected_web_acl_name expected_web_acl_id expected_web_acl_scope \
    <<<"$web_acl_physical"
  if [[ -z "$expected_web_acl_id" || "$expected_web_acl_scope" != "REGIONAL" ]]; then
    echo "The stack-managed WAF physical identifier is not in the reviewed name|id|REGIONAL form." >&2
    exit 1
  fi

  database="$(aws_target rds describe-db-instances \
    --db-instance-identifier "$database_id" \
    --query 'DBInstances[0]' \
    --output json)"
  if [[ "$database" == "null" ]] || ! jq -e \
    --arg database_group "$expected_database_group" \
    --arg parameter_group "$database_parameter_group" \
    --arg subnet_a "$expected_database_subnet_a" \
    --arg subnet_b "$expected_database_subnet_b" \
    '.PubliclyAccessible == false
     and .MultiAZ == false
     and .DBInstanceClass == "db.t4g.micro"
     and .Engine == "postgres"
     and .DBInstanceStatus == "available"
     and .StorageEncrypted == true
     and .Endpoint.Port == 5432
     and (.VpcSecurityGroups | length) == 1
     and .VpcSecurityGroups[0].VpcSecurityGroupId == $database_group
     and (.DBParameterGroups | length) == 1
     and .DBParameterGroups[0].DBParameterGroupName == $parameter_group
     and .DBParameterGroups[0].ParameterApplyStatus == "in-sync"
     and ([.DBSubnetGroup.Subnets[].SubnetIdentifier] | sort)
         == ([$subnet_a, $subnet_b] | sort)' \
    <<<"$database" \
    >/dev/null; then
    echo "RDS is missing or does not match the reviewed private Single-AZ shape." >&2
    exit 1
  fi
  database_group="$(jq -r '.VpcSecurityGroups[0].VpcSecurityGroupId' <<<"$database")"
  database_parameters="$(aws_target rds describe-db-parameters \
    --db-parameter-group-name "$database_parameter_group" \
    --query "Parameters[?ParameterName=='rds.force_ssl']" \
    --output json)"
  if ! jq -e \
    'length == 1 and .[0].ParameterValue == "1"' \
    <<<"$database_parameters" \
    >/dev/null; then
    echo "The database parameter group does not enforce TLS with rds.force_ssl=1." >&2
    exit 1
  fi

  alb_rules="$(security_group_rules "$alb_group")"
  api_rules="$(security_group_rules "$api_group")"
  database_rules="$(security_group_rules "$database_group")"
  if ! jq -e \
    --arg api "$api_group" \
    '
      ([.[] | select(.IsEgress == false)]) as $in
      | ([.[] | select(.IsEgress == true)]) as $out
      | ($in | length) == 2
        and ([$in[] | select(.IpProtocol == "tcp"
                             and .FromPort == 80 and .ToPort == 80
                             and .CidrIpv4 == "0.0.0.0/0")] | length) == 1
        and ([$in[] | select(.IpProtocol == "tcp"
                             and .FromPort == 443 and .ToPort == 443
                             and .CidrIpv4 == "0.0.0.0/0")] | length) == 1
        and ($out | length) == 2
        and ([$out[] | select(.IpProtocol == "tcp"
                              and .FromPort == 8080 and .ToPort == 8080
                              and .ReferencedGroupInfo.GroupId == $api)] | length) == 1
        and ([$out[] | select(.IpProtocol == "tcp"
                              and .FromPort == 8081 and .ToPort == 8081
                              and .ReferencedGroupInfo.GroupId == $api)] | length) == 1
    ' \
    <<<"$alb_rules" \
    >/dev/null; then
    echo "The ALB security group is not limited to public 80/443 ingress and API-only 8080/8081 egress." >&2
    exit 1
  fi
  if ! jq -e \
    --arg alb "$alb_group" \
    '
      ([.[] | select(.IsEgress == false)]) as $in
      | ($in | length) == 2
        and ([$in[] | select(.IpProtocol == "tcp"
                             and .FromPort == 8080 and .ToPort == 8080
                             and .ReferencedGroupInfo.GroupId == $alb)] | length) == 1
        and ([$in[] | select(.IpProtocol == "tcp"
                             and .FromPort == 8081 and .ToPort == 8081
                             and .ReferencedGroupInfo.GroupId == $alb)] | length) == 1
    ' \
    <<<"$api_rules" \
    >/dev/null; then
    echo "The ECS host security group permits ingress other than ALB-only ports 8080 and 8081." >&2
    exit 1
  fi
  if ! jq -e \
    --arg api "$api_group" \
    '
      ([.[] | select(.IsEgress == false)]) as $in
      | ($in | length) == 1
        and $in[0].IpProtocol == "tcp"
        and $in[0].FromPort == 5432
        and $in[0].ToPort == 5432
        and $in[0].ReferencedGroupInfo.GroupId == $api
    ' \
    <<<"$database_rules" \
    >/dev/null; then
    echo "The database security group is not limited to PostgreSQL from the ECS host security group." >&2
    exit 1
  fi

  while IFS= read -r subnet_id; do
    [[ -n "$subnet_id" ]] || continue
    subnet="$(aws_target ec2 describe-subnets \
      --subnet-ids "$subnet_id" \
      --query 'Subnets[0]' \
      --output json)"
    if [[ "$subnet" == "null" ]] || ! jq -e --arg vpc "$vpc_id" \
      '.MapPublicIpOnLaunch == false and .VpcId == $vpc' \
      <<<"$subnet" \
      >/dev/null; then
      echo "Database subnet ${subnet_id} permits public-IP mapping." >&2
      exit 1
    fi
    route_tables="$(aws_target ec2 describe-route-tables \
      --filters "Name=association.subnet-id,Values=${subnet_id}" \
      --query RouteTables \
      --output json)"
    if ! jq -e --arg route_table "$database_route_table_id" \
      'length == 1 and .[0].RouteTableId == $route_table' \
      <<<"$route_tables" \
      >/dev/null; then
      echo "Database subnet ${subnet_id} is not explicitly associated with the reviewed isolated route table." >&2
      exit 1
    fi
  done < <(jq -r '.DBSubnetGroup.Subnets[].SubnetIdentifier' <<<"$database")
  database_route_table="$(aws_target ec2 describe-route-tables \
    --route-table-ids "$database_route_table_id" \
    --query 'RouteTables[0]' \
    --output json)"
  if [[ "$database_route_table" == "null" ]] || ! jq -e \
    --arg vpc "$vpc_id" \
    --arg subnet_a "$expected_database_subnet_a" \
    --arg subnet_b "$expected_database_subnet_b" \
    '.VpcId == $vpc
     and ([.Associations[]? | .SubnetId // empty] | sort)
         == ([$subnet_a, $subnet_b] | sort)
     and (.Associations | length) == 2
     and all(.Associations[]?; .AssociationState.State == "associated")
     and (.Routes | length) == 1
     and .Routes[0].GatewayId == "local"
     and .Routes[0].State == "active"' \
    <<<"$database_route_table" \
    >/dev/null; then
    echo "The reviewed database route table is not local-only or is associated with unexpected subnets." >&2
    exit 1
  fi

  container_instances="$(aws_target ecs list-container-instances \
    --cluster "$ecs_cluster_name" \
    --query containerInstanceArns \
    --output json)"
  if [[ "$(jq 'length' <<<"$container_instances")" -ne 1 ]]; then
    echo "Expected exactly one active target ECS container instance." >&2
    exit 1
  fi
  container_instance="$(aws_target ecs describe-container-instances \
    --cluster "$ecs_cluster_name" \
    --container-instances "$(jq -r '.[0]' <<<"$container_instances")" \
    --query 'containerInstances[0]' \
    --output json)"
  ec2_instance_id="$(jq -r '.ec2InstanceId // empty' <<<"$container_instance")"
  instance="$(aws_target ec2 describe-instances \
    --instance-ids "$ec2_instance_id" \
    --query 'Reservations[0].Instances[0]' \
    --output json)"
  if [[ "$instance" == "null" ]] || ! jq -e \
    --arg api "$api_group" \
    --arg instance_profile "$expected_instance_profile" \
    '.State.Name == "running"
     and .InstanceType == "t4g.nano"
     and (.KeyName == null)
     and (.PublicIpAddress // "") != ""
     and (.IamInstanceProfile.Arn | endswith("/" + $instance_profile))
     and .MetadataOptions.State == "applied"
     and .MetadataOptions.HttpEndpoint == "enabled"
     and .MetadataOptions.HttpTokens == "required"
     and .MetadataOptions.HttpPutResponseHopLimit == 1
     and .MetadataOptions.InstanceMetadataTags == "disabled"
     and (.SecurityGroups | length) == 1
     and .SecurityGroups[0].GroupId == $api
     and (.NetworkInterfaces | length) == 1
     and .NetworkInterfaces[0].Association.PublicIp == .PublicIpAddress
     and (.NetworkInterfaces[0].Groups | length) == 1
     and .NetworkInterfaces[0].Groups[0].GroupId == $api' \
    <<<"$instance" \
    >/dev/null; then
    echo "The ECS host is not the reviewed keyless t4g.nano with only the API security group." >&2
    exit 1
  fi
  root_device_name="$(jq -r '.RootDeviceName // empty' <<<"$instance")"
  volume_id="$(jq -r --arg root "$root_device_name" \
    '.BlockDeviceMappings[] | select(.DeviceName == $root) | .Ebs.VolumeId // empty' \
    <<<"$instance")"
  if [[ -z "$root_device_name" || -z "$volume_id" ]]; then
    echo "The ECS host root volume could not be resolved." >&2
    exit 1
  fi
  volume="$(aws_target ec2 describe-volumes \
    --volume-ids "$volume_id" \
    --query 'Volumes[0]' \
    --output json)"
  credit_specification="$(aws_target ec2 describe-instance-credit-specifications \
    --instance-ids "$ec2_instance_id" \
    --query 'InstanceCreditSpecifications[0]' \
    --output json)"
  if [[ "$volume" == "null" ]] || ! jq -e \
    '.Encrypted == true and .VolumeType == "gp3" and .State == "in-use"' \
    <<<"$volume" \
    >/dev/null || [[ "$credit_specification" == "null" ]] || ! jq -e \
    '.CpuCredits == "standard"' \
    <<<"$credit_specification" \
    >/dev/null; then
    echo "The ECS host does not have the reviewed encrypted gp3 root volume and standard T4g credits." >&2
    exit 1
  fi
  nat_gateways="$(aws_target ec2 describe-nat-gateways \
    --filter "Name=vpc-id,Values=${vpc_id}" \
    --query "NatGateways[?State!='deleted']" \
    --output json)"
  if [[ "$(jq 'length' <<<"$nat_gateways")" -ne 0 ]]; then
    echo "A NAT gateway exists in the reviewed no-NAT target VPC." >&2
    exit 1
  fi
  api_enis="$(aws_target ec2 describe-network-interfaces \
    --filters "Name=group-id,Values=${api_group}" \
    --query NetworkInterfaces \
    --output json)"
  if ! jq -e \
    --arg instance "$ec2_instance_id" \
    --arg api "$api_group" \
    'length == 1
     and .[0].Status == "in-use"
     and .[0].Attachment.InstanceId == $instance
     and (.[0].Groups | length) == 1
     and .[0].Groups[0].GroupId == $api' \
    <<<"$api_enis" \
    >/dev/null; then
    echo "The API security group is attached to an unexpected or additional network interface." >&2
    exit 1
  fi

  load_balancer="$(aws_target elbv2 describe-load-balancers \
    --load-balancer-arns "$load_balancer_arn" \
    --query 'LoadBalancers[0]' \
    --output json)"
  if [[ "$load_balancer" == "null" ]] || ! jq -e \
    --arg alb "$alb_group" \
    --arg vpc "$vpc_id" \
    --arg subnet_a "$public_subnet_a" \
    --arg subnet_b "$public_subnet_b" \
    '.Scheme == "internet-facing"
     and .Type == "application"
     and .VpcId == $vpc
     and .IpAddressType == "ipv4"
     and ([.AvailabilityZones[].SubnetId] | sort) == ([$subnet_a, $subnet_b] | sort)
     and (.SecurityGroups | length) == 1
     and .SecurityGroups[0] == $alb' \
    <<<"$load_balancer" \
    >/dev/null; then
    echo "The load balancer does not have the reviewed public ALB and single-SG shape." >&2
    exit 1
  fi
  load_balancer_suffix="${load_balancer_arn#*loadbalancer/}"
  expected_alb_subnets="$(jq -n \
    --arg subnet_a "$public_subnet_a" \
    --arg subnet_b "$public_subnet_b" \
    '[$subnet_a, $subnet_b] | sort')"
  alb_enis="$(aws_target ec2 describe-network-interfaces \
    --filters "Name=group-id,Values=${alb_group}" \
    --query NetworkInterfaces \
    --output json)"
  if ! jq -e \
    --arg alb "$alb_group" \
    --arg vpc "$vpc_id" \
    --arg owner "$target_account" \
    --arg description "ELB ${load_balancer_suffix}" \
    --argjson subnets "$expected_alb_subnets" \
    'length >= 2
     and all(.[];
       .Status == "in-use"
       and .RequesterManaged == true
       # Application Load Balancer ENIs are requester-managed but the EC2 API
       # reports their interface type as the generic "interface" value.
       and .InterfaceType == "interface"
       and .OwnerId == $owner
       and .VpcId == $vpc
       and .Description == $description
       and (.Groups | length) == 1
       and .Groups[0].GroupId == $alb
       and ((.SubnetId as $subnet | $subnets | index($subnet)) != null))
     and ([.[].SubnetId] | unique | sort) == $subnets' \
    <<<"$alb_enis" \
    >/dev/null; then
    echo "The ALB security group is attached to an unexpected network interface." >&2
    exit 1
  fi
  listeners="$(aws_target elbv2 describe-listeners \
    --load-balancer-arn "$load_balancer_arn" \
    --query Listeners \
    --output json)"
  if ! jq -e \
    --arg http "$http_listener" \
    --arg https "$https_listener" \
    --arg web "$web_target_group" \
    --arg certificate "$certificate_arn" \
    --arg ssl_policy "$reviewed_alb_ssl_policy" \
    'length == 2
     and ([.[]
           | select(.ListenerArn == $http
                    and .Port == 80
                    and .Protocol == "HTTP"
                    and (.DefaultActions | length) == 1
                    and .DefaultActions[0].Type == "redirect"
                    and .DefaultActions[0].RedirectConfig.Protocol == "HTTPS"
                    and .DefaultActions[0].RedirectConfig.Port == "443"
                    and .DefaultActions[0].RedirectConfig.StatusCode == "HTTP_301")]
          | length) == 1
     and ([.[]
           | select(.ListenerArn == $https
                    and .Port == 443
                    and .Protocol == "HTTPS"
                    and .SslPolicy == $ssl_policy
                    and (.Certificates | length) == 1
                    and .Certificates[0].CertificateArn == $certificate
                    and (.DefaultActions | length) == 1
                    and .DefaultActions[0].Type == "forward"
                    and .DefaultActions[0].TargetGroupArn == $web)]
          | length) == 1' \
    <<<"$listeners" \
    >/dev/null; then
    echo "The ALB listeners are not limited to HTTP redirect and HTTPS." >&2
    exit 1
  fi
  certificate="$(aws_target acm describe-certificate \
    --certificate-arn "$certificate_arn" \
    --query Certificate \
    --output json)"
  if [[ "$certificate" == "null" ]] || ! jq -e \
    --arg host "$public_api_host" \
    --arg load_balancer "$load_balancer_arn" \
    '.Status == "ISSUED"
     and (([.DomainName, .SubjectAlternativeNames[]?] | index($host)) != null)
     and ((.InUseBy | index($load_balancer)) != null)' \
    <<<"$certificate" \
    >/dev/null; then
    echo "The HTTPS listener certificate is not issued for the reviewed public hostname and target ALB." >&2
    exit 1
  fi
  expected_csp="default-src 'self'; script-src 'self' 'wasm-unsafe-eval' https://cdn.jsdelivr.net; style-src 'self'; style-src-attr 'unsafe-inline'; img-src 'self' blob: https://api.producthunt.com https://${bucket_name}.s3.${target_region}.amazonaws.com; connect-src 'self' https://cdn.jsdelivr.net https://huggingface.co https://*.hf.co https://*.xethub.hf.co; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'"
  listener_attributes="$(aws_target elbv2 describe-listener-attributes \
    --listener-arn "$https_listener" \
    --query Attributes \
    --output json)"
  if ! jq -e \
    --arg csp "$expected_csp" \
    'any(.[];
      .Key == "routing.http.response.content_security_policy.header_value"
      and .Value == $csp)' \
    <<<"$listener_attributes" \
    >/dev/null; then
    echo "The HTTPS listener is not overriding the historical source-bucket CSP with the exact target S3 origin." >&2
    exit 1
  fi
  target_groups="$(aws_target elbv2 describe-target-groups \
    --load-balancer-arn "$load_balancer_arn" \
    --query TargetGroups \
    --output json)"
  if ! jq -e \
    --arg web "$web_target_group" \
    --arg grpc "$grpc_target_group" \
    --arg vpc "$vpc_id" \
    'length == 2
     and ([.[]
           | select(.TargetGroupArn == $web
                    and .TargetType == "instance"
                    and .VpcId == $vpc
                    and .Protocol == "HTTP"
                    and .ProtocolVersion == "HTTP1"
                    and .Port == 8080)]
          | length) == 1
     and ([.[]
           | select(.TargetGroupArn == $grpc
                    and .TargetType == "instance"
                    and .VpcId == $vpc
                    and .Protocol == "HTTP"
                    and .ProtocolVersion == "GRPC"
                    and .Port == 8081)]
          | length) == 1' \
    <<<"$target_groups" \
    >/dev/null; then
    echo "The ALB target groups are not limited to instance ports 8080 and 8081." >&2
    exit 1
  fi
  web_health="$(aws_target elbv2 describe-target-health \
    --target-group-arn "$web_target_group" \
    --query TargetHealthDescriptions \
    --output json)"
  grpc_health="$(aws_target elbv2 describe-target-health \
    --target-group-arn "$grpc_target_group" \
    --query TargetHealthDescriptions \
    --output json)"
  if ! jq -e --arg instance "$ec2_instance_id" \
    'length == 1
     and .[0].Target.Id == $instance
     and .[0].Target.Port == 8080
     and .[0].TargetHealth.State == "healthy"' \
    <<<"$web_health" \
    >/dev/null || ! jq -e --arg instance "$ec2_instance_id" \
    'length == 1
     and .[0].Target.Id == $instance
     and .[0].Target.Port == 8081
     and .[0].TargetHealth.State == "healthy"' \
    <<<"$grpc_health" \
    >/dev/null; then
    echo "The web and gRPC target groups are not both healthy on the only ECS host." >&2
    exit 1
  fi
  listener_rule="$(aws_target elbv2 describe-rules \
    --rule-arns "$grpc_rule" \
    --query 'Rules[0]' \
    --output json)"
  if [[ "$listener_rule" == "null" ]] || ! jq -e \
    --arg grpc "$grpc_target_group" \
    '.Priority == "10"
     and (.Actions | length) == 1
     and .Actions[0].Type == "forward"
     and .Actions[0].TargetGroupArn == $grpc
     and (.Conditions | length) == 1
     and .Conditions[0].Field == "path-pattern"
     and .Conditions[0].PathPatternConfig.Values
         == ["/mutualgpu.v1.ProviderControl/*"]' \
    <<<"$listener_rule" \
    >/dev/null; then
    echo "The gRPC listener rule does not exclusively forward the reviewed path to the gRPC target group." >&2
    exit 1
  fi
  web_acl="$(aws_target wafv2 get-web-acl-for-resource \
    --resource-arn "$load_balancer_arn" \
    --query WebACL \
    --output json)"
  if [[ "$web_acl" == "null" ]] || ! jq -e \
    --arg name "$expected_web_acl_name" \
    --arg id "$expected_web_acl_id" \
    --argjson rate_limit "$provider_rate_limit" \
    '
      .Name == $name
      and .Id == $id
      and .DefaultAction.Allow == {}
      and (.Rules | length) == 1
      and .Rules[0].Name == "provider-enrollment-rate-limit"
      and .Rules[0].Priority == 0
      and .Rules[0].Action.Block.CustomResponse.ResponseCode == 429
      and .Rules[0].Statement.RateBasedStatement.AggregateKeyType == "IP"
      and .Rules[0].Statement.RateBasedStatement.Limit == $rate_limit
      and .Rules[0].Statement.RateBasedStatement.EvaluationWindowSec == 60
      and (
        [.Rules[0].Statement.RateBasedStatement.ScopeDownStatement.AndStatement.Statements[]?
         | select(.ByteMatchStatement.FieldToMatch.Method == {}
                  and .ByteMatchStatement.PositionalConstraint == "EXACTLY"
                  and (.ByteMatchStatement.SearchString == "POST"
                       or .ByteMatchStatement.SearchString == "UE9TVA=="))]
        | length
      ) == 1
      and (
        [.Rules[0].Statement.RateBasedStatement.ScopeDownStatement.AndStatement.Statements[]?
         | select(.ByteMatchStatement.FieldToMatch.UriPath == {}
                  and .ByteMatchStatement.PositionalConstraint == "EXACTLY"
                  and (.ByteMatchStatement.SearchString == "/api/webgpu-enrollments"
                       or .ByteMatchStatement.SearchString
                          == "L2FwaS93ZWJncHUtZW5yb2xsbWVudHM="))]
        | length
      ) == 1
    ' \
    <<<"$web_acl" \
    >/dev/null; then
    echo "The exact stack-managed enrollment-rate WAF is not associated with the target ALB." >&2
    exit 1
  fi

  public_access="$(aws_target s3api get-public-access-block \
    --bucket "$bucket_name" \
    --query PublicAccessBlockConfiguration \
    --output json)"
  policy_status="$(aws_target s3api get-bucket-policy-status \
    --bucket "$bucket_name" \
    --query PolicyStatus \
    --output json)"
  if ! jq -e \
    '.BlockPublicAcls == true
     and .IgnorePublicAcls == true
     and .BlockPublicPolicy == true
     and .RestrictPublicBuckets == true' \
    <<<"$public_access" \
    >/dev/null || ! jq -e '.IsPublic == false' \
    <<<"$policy_status" \
    >/dev/null; then
    echo "The target application bucket is not fully blocked from public access." >&2
    exit 1
  fi
  if website_result="$(aws_target s3api get-bucket-website \
    --bucket "$bucket_name" \
    2>&1)"; then
    echo "The target application bucket unexpectedly has website hosting enabled." >&2
    exit 1
  fi
  if [[ "$website_result" != *"(NoSuchWebsiteConfiguration)"* ]]; then
    echo "The target application bucket website status could not be verified safely." >&2
    exit 1
  fi

  jq -n \
    --arg albSecurityGroup "$alb_group" \
    --arg apiSecurityGroup "$api_group" \
    --arg databaseSecurityGroup "$database_group" \
    --arg database "$database_id" \
    --arg ecsHost "$ec2_instance_id" \
    --arg loadBalancer "$load_balancer_arn" \
    --arg bucket "$bucket_name" \
    '{
       Status: "Verified",
       PublicIngress: "ALB TCP 80/443 only",
       AlbSecurityGroup: $albSecurityGroup,
       EcsHostSecurityGroup: $apiSecurityGroup,
       DatabaseSecurityGroup: $databaseSecurityGroup,
       Database: $database,
       EcsHost: $ecsHost,
       LoadBalancer: $loadBalancer,
       ApplicationBucket: $bucket
     }'
}

validate_templates() {
  aws_target cloudformation validate-template --template-body file://deploy/aws/foundation.yaml >/dev/null
  aws_target cloudformation validate-template --template-body file://deploy/aws/service.yaml >/dev/null
  echo "Validated both MutualGPU templates in ${target_account}/${target_region}."
}

change_set_type() {
  local stack_name="$1"
  if aws_target cloudformation describe-stacks --stack-name "$stack_name" >/dev/null 2>&1; then
    local stack_status
    stack_status="$(aws_target cloudformation describe-stacks --stack-name "$stack_name" --query 'Stacks[0].StackStatus' --output text)"
    case "$stack_status" in
      REVIEW_IN_PROGRESS)
        printf 'CREATE'
        ;;
      CREATE_COMPLETE|UPDATE_COMPLETE|UPDATE_ROLLBACK_COMPLETE)
        printf 'UPDATE'
        ;;
      *"_IN_PROGRESS")
        echo "Stack ${stack_name} is ${stack_status}; wait for it to settle." >&2
        exit 1
        ;;
      *)
        echo "Stack ${stack_name} is ${stack_status}; refusing to infer a safe change-set type." >&2
        exit 1
        ;;
    esac
  else
    printf 'CREATE'
  fi
}

show_change_set() {
  local stack_name="$1"
  local change_set_name="$2"
  aws_target cloudformation describe-change-set \
    --stack-name "$stack_name" \
    --change-set-name "$change_set_name" \
    --query '{ChangeSetId:ChangeSetId,StackId:StackId,Status:Status,ExecutionStatus:ExecutionStatus,Parameters:Parameters,Changes:Changes[*].ResourceChange.{Action:Action,LogicalResourceId:LogicalResourceId,ResourceType:ResourceType,Replacement:Replacement,Scope:Scope}}' \
    --output json |
    jq 'if .Parameters then
          .Parameters |= map(if (.ParameterKey == "DeploymentSecretArn"
                                 or .ParameterKey == "MigrationDatabaseSecretVersionId"
                                 or .ParameterKey == "MigrationDeploymentSecretVersionId"
                                 or .ParameterKey == "MaintenanceOperatorIpv4Cidr")
                             then .ParameterValue = "[redacted]"
                             else . end)
        else . end'
}

create_foundation_change_set() {
  [[ $# -eq 1 ]] || usage
  local change_set_name="$1"
  local application_bucket="${MUTUALGPU_APPLICATION_DATA_BUCKET_NAME:?Set MUTUALGPU_APPLICATION_DATA_BUCKET_NAME to the reviewed globally unique target bucket.}"
  local primary_az="${MUTUALGPU_PRIMARY_AZ:?Set MUTUALGPU_PRIMARY_AZ to the reviewed target primary AZ.}"
  local secondary_az="${MUTUALGPU_SECONDARY_AZ:?Set MUTUALGPU_SECONDARY_AZ to the reviewed distinct target secondary AZ.}"
  local storage_autoscaling="${MUTUALGPU_DATABASE_STORAGE_AUTOSCALING:-disabled}"
  local ecs_host_count="${MUTUALGPU_ECS_HOST_COUNT:-0}"
  local stack_type
  if [[ "$application_bucket" == "mutualgpu-data" ]]; then
    echo "The globally scoped source bucket name mutualgpu-data is forbidden." >&2
    exit 1
  fi
  stack_type="$(change_set_type "$foundation_stack")"

  local parameters=(
    "ParameterKey=ApplicationDataBucketName,ParameterValue=${application_bucket}"
    "ParameterKey=PrimaryAvailabilityZone,ParameterValue=${primary_az}"
    "ParameterKey=SecondaryAvailabilityZone,ParameterValue=${secondary_az}"
    "ParameterKey=VpcCidr,ParameterValue=${MUTUALGPU_VPC_CIDR:-10.42.0.0/16}"
    "ParameterKey=PublicSubnetACidr,ParameterValue=${MUTUALGPU_PUBLIC_SUBNET_A_CIDR:-10.42.0.0/20}"
    "ParameterKey=PublicSubnetBCidr,ParameterValue=${MUTUALGPU_PUBLIC_SUBNET_B_CIDR:-10.42.16.0/20}"
    "ParameterKey=DatabaseSubnetACidr,ParameterValue=${MUTUALGPU_DATABASE_SUBNET_A_CIDR:-10.42.32.0/20}"
    "ParameterKey=DatabaseSubnetBCidr,ParameterValue=${MUTUALGPU_DATABASE_SUBNET_B_CIDR:-10.42.48.0/20}"
    "ParameterKey=DatabaseEngineVersion,ParameterValue=${MUTUALGPU_DATABASE_ENGINE_VERSION:-18.4}"
    "ParameterKey=DatabaseStorageAutoscaling,ParameterValue=${storage_autoscaling}"
    "ParameterKey=EcsHostCount,ParameterValue=${ecs_host_count}"
  )

  echo "Creating ${stack_type} change set ${change_set_name} for ${foundation_stack}."
  printf '  %s\n' "${parameters[@]}"
  aws_target cloudformation create-change-set \
    --stack-name "$foundation_stack" \
    --change-set-name "$change_set_name" \
    --change-set-type "$stack_type" \
    --description "Reviewed MutualGPU clean-start foundation" \
    --template-body file://deploy/aws/foundation.yaml \
    --parameters "${parameters[@]}" \
    --capabilities CAPABILITY_NAMED_IAM \
    --tags \
      Key=Application,Value=MutualGPU \
      Key=Project,Value=MutualGPU \
      Key=Environment,Value=Production \
      Key=ManagedBy,Value=CloudFormation \
      Key=Owner,Value=Quinn-MutualCompute \
      Key=Component,Value=Migration \
      Key=Lifecycle,Value=Persistent \
    >/dev/null

  aws_target cloudformation wait change-set-create-complete \
    --stack-name "$foundation_stack" \
    --change-set-name "$change_set_name"
  show_change_set "$foundation_stack" "$change_set_name"
}

create_service_change_set() {
  [[ $# -eq 1 ]] || usage
  local change_set_name="$1"
  local image_uri="${MUTUALGPU_IMAGE_URI:?Set MUTUALGPU_IMAGE_URI to the reviewed target ECR URI pinned by digest.}"
  local certificate_arn="${MUTUALGPU_CERTIFICATE_ARN:?Set MUTUALGPU_CERTIFICATE_ARN to the reviewed target ACM certificate ARN.}"
  local deployment_secret_arn="${MUTUALGPU_DEPLOYMENT_SECRET_ARN:?Set MUTUALGPU_DEPLOYMENT_SECRET_ARN to the target secret ARN without exposing its value.}"
  local active_legacy_task_definition_arn="${MUTUALGPU_ACTIVE_LEGACY_TASK_DEFINITION_ARN:?Set MUTUALGPU_ACTIVE_LEGACY_TASK_DEFINITION_ARN to the exact current legacy task definition ARN.}"
  local database_secret_arn database_secret_version_id deployment_secret_version_id
  database_secret_arn="$(aws_target cloudformation describe-stacks --stack-name "$foundation_stack" --query "Stacks[0].Outputs[?OutputKey=='DatabaseSecretArn'].OutputValue | [0]" --output text)"
  if [[ -z "$database_secret_arn" || "$database_secret_arn" == "None" ]]; then
    echo "The target foundation stack did not expose a database secret ARN for attestation." >&2
    exit 1
  fi
  database_secret_version_id="${MUTUALGPU_MIGRATION_DATABASE_SECRET_VERSION_ID:-$(aws_target secretsmanager describe-secret --secret-id "$database_secret_arn" --query VersionIdsToStages --output json | jq -r 'to_entries | map(select(.value | index("AWSCURRENT"))) | if length == 1 then .[0].key else empty end')}"
  deployment_secret_version_id="${MUTUALGPU_MIGRATION_DEPLOYMENT_SECRET_VERSION_ID:-$(aws_target secretsmanager describe-secret --secret-id "$deployment_secret_arn" --query VersionIdsToStages --output json | jq -r 'to_entries | map(select(.value | index("AWSCURRENT"))) | if length == 1 then .[0].key else empty end')}"
  if [[ -z "$database_secret_version_id" || -z "$deployment_secret_version_id" ]]; then
    echo "The required target secret metadata did not contain exactly one current version." >&2
    exit 1
  fi
  local stack_type
  stack_type="$(change_set_type "$service_stack")"
  echo "Verifying the reviewed target image and its complete immutable-release provenance."
  verify_release_image "$image_uri" >/dev/null

  local parameters=(
    "ParameterKey=FoundationStackName,ParameterValue=${foundation_stack}"
    "ParameterKey=ImageUri,ParameterValue=${image_uri}"
    "ParameterKey=CertificateArn,ParameterValue=${certificate_arn}"
    "ParameterKey=PublicApiHost,ParameterValue=${MUTUALGPU_DOMAIN:-mutualgpu.com}"
    "ParameterKey=DeploymentSecretArn,ParameterValue=${deployment_secret_arn}"
    "ParameterKey=MigrationDatabaseSecretVersionId,ParameterValue=${database_secret_version_id}"
    "ParameterKey=MigrationDeploymentSecretVersionId,ParameterValue=${deployment_secret_version_id}"
    "ParameterKey=DesiredCount,ParameterValue=${MUTUALGPU_DESIRED_COUNT:-1}"
    "ParameterKey=PostgresRuntimeMode,ParameterValue=${MUTUALGPU_POSTGRES_RUNTIME_MODE:-false}"
    "ParameterKey=ActiveLegacyTaskDefinitionArn,ParameterValue=${active_legacy_task_definition_arn}"
    "ParameterKey=MaintenanceMode,ParameterValue=${MUTUALGPU_MAINTENANCE_MODE:-false}"
    "ParameterKey=MaintenanceOperatorIpv4Cidr,ParameterValue=${MUTUALGPU_MAINTENANCE_OPERATOR_IPV4_CIDR:-192.0.2.1/32}"
    "ParameterKey=ProviderCorsOrigin0,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_0:-https://huggingface.co}"
    "ParameterKey=ProviderCorsOrigin1,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_1:-https://vercel.com}"
    "ParameterKey=ProviderCorsOrigin2,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_2:-https://yosun-triposplat-webgpu-demo.static.hf.space}"
    "ParameterKey=ProviderEnrollmentRateLimit,ParameterValue=${MUTUALGPU_PROVIDER_ENROLLMENT_RATE_LIMIT:-10}"
    "ParameterKey=AllowArbitraryBrowserTaskWriteOrigins,ParameterValue=${MUTUALGPU_ALLOW_ARBITRARY_BROWSER_TASK_WRITE_ORIGINS:-true}"
  )

  echo "Creating ${stack_type} change set ${change_set_name} for ${service_stack}."
  for parameter in "${parameters[@]}"; do
    if [[ "$parameter" == ParameterKey=DeploymentSecretArn,* || "$parameter" == ParameterKey=MigrationDatabaseSecretVersionId,* || "$parameter" == ParameterKey=MigrationDeploymentSecretVersionId,* || "$parameter" == ParameterKey=MaintenanceOperatorIpv4Cidr,* ]]; then
      echo "  ${parameter%%,*},ParameterValue=[redacted]"
    else
      echo "  ${parameter}"
    fi
  done
  aws_target cloudformation create-change-set \
    --stack-name "$service_stack" \
    --change-set-name "$change_set_name" \
    --change-set-type "$stack_type" \
    --description "Reviewed MutualGPU clean-start EC2 service" \
    --template-body file://deploy/aws/service.yaml \
    --parameters "${parameters[@]}" \
    --tags \
      Key=Application,Value=MutualGPU \
      Key=Project,Value=MutualGPU \
      Key=Environment,Value=Production \
      Key=ManagedBy,Value=CloudFormation \
      Key=Owner,Value=Quinn-MutualCompute \
      Key=Component,Value=Migration \
      Key=Lifecycle,Value=Persistent \
    >/dev/null

  aws_target cloudformation wait change-set-create-complete \
    --stack-name "$service_stack" \
    --change-set-name "$change_set_name"
  show_change_set "$service_stack" "$change_set_name"
}

live_task_family_references() {
  local cluster_arns='[]'
  local live_task_definitions='[]'
  local cluster_arn
  cluster_arns="$(aws_target ecs list-clusters \
    --query clusterArns \
    --output json)"

  while IFS= read -r cluster_arn; do
    [[ -n "$cluster_arn" ]] || continue
    local service_arns task_arns service_arn task_arn
    service_arns="$(aws_target ecs list-services \
      --cluster "$cluster_arn" \
      --query serviceArns \
      --output json)"
    while IFS= read -r service_arn; do
      [[ -n "$service_arn" ]] || continue
      local described_service service_task_definition service_name service_cluster
      described_service="$(aws_target ecs describe-services \
        --cluster "$cluster_arn" \
        --services "$service_arn" \
        --query 'services[0]' \
        --output json)"
      service_task_definition="$(jq -r '.taskDefinition // empty' <<<"$described_service")"
      [[ "$service_task_definition" == "$ecs_task_definition_prefix"* ]] || continue
      service_name="$(jq -r '.serviceName // empty' <<<"$described_service")"
      service_cluster="$(jq -r '.clusterArn // empty' <<<"$described_service")"
      if [[ "$service_name" != "$ecs_service_name" || "$service_cluster" != "$ecs_cluster_arn" ]]; then
        echo "Task family ${ecs_task_family} is referenced by unexpected service ${service_arn}; refusing retention." >&2
        exit 1
      fi
    done < <(jq -r '.[]' <<<"$service_arns")

    task_arns="$(aws_target ecs list-tasks \
      --cluster "$cluster_arn" \
      --family "$ecs_task_family" \
      --desired-status RUNNING \
      --query taskArns \
      --output json)"
    while IFS= read -r task_arn; do
      [[ -n "$task_arn" ]] || continue
      local described_task task_definition_arn task_group task_cluster
      described_task="$(aws_target ecs describe-tasks \
        --cluster "$cluster_arn" \
        --tasks "$task_arn" \
        --query 'tasks[0]' \
        --output json)"
      task_definition_arn="$(jq -r '.taskDefinitionArn // empty' <<<"$described_task")"
      [[ "$task_definition_arn" == "$ecs_task_definition_prefix"* ]] || continue
      task_group="$(jq -r '.group // empty' <<<"$described_task")"
      task_cluster="$(jq -r '.clusterArn // empty' <<<"$described_task")"
      if [[ "$task_group" != "service:${ecs_service_name}" || "$task_cluster" != "$ecs_cluster_arn" ]]; then
        echo "Task family ${ecs_task_family} has unexpected live consumer ${task_arn}; refusing retention." >&2
        exit 1
      fi
      live_task_definitions="$(jq --arg arn "$task_definition_arn" \
        '. + [$arn] | unique' \
        <<<"$live_task_definitions")"
    done < <(jq -r '.[]' <<<"$task_arns")
  done < <(jq -r '.[]' <<<"$cluster_arns")

  echo "$live_task_definitions"
}

task_definition_retention_plan() {
  local active_task_definitions inactive_task_definitions all_task_definitions
  local service_description current_service_task_definition service_references
  local live_task_references successful_releases plan
  active_task_definitions="$(aws_target ecs list-task-definitions \
    --family-prefix "$ecs_task_family" \
    --status ACTIVE \
    --sort DESC \
    --query taskDefinitionArns \
    --output json)"
  inactive_task_definitions="$(aws_target ecs list-task-definitions \
    --family-prefix "$ecs_task_family" \
    --status INACTIVE \
    --sort DESC \
    --query taskDefinitionArns \
    --output json)"
  all_task_definitions="$(jq -n \
    --arg prefix "$ecs_task_definition_prefix" \
    --argjson active "$active_task_definitions" \
    --argjson inactive "$inactive_task_definitions" \
    '
      def revision: split(":") | last | tonumber;
      ($active + $inactive)
      | map(select(startswith($prefix)))
      | unique
      | sort_by(revision)
      | reverse
    ')"
  service_description="$(aws_target ecs describe-services \
    --cluster "$ecs_cluster_name" \
    --services "$ecs_service_name" \
    --query 'services[0]' \
    --output json)"
  if [[ "$service_description" == "null" ]]; then
    echo "Target ECS service ${ecs_cluster_name}/${ecs_service_name} does not exist." >&2
    exit 1
  fi
  if ! jq -e \
    '(.deployments | length) == 1
     and .deployments[0].status == "PRIMARY"
     and (.deployments[0].rolloutState // "COMPLETED") == "COMPLETED"
     and .pendingCount == 0
     and .desiredCount == 1
     and .runningCount == 1' \
    <<<"$service_description" \
    >/dev/null; then
    echo "Target ECS service ${ecs_cluster_name}/${ecs_service_name} is not in a single stable deployment; refusing retention." >&2
    exit 1
  fi
  current_service_task_definition="$(jq -r '.taskDefinition // empty' \
    <<<"$service_description")"
  if [[ "$current_service_task_definition" != "$ecs_task_definition_prefix"* ]]; then
    echo "The target service does not use the reviewed ${ecs_task_family} task-definition family." >&2
    exit 1
  fi
  service_references="$(jq \
    '[.taskDefinition, (.deployments[]?.taskDefinition)] | map(select(type == "string")) | unique' \
    <<<"$service_description")"
  if ! jq -e --arg prefix "$ecs_task_definition_prefix" \
    'all(.[]; startswith($prefix))' \
    <<<"$service_references" \
    >/dev/null; then
    echo "The target service references a task-definition family outside ${ecs_task_family}." >&2
    exit 1
  fi
  live_task_references="$(live_task_family_references)"
  service_references="$(jq -n \
    --argjson services "$service_references" \
    --argjson tasks "$live_task_references" \
    '$services + $tasks | unique')"
  if ! jq -e -n \
    --argjson references "$service_references" \
    --argjson definitions "$all_task_definitions" \
    '(($references - $definitions) | length) == 0' \
    >/dev/null; then
    echo "A live target consumer references a task definition outside the deletable ACTIVE/INACTIVE set." >&2
    exit 1
  fi

  successful_releases='[]'
  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    local task_tags
    task_tags="$(aws_target ecs list-tags-for-resource \
      --resource-arn "$task_definition_arn" \
      --query tags \
      --output json)"
    if jq -e \
      --arg key "$release_status_tag_key" \
      --arg value "$release_status_tag_value" \
      'any(.[]?; .key == $key and .value == $value)' \
      <<<"$task_tags" \
      >/dev/null; then
      successful_releases="$(jq --arg arn "$task_definition_arn" \
        '. + [$arn] | unique' \
        <<<"$successful_releases")"
    fi
  done < <(jq -r '.[]' <<<"$all_task_definitions")

  plan="$(jq -n \
    --arg family "$ecs_task_family" \
    --arg releaseTagKey "$release_status_tag_key" \
    --arg releaseTagValue "$release_status_tag_value" \
    --arg currentServiceTaskDefinition "$current_service_task_definition" \
    --argjson retain "$ecs_releases_to_keep" \
    --argjson active "$active_task_definitions" \
    --argjson all "$all_task_definitions" \
    --argjson serviceReferences "$service_references" \
    --argjson successful "$successful_releases" \
    '
      def revision: split(":") | last | tonumber;
      ($successful | sort_by(revision) | reverse) as $history
      | (($serviceReferences + $history) | unique | sort_by(revision) | reverse) as $releaseCandidates
      | ($releaseCandidates[0:$retain]) as $newest
      | (($newest + $serviceReferences) | unique | sort_by(revision) | reverse) as $protected
      | ($all - $protected) as $old
      | {
          Family: $family,
          RetainCount: $retain,
          ReleaseStatusTag: {Key: $releaseTagKey, Value: $releaseTagValue},
          TotalBefore: ($all | length),
          SuccessfulReleaseHistory: $history,
          CurrentLiveReferences: $serviceReferences,
          CurrentServiceTaskDefinition: $currentServiceTaskDefinition,
          NewestRetained: $newest,
          AdditionalInUseRetained: ($protected - $newest),
          Protected: $protected,
          Deregister: [
            $old[] as $arn
            | select($active | index($arn))
            | $arn
          ],
          Delete: $old,
          TotalAfterPlanned: ($protected | length)
        }
    ')"
  if [[ "$(jq '.AdditionalInUseRetained | length' <<<"$plan")" -gt 0 ]]; then
    echo "A live target task still uses a task definition outside the last ${ecs_releases_to_keep} successful releases; refusing retention." >&2
    echo "$plan" >&2
    exit 1
  fi
  echo "$plan"
}

ecr_artifact_closure() {
  local root_digests="$1"
  local root_description="$2"
  local require_multiarch="$3"
  local artifact_digests='[]'
  local pending_digests="$root_digests"
  if [[ "$require_multiarch" != "true" && "$require_multiarch" != "false" ]]; then
    echo "Internal error: ECR closure multi-architecture mode must be true or false." >&2
    exit 1
  fi
  while [[ "$(jq 'length' <<<"$pending_digests")" -gt 0 ]]; do
    local image_digest manifest_response manifest_media_type image_manifest
    local descriptor_digests referrer_digests
    image_digest="$(jq -r '.[0]' <<<"$pending_digests")"
    pending_digests="$(jq '.[1:]' <<<"$pending_digests")"
    if jq -e --arg digest "$image_digest" \
      'index($digest) != null' \
      <<<"$artifact_digests" \
      >/dev/null; then
      continue
    fi

    manifest_response="$(aws_target ecr batch-get-image \
      --repository-name "$ecr_repository_name" \
      --image-ids "imageDigest=${image_digest}" \
      --output json)"
    if ! jq -e --arg digest "$image_digest" \
      '(.failures | length) == 0
       and ([.images[]? | select(.imageId.imageDigest == $digest)] | length) == 1' \
      <<<"$manifest_response" \
      >/dev/null; then
      echo "AWS did not return exactly one ECR manifest for closure digest ${image_digest}." >&2
      exit 1
    fi
    manifest_media_type="$(jq -r --arg digest "$image_digest" \
      '.images[]
       | select(.imageId.imageDigest == $digest)
       | .imageManifestMediaType // (.imageManifest | fromjson | .mediaType) // empty' \
      <<<"$manifest_response")"
    image_manifest="$(jq -r --arg digest "$image_digest" \
      '.images[] | select(.imageId.imageDigest == $digest) | .imageManifest' \
      <<<"$manifest_response")"
    if jq -e --arg digest "$image_digest" \
      'index($digest) != null' \
      <<<"$root_digests" \
      >/dev/null; then
      case "$manifest_media_type" in
        application/vnd.oci.image.index.v1+json|application/vnd.docker.distribution.manifest.list.v2+json)
          ;;
        *)
          echo "${root_description} ${image_digest} is not an OCI index or Docker manifest list." >&2
          exit 1
          ;;
      esac
      if [[ "$require_multiarch" == "true" ]] && ! jq -e \
        '([.manifests[]?
           | select(.platform.os == "linux" and .platform.architecture == "amd64")]
          | length) == 1
         and ([.manifests[]?
               | select(.platform.os == "linux" and .platform.architecture == "arm64")]
              | length) == 1' \
        <<<"$image_manifest" \
        >/dev/null; then
        echo "${root_description} ${image_digest} is not the required Linux AMD64 plus Linux ARM64 image index." >&2
        exit 1
      fi
    fi

    case "$manifest_media_type" in
      application/vnd.oci.image.index.v1+json|application/vnd.docker.distribution.manifest.list.v2+json)
        descriptor_digests="$(jq '[.manifests[]?.digest] | map(select(type == "string"))' <<<"$image_manifest")"
        ;;
      *)
        descriptor_digests='[]'
        ;;
    esac
    referrer_digests="$(aws_target ecr list-image-referrers \
      --repository-name "$ecr_repository_name" \
      --subject-id "imageDigest=${image_digest}" \
      --filter artifactStatus=ANY \
      --query 'referrers[].digest' \
      --output json)"
    if ! jq -e \
      'all(.[]?; type == "string" and test("^sha256:[0-9a-f]{64}$"))' \
      <<<"$referrer_digests" \
      >/dev/null; then
      echo "ECR returned an invalid referrer digest for ${image_digest}." >&2
      exit 1
    fi

    artifact_digests="$(jq --arg digest "$image_digest" \
      '. + [$digest] | unique' \
      <<<"$artifact_digests")"
    pending_digests="$(jq -n \
      --argjson pending "$pending_digests" \
      --argjson descriptors "$descriptor_digests" \
      --argjson referrers "$referrer_digests" \
      '$pending + $descriptors + $referrers | unique')"
  done
  echo "$artifact_digests"
}

ecr_retention_plan() {
  local task_plan="$1"
  local retained_image_digests='[]'
  local deletable_release_digests='[]'
  local retained_artifact_digests='[]'
  local deletable_artifact_digests='[]'
  local image_details

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    local image_uri image_digest
    image_uri="$(aws_target ecs describe-task-definition \
      --task-definition "$task_definition_arn" \
      --query "taskDefinition.containerDefinitions[?name=='api'].image | [0]" \
      --output text)"
    if [[ "$image_uri" != "${ecr_repository_uri}@sha256:"* ]]; then
      echo "Retained task definition ${task_definition_arn} does not use the reviewed target ECR repository by digest." >&2
      exit 1
    fi
    image_digest="${image_uri##*@}"
    if [[ ! "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      echo "Retained task definition ${task_definition_arn} has an invalid image digest." >&2
      exit 1
    fi
    retained_image_digests="$(jq --arg digest "$image_digest" \
      '. + [$digest] | unique' \
      <<<"$retained_image_digests")"
  done < <(jq -r '.Protected[]' <<<"$task_plan")

  if [[ "$(jq 'length' <<<"$retained_image_digests")" -gt "$ecs_releases_to_keep" ]]; then
    echo "The retained task definitions reference more than ${ecs_releases_to_keep} target ECR release indexes." >&2
    exit 1
  fi

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    local old_image_uri old_image_digest
    old_image_uri="$(aws_target ecs describe-task-definition \
      --task-definition "$task_definition_arn" \
      --query "taskDefinition.containerDefinitions[?name=='api'].image | [0]" \
      --output text)"
    if [[ "$old_image_uri" != "${ecr_repository_uri}@sha256:"* ]]; then
      echo "Old task definition ${task_definition_arn} does not use the reviewed target ECR repository by digest." >&2
      exit 1
    fi
    old_image_digest="${old_image_uri##*@}"
    if [[ ! "$old_image_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      echo "Old task definition ${task_definition_arn} has an invalid image digest." >&2
      exit 1
    fi
    deletable_release_digests="$(jq --arg digest "$old_image_digest" \
      '. + [$digest] | unique' \
      <<<"$deletable_release_digests")"
  done < <(jq -r '.Delete[]' <<<"$task_plan")

  retained_artifact_digests="$(ecr_artifact_closure \
    "$retained_image_digests" \
    "Retained ECR release" \
    true)"
  deletable_artifact_digests="$(ecr_artifact_closure \
    "$deletable_release_digests" \
    "Deletable ECR release" \
    false)"

  image_details="$(aws_target ecr describe-images \
    --repository-name "$ecr_repository_name" \
    --filter imageStatus=ANY \
    --query 'imageDetails[].{Digest:imageDigest,Tags:imageTags,MediaType:imageManifestMediaType,ArtifactMediaType:artifactMediaType,ImageStatus:imageStatus,PushedAt:imagePushedAt}' \
    --output json)"
  if ! jq -e -n \
    --argjson retained "$retained_artifact_digests" \
    --argjson deletable "$deletable_artifact_digests" \
    --argjson details "$image_details" \
    '
      ($details | map(.Digest)) as $available
      | ((($retained + $deletable | unique) - $available) | length) == 0
    ' \
    >/dev/null; then
    echo "One or more artifacts required by a retained or deletable target ECR release are missing." >&2
    exit 1
  fi
  if ! jq -e -n \
    --argjson retained "$retained_artifact_digests" \
    --argjson deletable "$deletable_artifact_digests" \
    --argjson details "$image_details" \
    '
      ($retained + $deletable | unique) as $tracked
      | ([ $details[].Digest ] - $tracked | length) == 0
    ' \
    >/dev/null; then
    echo "Target ECR contains staged, orphaned, or untracked artifacts; refusing cleanup." >&2
    jq -n \
      --argjson retained "$retained_artifact_digests" \
      --argjson deletable "$deletable_artifact_digests" \
      --argjson details "$image_details" \
      '
        ($retained + $deletable | unique) as $tracked
        {
          UntrackedArtifacts: [
            $details[]
            | select((.Digest as $digest | $tracked | index($digest)) == null)
            | {
                Digest: .Digest,
                Tags: (.Tags // []),
                MediaType: .MediaType,
                ArtifactMediaType: .ArtifactMediaType,
                ImageStatus: .ImageStatus,
                PushedAt: .PushedAt
              }
          ]
        }
      ' \
      >&2
    exit 1
  fi

  jq -n \
    --arg repository "$ecr_repository_name" \
    --argjson releaseDigests "$retained_image_digests" \
    --argjson deletableReleaseDigests "$deletable_release_digests" \
    --argjson retainedArtifacts "$retained_artifact_digests" \
    --argjson deletableArtifacts "$deletable_artifact_digests" \
    --argjson imageDetails "$image_details" \
    '
      def is_index:
        .MediaType == "application/vnd.oci.image.index.v1+json"
        or .MediaType == "application/vnd.docker.distribution.manifest.list.v2+json";
      [
        $imageDetails[]
        | select(
            (.Digest as $digest | $deletableArtifacts | index($digest)) != null
            and (.Digest as $digest | $retainedArtifacts | index($digest)) == null
          )
      ] as $delete
      | {
          Repository: $repository,
          RetainedReleaseDigests: $releaseDigests,
          DeletableReleaseDigests: $deletableReleaseDigests,
          RetainedArtifactDigests: $retainedArtifacts,
          DeletableArtifactDigests: $deletableArtifacts,
          TotalArtifactsBefore: ($imageDetails | length),
          DeleteIndexes: [$delete[] | select(is_index)] | sort_by(.Digest),
          DeleteArtifacts: [$delete[] | select(is_index | not)] | sort_by(.Digest),
          TotalArtifactsAfterPlanned: ($retainedArtifacts | length)
        }
    '
}

release_retention_plan() {
  local task_plan ecr_plan
  task_plan="$(task_definition_retention_plan)"
  ecr_plan="$(ecr_retention_plan "$task_plan")"
  jq -n \
    --argjson ecs "$task_plan" \
    --argjson ecr "$ecr_plan" \
    '{ECS: $ecs, ECR: $ecr}'
}

plan_release_retention() {
  [[ $# -eq 0 ]] || usage
  local plan plan_hash
  plan="$(release_retention_plan)"
  plan_hash="$(retention_plan_hash "$plan")"
  echo "$plan"
  echo "Approval SHA-256: ${plan_hash}"
}

retention_plan_hash() {
  local plan="$1"
  jq -cS . <<<"$plan" |
    shasum -a 256 |
    awk '{print $1}'
}

mark_current_release_successful() {
  local task_definition_arn="$1"
  local tags
  aws_target ecs tag-resource \
    --resource-arn "$task_definition_arn" \
    --tags "key=${release_status_tag_key},value=${release_status_tag_value}" \
    >/dev/null
  tags="$(aws_target ecs list-tags-for-resource \
    --resource-arn "$task_definition_arn" \
    --query tags \
    --output json)"
  if ! jq -e \
    --arg key "$release_status_tag_key" \
    --arg value "$release_status_tag_value" \
    'any(.[]?; .key == $key and .value == $value)' \
    <<<"$tags" \
    >/dev/null; then
    echo "AWS did not confirm the successful-release tag on ${task_definition_arn}." >&2
    exit 1
  fi
}

assert_task_definition_not_live() {
  local task_definition_arn="$1"
  local approved_current_task_definition="$2"
  local service_description live_task_definitions
  if [[ "$task_definition_arn" == "$approved_current_task_definition" ]]; then
    echo "The current service task definition cannot be removed." >&2
    exit 1
  fi
  service_description="$(aws_target ecs describe-services \
    --cluster "$ecs_cluster_name" \
    --services "$ecs_service_name" \
    --query 'services[0]' \
    --output json)"
  if [[ "$service_description" == "null" ]] || ! jq -e \
    --arg cluster "$ecs_cluster_arn" \
    --arg current "$approved_current_task_definition" \
    '
      .clusterArn == $cluster
      and .taskDefinition == $current
      and (.deployments | length) == 1
      and .deployments[0].status == "PRIMARY"
      and .deployments[0].taskDefinition == $current
      and (.deployments[0].rolloutState // "COMPLETED") == "COMPLETED"
      and .desiredCount == 1
      and .runningCount == 1
      and .pendingCount == 0
    ' \
    <<<"$service_description" \
    >/dev/null; then
    echo "The target service changed after retention approval; refusing task-definition mutation." >&2
    exit 1
  fi
  live_task_definitions="$(live_task_family_references)"
  if ! jq -e -n \
    --arg current "$approved_current_task_definition" \
    --arg candidate "$task_definition_arn" \
    --argjson live "$live_task_definitions" \
    '
      ($live | length) == 1
      and $live[0] == $current
      and ($live | index($candidate)) == null
    ' \
    >/dev/null; then
    echo "A live service or task still references ${task_definition_arn}, or the current release changed; refusing removal." >&2
    exit 1
  fi
}

delete_task_definition() {
  local task_definition_arn="$1"
  local response
  response="$(aws_target ecs delete-task-definitions \
    --task-definitions "$task_definition_arn" \
    --output json)"
  if ! jq -e --arg arn "$task_definition_arn" \
    '(.failures | length) == 0
     and any(.taskDefinitions[]?; .taskDefinitionArn == $arn)' \
    <<<"$response" \
    >/dev/null; then
    echo "AWS did not confirm deletion of task definition ${task_definition_arn}." >&2
    jq '{Failures: [.failures[]? | {Arn: .arn, Reason: .reason, Detail: .detail}]}' \
      <<<"$response" \
      >&2
    exit 1
  fi
}

assert_ecr_digest_unreferenced_by_task_definitions() {
  local image_digest="$1"
  local active_task_definitions inactive_task_definitions all_task_definitions
  local task_definition_arn
  active_task_definitions="$(aws_target ecs list-task-definitions \
    --status ACTIVE \
    --query taskDefinitionArns \
    --output json)"
  inactive_task_definitions="$(aws_target ecs list-task-definitions \
    --status INACTIVE \
    --query taskDefinitionArns \
    --output json)"
  all_task_definitions="$(jq -n \
    --argjson active "$active_task_definitions" \
    --argjson inactive "$inactive_task_definitions" \
    '$active + $inactive | unique')"

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    local image_uri
    while IFS= read -r image_uri; do
      [[ -n "$image_uri" ]] || continue
      if [[ "$image_uri" == "$ecr_repository_uri" ||
            "$image_uri" == "${ecr_repository_uri}:"* ]]; then
        echo "Task definition ${task_definition_arn} uses a mutable/tagged target ECR reference; refusing artifact deletion." >&2
        exit 1
      fi
      if [[ "$image_uri" == "${ecr_repository_uri}@${image_digest}" ]]; then
        echo "Task definition ${task_definition_arn} still references ECR digest ${image_digest}; refusing deletion." >&2
        exit 1
      fi
    done < <(aws_target ecs describe-task-definition \
      --task-definition "$task_definition_arn" \
      --query 'taskDefinition.containerDefinitions[].image' \
      --output text |
      tr '\t' '\n')
  done < <(jq -r '.[]' <<<"$all_task_definitions")
}

assert_ecr_repository_digest_set() {
  local expected_digests="$1"
  local image_details actual_digests
  image_details="$(aws_target ecr describe-images \
    --repository-name "$ecr_repository_name" \
    --filter imageStatus=ANY \
    --query 'imageDetails[].{Digest:imageDigest,ImageStatus:imageStatus}' \
    --output json)"
  actual_digests="$(jq '[.[].Digest] | unique | sort' <<<"$image_details")"
  if ! jq -e -n \
    --argjson expected "$expected_digests" \
    --argjson actual "$actual_digests" \
    '($expected | unique | sort) == $actual' \
    >/dev/null; then
    echo "The target ECR repository changed after retention approval; refusing artifact deletion." >&2
    jq -n \
      --argjson expected "$expected_digests" \
      --argjson details "$image_details" \
      '
        ($expected | unique | sort) as $approved
        | ($details | map(.Digest) | unique | sort) as $actual
        | {
            MissingApprovedDigests: ($approved - $actual),
            UnexpectedArtifacts: [
              $details[]
              | select((.Digest as $digest | $approved | index($digest)) == null)
            ]
          }
      ' \
      >&2
    exit 1
  fi
}

delete_ecr_digest() {
  local image_digest="$1"
  local response
  assert_ecr_digest_unreferenced_by_task_definitions "$image_digest"
  response="$(aws_target ecr batch-delete-image \
    --repository-name "$ecr_repository_name" \
    --image-ids "imageDigest=${image_digest}" \
    --output json)"
  if ! jq -e --arg digest "$image_digest" \
    '(.failures | length) == 0
     and any(.imageIds[]?; .imageDigest == $digest)' \
    <<<"$response" \
    >/dev/null; then
    echo "AWS did not confirm deletion of ECR artifact ${image_digest}." >&2
    jq '{Failures: [.failures[]? | {ImageId: .imageId, FailureCode: .failureCode, FailureReason: .failureReason}]}' \
      <<<"$response" \
      >&2
    exit 1
  fi
}

apply_release_retention() {
  [[ $# -eq 0 ]] || usage
  local plan plan_hash confirmed_plan confirmed_plan_hash
  local task_definition_remove_count ecr_remove_count
  local current_task_definition post_ecs_plan deregistered_task_definition
  local remaining_ecr_digests
  aws_target ecs wait services-stable \
    --cluster "$ecs_cluster_name" \
    --services "$ecs_service_name"
  plan="$(release_retention_plan)"
  plan_hash="$(retention_plan_hash "$plan")"
  echo "$plan"
  echo "Approval SHA-256: ${plan_hash}"
  if [[ "${MUTUALGPU_APPROVED_RETENTION_PLAN_SHA256:-}" != "$plan_hash" ]]; then
    echo "Refusing cleanup. Review plan-release-retention and set MUTUALGPU_APPROVED_RETENTION_PLAN_SHA256 to the exact displayed hash." >&2
    exit 1
  fi
  echo "Verifying deployed network and public-access boundaries before retention changes."
  verify_deployed_security_boundaries
  if [[ "$(jq '.ECS.SuccessfulReleaseHistory | length' <<<"$plan")" -eq 0 ]]; then
    echo "Verifying the exact live-source release before marking the first target release successful."
    verify_deployed_live_source_release
  fi
  confirmed_plan="$(release_retention_plan)"
  confirmed_plan_hash="$(retention_plan_hash "$confirmed_plan")"
  if [[ "$confirmed_plan_hash" != "$plan_hash" ]] || ! jq -e -n \
    --argjson approved "$plan" \
    --argjson confirmed "$confirmed_plan" \
    '$approved == $confirmed' \
    >/dev/null; then
    echo "The release-retention plan changed during read-only verification; refusing every mutation." >&2
    echo "Originally approved SHA-256: ${plan_hash}" >&2
    echo "Current SHA-256: ${confirmed_plan_hash}" >&2
    exit 1
  fi
  plan="$confirmed_plan"
  task_definition_remove_count="$(jq '.ECS.Delete | length' <<<"$plan")"
  ecr_remove_count="$(jq '(.ECR.DeleteIndexes | length) + (.ECR.DeleteArtifacts | length)' <<<"$plan")"
  current_task_definition="$(jq -r '.ECS.CurrentServiceTaskDefinition' <<<"$plan")"
  mark_current_release_successful "$current_task_definition"

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    assert_task_definition_not_live \
      "$task_definition_arn" \
      "$current_task_definition"
    deregistered_task_definition="$(aws_target ecs deregister-task-definition \
      --task-definition "$task_definition_arn" \
      --query taskDefinition.taskDefinitionArn \
      --output text)"
    if [[ "$deregistered_task_definition" != "$task_definition_arn" ]]; then
      echo "AWS did not confirm deregistration of task definition ${task_definition_arn}." >&2
      exit 1
    fi
  done < <(jq -r '.ECS.Deregister[]' <<<"$plan")

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    assert_task_definition_not_live \
      "$task_definition_arn" \
      "$current_task_definition"
    delete_task_definition "$task_definition_arn"
  done < <(jq -r '.ECS.Delete[]' <<<"$plan")

  post_ecs_plan="$(task_definition_retention_plan)"
  if ! jq -e -n \
    --argjson before "$plan" \
    --argjson after "$post_ecs_plan" \
    '($after.Delete | length) == 0
     and $after.Protected == $before.ECS.Protected' \
    >/dev/null; then
    echo "The target task-definition state changed after approval; refusing all ECR deletion." >&2
    exit 1
  fi

  remaining_ecr_digests="$(jq -n \
    --argjson plan "$plan" \
    '
      (
        $plan.ECR.RetainedArtifactDigests
        + [$plan.ECR.DeleteIndexes[].Digest]
        + [$plan.ECR.DeleteArtifacts[].Digest]
      )
      | unique
      | sort
    ')"
  while IFS= read -r image_digest; do
    [[ -n "$image_digest" ]] || continue
    assert_ecr_repository_digest_set "$remaining_ecr_digests"
    delete_ecr_digest "$image_digest"
    remaining_ecr_digests="$(jq --arg digest "$image_digest" \
      'map(select(. != $digest))' \
      <<<"$remaining_ecr_digests")"
  done < <(jq -r '.ECR.DeleteIndexes[].Digest' <<<"$plan")

  while IFS= read -r image_digest; do
    [[ -n "$image_digest" ]] || continue
    assert_ecr_repository_digest_set "$remaining_ecr_digests"
    delete_ecr_digest "$image_digest"
    remaining_ecr_digests="$(jq --arg digest "$image_digest" \
      'map(select(. != $digest))' \
      <<<"$remaining_ecr_digests")"
  done < <(jq -r '.ECR.DeleteArtifacts[].Digest' <<<"$plan")
  assert_ecr_repository_digest_set "$remaining_ecr_digests"

  echo "Retained the last five successful target releases and their required multi-architecture artifacts."
  echo "Scheduled deletion of ${task_definition_remove_count} old task-definition revision(s) and ${ecr_remove_count} unreferenced ECR artifact(s)."
}

execute_change_set() {
  [[ $# -eq 2 ]] || usage
  local stack_name="$1"
  local change_set_name="$2"
  if [[ "$stack_name" != "$foundation_stack" && "$stack_name" != "$service_stack" ]]; then
    echo "Stack name must be exactly ${foundation_stack} or ${service_stack}." >&2
    exit 1
  fi

  show_change_set "$stack_name" "$change_set_name"
  local change_set_arn change_set_description
  change_set_description="$(aws_target cloudformation describe-change-set \
    --stack-name "$stack_name" \
    --change-set-name "$change_set_name" \
    --output json)"
  change_set_arn="$(jq -r '.ChangeSetId' <<<"$change_set_description")"
  if [[ -z "$change_set_arn" || "$change_set_arn" == "null" ]]; then
    echo "The reviewed change-set ARN could not be resolved." >&2
    exit 1
  fi
  if [[ "${MUTUALGPU_APPROVED_CHANGE_SET_ARN:-}" != "$change_set_arn" ]]; then
    echo "Refusing execution. Review the change set and set MUTUALGPU_APPROVED_CHANGE_SET_ARN to:" >&2
    echo "$change_set_arn" >&2
    exit 1
  fi
  if [[ "$stack_name" == "$service_stack" ]]; then
    local image_uri
    image_uri="$(jq -r \
      '.Parameters[] | select(.ParameterKey == "ImageUri") | .ParameterValue // empty' \
      <<<"$change_set_description")"
    echo "Reverifying the reviewed target image immediately before service-stack execution."
    verify_release_image "$image_uri" >/dev/null
  fi

  aws_target cloudformation execute-change-set \
    --stack-name "$stack_name" \
    --change-set-name "$change_set_arn"
  echo "Started execution of the approved change set ${change_set_arn}."
  if [[ "$stack_name" == "$service_stack" ]]; then
    echo "After the service stack is complete and stable, run plan-release-retention, review its exact SHA-256, then run apply-release-retention with explicit approval."
  fi
}

verify_target_identity

case "$action" in
  validate)
    [[ $# -eq 0 ]] || usage
    validate_templates
    ;;
  create-foundation-change-set)
    validate_templates
    create_foundation_change_set "$@"
    ;;
  create-service-change-set)
    validate_templates
    create_service_change_set "$@"
    ;;
  execute-change-set)
    execute_change_set "$@"
    ;;
  verify-release-image)
    verify_release_image "$@"
    ;;
  verify-live-source-release-image)
    verify_live_source_release_image "$@"
    ;;
  verify-deployed-live-source-release)
    verify_deployed_live_source_release "$@"
    ;;
  verify-deployed-security-boundaries)
    verify_deployed_security_boundaries "$@"
    ;;
  plan-release-retention)
    plan_release_retention "$@"
    ;;
  apply-release-retention)
    apply_release_retention "$@"
    ;;
  *)
    usage
    ;;
esac
