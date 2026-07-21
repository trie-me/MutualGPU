#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <published-image-tag>" >&2
  exit 2
fi

image_tag="$1"
region="${AWS_REGION:-us-east-1}"
foundation_stack="${MUTUALGPU_FOUNDATION_STACK:-mutualgpu-foundation}"
service_stack="${MUTUALGPU_SERVICE_STACK:-mutualgpu-service}"
domain="${MUTUALGPU_DOMAIN:-mutualgpu.com}"
secret_name="${MUTUALGPU_DEPLOYMENT_SECRET_NAME:-mutualgpu/deployment}"
profile_args=()
if [[ -n "${AWS_PROFILE:-}" ]]; then profile_args=(--profile "$AWS_PROFILE"); fi
aws_cli() { aws "${profile_args[@]}" --region "$region" "$@"; }

account_id="$(aws_cli sts get-caller-identity --query Account --output text)"
repository_uri="${account_id}.dkr.ecr.${region}.amazonaws.com/mutualgpu-api"
image_digest="$(aws_cli ecr describe-images --repository-name mutualgpu-api --image-ids "imageTag=${image_tag}" --query 'imageDetails[0].imageDigest' --output text)"
if [[ -z "$image_digest" || "$image_digest" == "None" ]]; then
  echo "ECR tag '${image_tag}' was not found in mutualgpu-api." >&2
  exit 1
fi

certificate_arn="${MUTUALGPU_CERTIFICATE_ARN:-}"
if [[ -z "$certificate_arn" ]]; then
  certificate_arn="$(aws_cli acm list-certificates --certificate-statuses ISSUED --query "CertificateSummaryList[?DomainName=='${domain}'] | [0].CertificateArn" --output text)"
fi
if [[ -z "$certificate_arn" || "$certificate_arn" == "None" ]]; then
  echo "No issued ACM certificate was found for ${domain}. Set MUTUALGPU_CERTIFICATE_ARN to override." >&2
  exit 1
fi

secret_arn="$(aws_cli secretsmanager describe-secret --secret-id "$secret_name" --query ARN --output text)"

if aws_cli cloudformation describe-stacks --stack-name "$service_stack" >/dev/null 2>&1; then
  stack_status="$(aws_cli cloudformation describe-stacks --stack-name "$service_stack" --query 'Stacks[0].StackStatus' --output text)"
  if [[ "$stack_status" == "ROLLBACK_COMPLETE" ]]; then
    echo "Removing failed stack record ${service_stack}."
    aws_cli cloudformation delete-stack --stack-name "$service_stack"
    aws_cli cloudformation wait stack-delete-complete --stack-name "$service_stack"
  elif [[ "$stack_status" == *"_IN_PROGRESS" ]]; then
    echo "Stack ${service_stack} is currently ${stack_status}; wait for it to settle first." >&2
    exit 1
  fi
fi

aws_cli cloudformation validate-template --template-body file://examples/MutualGPU/deploy/aws/service.yaml >/dev/null
aws_cli cloudformation deploy \
  --stack-name "$service_stack" \
  --template-file examples/MutualGPU/deploy/aws/service.yaml \
  --parameter-overrides \
    "FoundationStackName=${foundation_stack}" \
    "ImageUri=${repository_uri}@${image_digest}" \
    "CertificateArn=${certificate_arn}" \
    "DeploymentSecretArn=${secret_arn}" \
  --no-fail-on-empty-changeset

https_url="$(aws_cli cloudformation describe-stacks --stack-name "$service_stack" --query "Stacks[0].Outputs[?OutputKey=='HttpsUrl'].OutputValue | [0]" --output text)"
echo "Deployed ${repository_uri}@${image_digest}"
echo "ALB endpoint: ${https_url}"
echo "Point ${domain} at the ALB using the output hostname, then use https://${domain}."
