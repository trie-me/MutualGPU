#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ("$1" != "import" && "$1" != "verify") ]]; then
  echo "Usage: $0 import|verify" >&2
  exit 2
fi

mode="$1"
if [[ "${MUTUALGPU_TARGET_TASK_MODE:-}" != "true" ]]; then
  echo "The transactional migrator may run only inside the reviewed target ECS task." >&2
  exit 1
fi
if [[ "${AWS_REGION:-}" != "us-east-1" ]]; then
  echo "Target-task migration requires AWS_REGION=us-east-1." >&2
  exit 1
fi
if [[ -n "${AWS_PROFILE:-}" || -n "${AWS_DEFAULT_PROFILE:-}" ]]; then
  echo "Target-task migration must use the ECS task-role credential chain, not an AWS profile." >&2
  exit 1
fi

required=(
  MUTUALGPU_MIGRATION_APPLICATION_BUCKET
  MUTUALGPU_MIGRATION_WRITE_STORAGE_TARGET_ID
  MutualGPU__Postgres__Host
  MutualGPU__Postgres__Database
  MutualGPU__Postgres__Password
  MutualGPU__Postgres__HandleEncryptionKey
)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "${name} is required in the reviewed target migration task." >&2
    exit 1
  fi
done

exec dotnet run \
  --project tools/MutualGPU.TransactionalDataMigrator/MutualGPU.TransactionalDataMigrator.csproj \
  -- "$mode"
