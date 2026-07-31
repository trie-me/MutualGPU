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

usage() {
  cat >&2 <<'USAGE'
Usage:
  scripts/deploy-aws-service.sh validate
  scripts/deploy-aws-service.sh create-foundation-change-set <change-set-name>
  scripts/deploy-aws-service.sh create-service-change-set <change-set-name>
  scripts/deploy-aws-service.sh execute-change-set <stack-name> <change-set-name-or-arn>
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

verify_live_source_release_image() {
  [[ $# -eq 1 ]] || usage
  local image_uri="$1"
  local image_digest image_details manifest_response index_manifest
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
  if ! jq -e \
    --arg digest "$image_digest" \
    --arg tag "$live_source_release_tag" \
    'length == 1
     and .[0].imageDigest == $digest
     and any(.[0].imageTags[]?; . == $tag)' \
    <<<"$image_details" \
    >/dev/null; then
    echo "The target digest is missing the immutable live-source release tag ${live_source_release_tag}." >&2
    exit 1
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
      --arg revision "$live_source_release_commit" \
      --arg version "$live_source_release_tag" \
      --arg source_index "$live_source_index_digest" \
      --arg source_tag "$live_source_release_tag" \
      '.config.Labels["org.opencontainers.image.revision"] == $revision
       and .config.Labels["org.opencontainers.image.version"] == $version
       and .config.Labels["com.mutualgpu.live-source.index"] == $source_index
       and .config.Labels["com.mutualgpu.live-source.tag"] == $source_tag' \
      <<<"$config_json" \
      >/dev/null; then
      echo "The ${architecture} image does not carry the exact recorded live-source provenance." >&2
      exit 1
    fi
  done

  jq -n \
    --arg imageUri "$image_uri" \
    --arg targetDigest "$image_digest" \
    --arg sourceCommit "$live_source_release_commit" \
    --arg sourceTag "$live_source_release_tag" \
    --arg sourceIndexDigest "$live_source_index_digest" \
    --arg amd64Digest "$amd64_digest" \
    --arg arm64Digest "$arm64_digest" \
    '{
       ImageUri: $imageUri,
       TargetIndexDigest: $targetDigest,
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
  service_description="$(aws_target ecs describe-services \
    --cluster "$ecs_cluster_name" \
    --services "$ecs_service_name" \
    --query 'services[0]' \
    --output json)"
  if [[ "$service_description" == "null" ]] || ! jq -e \
    '(.deployments | length) == 1
     and .deployments[0].status == "PRIMARY"
     and (.deployments[0].rolloutState // "COMPLETED") == "COMPLETED"
     and .desiredCount == 1
     and .runningCount == 1
     and .pendingCount == 0' \
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

  jq -n \
    --arg taskDefinitionArn "$task_definition_arn" \
    --arg runningDigest "$running_digest" \
    --argjson image "$verification" \
    '{
       Status: "Verified",
       TaskDefinitionArn: $taskDefinitionArn,
       RunningContainerDigest: $runningDigest,
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
  local alb_group api_group database_id database bucket_name load_balancer_arn
  local database_group alb_rules api_rules database_rules subnet_id
  local subnet route_tables container_instances container_instance ec2_instance_id instance
  local load_balancer listeners target_groups web_acl public_access policy_status

  alb_group="$(stack_output "$foundation_stack" AlbSecurityGroupId)"
  api_group="$(stack_output "$foundation_stack" ApiSecurityGroupId)"
  bucket_name="$(stack_output "$foundation_stack" ApplicationDataBucketName)"
  database_id="$(stack_resource_id "$foundation_stack" Database)"
  load_balancer_arn="$(stack_resource_id "$service_stack" LoadBalancer)"

  database="$(aws_target rds describe-db-instances \
    --db-instance-identifier "$database_id" \
    --query 'DBInstances[0]' \
    --output json)"
  if [[ "$database" == "null" ]] || ! jq -e \
    '.PubliclyAccessible == false
     and .MultiAZ == false
     and .DBInstanceClass == "db.t4g.micro"
     and .Engine == "postgres"
     and (.VpcSecurityGroups | length) == 1
     and (.DBSubnetGroup.Subnets | length) == 2' \
    <<<"$database" \
    >/dev/null; then
    echo "RDS is missing or does not match the reviewed private Single-AZ shape." >&2
    exit 1
  fi
  database_group="$(jq -r '.VpcSecurityGroups[0].VpcSecurityGroupId' <<<"$database")"

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
    if [[ "$subnet" == "null" ]] || ! jq -e '.MapPublicIpOnLaunch == false' \
      <<<"$subnet" \
      >/dev/null; then
      echo "Database subnet ${subnet_id} permits public-IP mapping." >&2
      exit 1
    fi
    route_tables="$(aws_target ec2 describe-route-tables \
      --filters "Name=association.subnet-id,Values=${subnet_id}" \
      --query RouteTables \
      --output json)"
    if ! jq -e \
      'length == 1
       and all(.[0].Routes[]?;
         (.DestinationCidrBlock // "") != "0.0.0.0/0"
         and (.DestinationIpv6CidrBlock // "") != "::/0")' \
      <<<"$route_tables" \
      >/dev/null; then
      echo "Database subnet ${subnet_id} is not explicitly associated with a route table free of public default routes." >&2
      exit 1
    fi
  done < <(jq -r '.DBSubnetGroup.Subnets[].SubnetIdentifier' <<<"$database")

  container_instances="$(aws_target ecs list-container-instances \
    --cluster "$ecs_cluster_name" \
    --status ACTIVE \
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
    '.State.Name == "running"
     and .InstanceType == "t4g.nano"
     and (.KeyName == null)
     and (.SecurityGroups | length) == 1
     and .SecurityGroups[0].GroupId == $api' \
    <<<"$instance" \
    >/dev/null; then
    echo "The ECS host is not the reviewed keyless t4g.nano with only the API security group." >&2
    exit 1
  fi

  load_balancer="$(aws_target elbv2 describe-load-balancers \
    --load-balancer-arns "$load_balancer_arn" \
    --query 'LoadBalancers[0]' \
    --output json)"
  if [[ "$load_balancer" == "null" ]] || ! jq -e \
    --arg alb "$alb_group" \
    '.Scheme == "internet-facing"
     and .Type == "application"
     and (.SecurityGroups | length) == 1
     and .SecurityGroups[0] == $alb' \
    <<<"$load_balancer" \
    >/dev/null; then
    echo "The load balancer does not have the reviewed public ALB and single-SG shape." >&2
    exit 1
  fi
  listeners="$(aws_target elbv2 describe-listeners \
    --load-balancer-arn "$load_balancer_arn" \
    --query Listeners \
    --output json)"
  if ! jq -e \
    'length == 2
     and ([.[] | select(.Port == 80 and .Protocol == "HTTP"
                       and any(.DefaultActions[]?;
                         .Type == "redirect"
                         and .RedirectConfig.Protocol == "HTTPS"
                         and .RedirectConfig.Port == "443"))] | length) == 1
     and ([.[] | select(.Port == 443 and .Protocol == "HTTPS")] | length) == 1' \
    <<<"$listeners" \
    >/dev/null; then
    echo "The ALB listeners are not limited to HTTP redirect and HTTPS." >&2
    exit 1
  fi
  target_groups="$(aws_target elbv2 describe-target-groups \
    --load-balancer-arn "$load_balancer_arn" \
    --query TargetGroups \
    --output json)"
  if ! jq -e \
    'length == 2
     and ([.[] | select(.TargetType == "instance" and .Port == 8080)] | length) == 1
     and ([.[] | select(.TargetType == "instance" and .Port == 8081)] | length) == 1' \
    <<<"$target_groups" \
    >/dev/null; then
    echo "The ALB target groups are not limited to instance ports 8080 and 8081." >&2
    exit 1
  fi
  web_acl="$(aws_target wafv2 get-web-acl-for-resource \
    --resource-arn "$load_balancer_arn" \
    --query WebACL \
    --output json)"
  if [[ "$web_acl" == "null" ]] || ! jq -e \
    '.Name == "mutualgpu-provider-enrollment"' \
    <<<"$web_acl" \
    >/dev/null; then
    echo "The reviewed WAF is not associated with the target ALB." >&2
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
  if aws_target s3api get-bucket-website --bucket "$bucket_name" >/dev/null 2>&1; then
    echo "The target application bucket unexpectedly has website hosting enabled." >&2
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
    if [[ "$stack_status" == *"_IN_PROGRESS" ]]; then
      echo "Stack ${stack_name} is ${stack_status}; wait for it to settle." >&2
      exit 1
    fi
    printf 'UPDATE'
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
    --output json
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
  local stack_type
  stack_type="$(change_set_type "$service_stack")"
  if [[ "$stack_type" == "CREATE" ]]; then
    echo "Verifying that the first target image is the recorded live source release rebuilt for both target architectures."
    verify_live_source_release_image "$image_uri"
  fi

  local parameters=(
    "ParameterKey=FoundationStackName,ParameterValue=${foundation_stack}"
    "ParameterKey=ImageUri,ParameterValue=${image_uri}"
    "ParameterKey=CertificateArn,ParameterValue=${certificate_arn}"
    "ParameterKey=PublicApiHost,ParameterValue=${MUTUALGPU_DOMAIN:-mutualgpu.com}"
    "ParameterKey=DeploymentSecretArn,ParameterValue=${deployment_secret_arn}"
    "ParameterKey=DesiredCount,ParameterValue=${MUTUALGPU_DESIRED_COUNT:-1}"
    "ParameterKey=ProviderCorsOrigin0,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_0:-https://huggingface.co}"
    "ParameterKey=ProviderCorsOrigin1,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_1:-https://vercel.com}"
    "ParameterKey=ProviderCorsOrigin2,ParameterValue=${MUTUALGPU_PROVIDER_CORS_ORIGIN_2:-https://yosun-triposplat-webgpu-demo.static.hf.space}"
    "ParameterKey=ProviderEnrollmentRateLimit,ParameterValue=${MUTUALGPU_PROVIDER_ENROLLMENT_RATE_LIMIT:-10}"
  )

  echo "Creating ${stack_type} change set ${change_set_name} for ${service_stack}."
  printf '  %s\n' "${parameters[@]}"
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
  local service_description service_references live_task_references successful_releases plan
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
     and .runningCount == .desiredCount' \
    <<<"$service_description" \
    >/dev/null; then
    echo "Target ECS service ${ecs_cluster_name}/${ecs_service_name} is not in a single stable deployment; refusing retention." >&2
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
          CurrentServiceTaskDefinition: $serviceReferences[0],
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

ecr_retention_plan() {
  local task_plan="$1"
  local retained_image_digests='[]'
  local retained_artifact_digests='[]'
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

  local pending_digests="$retained_image_digests"
  while [[ "$(jq 'length' <<<"$pending_digests")" -gt 0 ]]; do
    local image_digest manifest_response manifest_media_type image_manifest
    local descriptor_digests referrer_digests
    image_digest="$(jq -r '.[0]' <<<"$pending_digests")"
    pending_digests="$(jq '.[1:]' <<<"$pending_digests")"
    if jq -e --arg digest "$image_digest" \
      'index($digest) != null' \
      <<<"$retained_artifact_digests" \
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
      echo "AWS did not return exactly one ECR manifest for retained digest ${image_digest}." >&2
      jq '{Failures: [.failures[]? | {ImageId: .imageId, FailureCode: .failureCode, FailureReason: .failureReason}]}' \
        <<<"$manifest_response" \
        >&2
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
      <<<"$retained_image_digests" \
      >/dev/null; then
      case "$manifest_media_type" in
        application/vnd.oci.image.index.v1+json|application/vnd.docker.distribution.manifest.list.v2+json)
          ;;
        *)
          echo "Retained ECR release ${image_digest} is not an OCI index or Docker manifest list." >&2
          exit 1
          ;;
      esac
      if ! jq -e \
        '([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "amd64")] | length) == 1
         and ([.manifests[]? | select(.platform.os == "linux" and .platform.architecture == "arm64")] | length) == 1' \
        <<<"$image_manifest" \
        >/dev/null; then
        echo "Retained ECR digest ${image_digest} is not the required Linux AMD64 plus Linux ARM64 image index." >&2
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
      --query 'referrers[].digest' \
      --output json)"
    if ! jq -e \
      'all(.[]?; type == "string" and test("^sha256:[0-9a-f]{64}$"))' \
      <<<"$referrer_digests" \
      >/dev/null; then
      echo "ECR returned an invalid referrer digest for ${image_digest}." >&2
      exit 1
    fi

    retained_artifact_digests="$(jq --arg digest "$image_digest" \
      '. + [$digest] | unique' \
      <<<"$retained_artifact_digests")"
    pending_digests="$(jq -n \
      --argjson pending "$pending_digests" \
      --argjson descriptors "$descriptor_digests" \
      --argjson referrers "$referrer_digests" \
      '$pending + $descriptors + $referrers | unique')"
  done

  image_details="$(aws_target ecr describe-images \
    --repository-name "$ecr_repository_name" \
    --query 'imageDetails[].{Digest:imageDigest,Tags:imageTags,MediaType:imageManifestMediaType,ArtifactMediaType:artifactMediaType,PushedAt:imagePushedAt}' \
    --output json)"
  if ! jq -e -n \
    --argjson retained "$retained_artifact_digests" \
    --argjson details "$image_details" \
    '($details | map(.Digest)) as $available | (($retained - $available) | length) == 0' \
    >/dev/null; then
    echo "One or more artifacts required by a retained target ECR release are missing." >&2
    exit 1
  fi

  jq -n \
    --arg repository "$ecr_repository_name" \
    --argjson releaseDigests "$retained_image_digests" \
    --argjson retainedArtifacts "$retained_artifact_digests" \
    --argjson imageDetails "$image_details" \
    '
      def is_index:
        .MediaType == "application/vnd.oci.image.index.v1+json"
        or .MediaType == "application/vnd.docker.distribution.manifest.list.v2+json";
      [
        $imageDetails[]
        | select(.Digest as $digest | $retainedArtifacts | index($digest) | not)
      ] as $delete
      | {
          Repository: $repository,
          RetainedReleaseDigests: $releaseDigests,
          RetainedArtifactDigests: $retainedArtifacts,
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
      if [[ "$image_uri" == "${ecr_repository_uri}:"* ]]; then
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
  local plan plan_hash task_definition_remove_count ecr_remove_count
  local current_task_definition post_ecs_plan
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
  task_definition_remove_count="$(jq '.ECS.Delete | length' <<<"$plan")"
  ecr_remove_count="$(jq '(.ECR.DeleteIndexes | length) + (.ECR.DeleteArtifacts | length)' <<<"$plan")"
  current_task_definition="$(jq -r '.ECS.CurrentServiceTaskDefinition' <<<"$plan")"
  if [[ "$(jq '.ECS.SuccessfulReleaseHistory | length' <<<"$plan")" -eq 0 ]]; then
    echo "Verifying the exact live-source release before marking the first target release successful."
    verify_deployed_live_source_release
  fi
  mark_current_release_successful "$current_task_definition"

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
    aws_target ecs deregister-task-definition \
      --task-definition "$task_definition_arn" \
      --query taskDefinition.taskDefinitionArn \
      --output text \
      >/dev/null
  done < <(jq -r '.ECS.Deregister[]' <<<"$plan")

  while IFS= read -r task_definition_arn; do
    [[ -n "$task_definition_arn" ]] || continue
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

  while IFS= read -r image_digest; do
    [[ -n "$image_digest" ]] || continue
    delete_ecr_digest "$image_digest"
  done < <(jq -r '.ECR.DeleteIndexes[].Digest' <<<"$plan")

  while IFS= read -r image_digest; do
    [[ -n "$image_digest" ]] || continue
    delete_ecr_digest "$image_digest"
  done < <(jq -r '.ECR.DeleteArtifacts[].Digest' <<<"$plan")

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
  if [[ "$stack_name" == "$service_stack" &&
        "$(jq -r '.ChangeSetType' <<<"$change_set_description")" == "CREATE" ]]; then
    local image_uri
    image_uri="$(jq -r \
      '.Parameters[] | select(.ParameterKey == "ImageUri") | .ParameterValue // empty' \
      <<<"$change_set_description")"
    echo "Reverifying the exact first-release image immediately before service-stack execution."
    verify_live_source_release_image "$image_uri"
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
