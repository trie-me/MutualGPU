#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ("$1" != "import" && "$1" != "verify") ]]; then
  echo "Usage: $0 import|verify" >&2
  exit 2
fi

mode="$1"
region="${AWS_REGION:-us-east-1}"
foundation_stack="${MUTUALGPU_FOUNDATION_STACK:-mutualgpu-foundation}"
service_stack="${MUTUALGPU_SERVICE_STACK:-mutualgpu-service}"
aws_cli() { aws --profile ai-quinn --region "$region" "$@"; }

stack_parameter() {
  local stack="$1"
  local parameter="$2"
  aws_cli cloudformation describe-stacks \
    --stack-name "$stack" \
    --query "Stacks[0].Parameters[?ParameterKey=='${parameter}'].ParameterValue | [0]" \
    --output text
}

application_bucket="$(stack_parameter "$service_stack" ApplicationDataBucketName)"
if [[ -z "$application_bucket" || "$application_bucket" == "None" ]]; then
  application_bucket="$(stack_parameter "$foundation_stack" ApplicationDataBucketName)"
fi
if [[ -z "$application_bucket" || "$application_bucket" == "None" ]]; then
  echo "Could not resolve ApplicationDataBucketName from the current CloudFormation stacks." >&2
  exit 1
fi

provider_bucket="$(stack_parameter "$service_stack" ProviderKeyBucketName)"
if [[ -z "$provider_bucket" || "$provider_bucket" == "None" ]]; then
  provider_bucket="$(stack_parameter "$foundation_stack" ProviderKeyBucketName)"
fi
if [[ -z "$provider_bucket" || "$provider_bucket" == "None" ]]; then
  provider_bucket="$application_bucket"
fi

export AWS_PROFILE=ai-quinn
export AWS_REGION="$region"
export MUTUALGPU_MIGRATION_APPLICATION_BUCKET="$application_bucket"
export MUTUALGPU_MIGRATION_PROVIDER_BUCKET="$provider_bucket"

dotnet run \
  --project tools/MutualGPU.TransactionalDataMigrator/MutualGPU.TransactionalDataMigrator.csproj \
  -- "$mode"
