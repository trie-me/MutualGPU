#!/usr/bin/env bash
set -euo pipefail

# Foreground process run by the per-user launchd job. Keys stay in the protected
# provisioner output file and are never copied into a plist, command line, or log.
if [[ $# -ne 7 ]]; then
  echo "Usage: $0 <key-file> <line> <capability> <machine-tier> <compute-tier> <memory-gib> <api-url>" >&2
  exit 64
fi

key_file="$1"
line="$2"
capability="$3"
machine_tier="$4"
compute_tier="$5"
memory_gib="$6"
api_url="$7"

record="$(sed -n "${line}p" "$key_file")"
unit_id="${record%% *}"
provider_key="${record#* }"
if [[ -z "$record" || "$unit_id" == "$provider_key" ]]; then
  echo "Key file line $line is malformed." >&2
  exit 65
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sdk_dir="$(cd "$script_dir/../sdk/typescript" && pwd)"
cd "$sdk_dir"
export NODE_OPTIONS="${NODE_OPTIONS:+$NODE_OPTIONS }--use-system-ca"
exec env \
  MUTUALGPU_API_URL="$api_url" \
  MUTUALGPU_EXECUTION_UNIT_ID="$unit_id" \
  MUTUALGPU_PROVIDER_KEY="$provider_key" \
  MUTUALGPU_DEMO_CAPABILITY="$capability" \
  MUTUALGPU_DEMO_MACHINE_TIER="$machine_tier" \
  MUTUALGPU_DEMO_COMPUTE_TIER="$compute_tier" \
  MUTUALGPU_DEMO_MEMORY_GIB="$memory_gib" \
  node samples/node-demo-provider.mjs
