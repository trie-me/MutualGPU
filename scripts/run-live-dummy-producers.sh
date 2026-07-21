#!/usr/bin/env bash
set -euo pipefail

# Starts the three intentional demo producers against the configured HTTPS API.
# Each entry has exactly one execution unit and one capability. The protected key
# file has one '<execution-unit-id> <key>' entry per line; no key is printed.
# A detached screen session deliberately retains this terminal's access to the
# checked-out workspace; macOS launchd cannot read the Documents workspace.

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <protected-provider-key-file>" >&2
  exit 64
fi

key_file="$1"
if [[ ! -r "$key_file" ]]; then
  echo "Provider key file is not readable: $key_file" >&2
  exit 66
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
example_dir="$(cd "$script_dir/.." && pwd)"
sdk_dir="$example_dir/sdk/typescript"
api_url="${MUTUALGPU_API_URL:-https://mutualgpu.com}"
state_dir="${MUTUALGPU_DUMMY_PROVIDER_STATE_DIR:-${TMPDIR:-/tmp}/mutualgpu-live-dummy-producers}"
mkdir -p "$state_dir"

if [[ "$api_url" != https://* ]]; then
  echo "MUTUALGPU_API_URL must use https:// (received: $api_url)" >&2
  exit 64
fi

start_provider() {
  local line="$1" capability="$2" machine_tier="$3" compute_tier="$4" memory_gib="$5"
  local record unit_id provider_key session log_file
  record="$(sed -n "${line}p" "$key_file")"
  unit_id="${record%% *}"
  provider_key="${record#* }"
  if [[ -z "$record" || "$unit_id" == "$provider_key" ]]; then
    echo "Key file line $line is malformed." >&2
    exit 65
  fi

  log_file="$state_dir/${capability}.log"
  session="mutualgpu-${capability}"
  screen -S "$session" -X quit >/dev/null 2>&1 || true
  : >"$log_file"
  LOG_FILE="$log_file" screen -dmS "$session" /bin/bash -c 'exec "$@" >"$LOG_FILE" 2>&1' \
    mutualgpu-provider "$script_dir/run-live-dummy-producer-unit.sh" "$key_file" "$line" "$capability" "$machine_tier" "$compute_tier" "$memory_gib" "$api_url"
  echo "Started ${capability}: one ${compute_tier} / ${memory_gib} GiB unit (screen session ${session})."
}

# Line 3 is the established live 32 GiB demo identity. Lines 4 and 5 are
# intentionally unused demo keys from the same protected provisioned batch.
start_provider 3 exasplat Large Large 32
start_provider 4 stable-diffs ExtraLarge ExtraLarge 64
start_provider 5 chatterboxer ExtraLarge ExtraLarge 128

echo "Logs and PID files: $state_dir"
