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

usage() {
  cat >&2 <<'USAGE'
Usage:
  scripts/deploy-aws-service.sh validate
  scripts/deploy-aws-service.sh create-foundation-change-set <change-set-name>
  scripts/deploy-aws-service.sh create-service-change-set <change-set-name>
  scripts/deploy-aws-service.sh execute-change-set <stack-name> <change-set-name-or-arn>

This target-only tool never reads or changes the ai-quinn source account. It creates
reviewable CloudFormation change sets and never deletes failed stacks. Execution
requires MUTUALGPU_APPROVED_CHANGE_SET_ARN to equal the exact reviewed change-set ARN.
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

execute_change_set() {
  [[ $# -eq 2 ]] || usage
  local stack_name="$1"
  local change_set_name="$2"
  if [[ "$stack_name" != "$foundation_stack" && "$stack_name" != "$service_stack" ]]; then
    echo "Stack name must be exactly ${foundation_stack} or ${service_stack}." >&2
    exit 1
  fi

  show_change_set "$stack_name" "$change_set_name"
  local change_set_arn
  change_set_arn="$(aws_target cloudformation describe-change-set \
    --stack-name "$stack_name" \
    --change-set-name "$change_set_name" \
    --query ChangeSetId \
    --output text)"
  if [[ "${MUTUALGPU_APPROVED_CHANGE_SET_ARN:-}" != "$change_set_arn" ]]; then
    echo "Refusing execution. Review the change set and set MUTUALGPU_APPROVED_CHANGE_SET_ARN to:" >&2
    echo "$change_set_arn" >&2
    exit 1
  fi

  aws_target cloudformation execute-change-set \
    --stack-name "$stack_name" \
    --change-set-name "$change_set_arn"
  echo "Started execution of the approved change set ${change_set_arn}."
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
  *)
    usage
    ;;
esac
