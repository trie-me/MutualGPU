#!/usr/bin/env bash
set -euo pipefail

# Starts one configurable synthetic provider against an HTTPS API. The protected
# key file contains '<execution-unit-id> <key>' records; the key is never copied
# into the detached process command line or its log.

if [[ $# -ne 6 ]]; then
  echo "Usage: $0 <key-file> <line> <capability> <machine-tier> <compute-tier> <memory-gib>" >&2
  exit 64
fi

key_file="$1"
line="$2"
capability="$3"
machine_tier="$4"
compute_tier="$5"
memory_gib="$6"
api_url="${MUTUALGPU_API_URL:-https://mutualgpu.com}"
state_dir="${MUTUALGPU_DUMMY_PROVIDER_STATE_DIR:-${TMPDIR:-/tmp}/mutualgpu-live-dummy-producers}"

if [[ ! -r "$key_file" ]]; then
  echo "Provider key file is not readable: $key_file" >&2
  exit 66
fi
if ! [[ "$line" =~ ^[1-9][0-9]*$ ]]; then
  echo "Key line must be a positive integer." >&2
  exit 64
fi
if ! [[ "$capability" =~ ^[A-Za-z0-9_-]+$ ]]; then
  echo "Capability must contain only letters, digits, hyphens, or underscores." >&2
  exit 64
fi
case "$machine_tier" in
  Small|Medium|Large|ExtraLarge) ;;
  *) echo "Machine tier must be Small, Medium, Large, or ExtraLarge." >&2; exit 64 ;;
esac
case "$compute_tier" in
  Small|Medium|Large|ExtraLarge) ;;
  *) echo "Compute tier must be Small, Medium, Large, or ExtraLarge." >&2; exit 64 ;;
esac
if ! [[ "$memory_gib" =~ ^[1-9][0-9]*$ ]]; then
  echo "Memory GiB must be a positive integer." >&2
  exit 64
fi
if [[ "$api_url" != https://* ]]; then
  echo "MUTUALGPU_API_URL must use https:// (received: $api_url)" >&2
  exit 64
fi

record="$(sed -n "${line}p" "$key_file")"
unit_id="${record%% *}"
provider_key="${record#* }"
if [[ -z "$record" || "$unit_id" == "$provider_key" ]]; then
  echo "Key file line $line is malformed." >&2
  exit 65
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
session="mutualgpu-${capability}"
log_file="${state_dir}/${capability}.log"
mkdir -p "$state_dir"
screen -S "$session" -X quit >/dev/null 2>&1 || true
: >"$log_file"
LOG_FILE="$log_file" screen -dmS "$session" /bin/bash -c 'exec "$@" >"$LOG_FILE" 2>&1' \
  mutualgpu-provider "$script_dir/run-live-dummy-producer-unit.sh" "$key_file" "$line" "$capability" "$machine_tier" "$compute_tier" "$memory_gib" "$api_url"
echo "Started ${capability}: one ${compute_tier} / ${memory_gib} GiB unit (screen session ${session})."
