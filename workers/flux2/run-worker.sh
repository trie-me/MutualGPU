#!/usr/bin/env bash
set -euo pipefail

worker_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
key_file="${1:-${MUTUALGPU_PROVIDER_KEY_FILE:-}}"
key_line="${2:-${MUTUALGPU_PROVIDER_KEY_LINE:-4}}"
credential_source="environment"
execution_unit_id=""

fail() {
  echo "FLUX.2 worker: $1" >&2
  exit 2
}

load_key_file() {
  local record
  [[ -r "$key_file" ]] || fail "provider credential file is not readable: $key_file"
  [[ "$key_line" =~ ^[1-9][0-9]*$ ]] || fail "provider credential line must be a positive integer"
  record="$(sed -n "${key_line}p" "$key_file")"
  record="${record%$'\r'}"
  [[ -n "$record" && "$record" == *" "* ]] || fail "line $key_line is not an '<execution-unit-id> <provider-key>' record"
  execution_unit_id="${record%% *}"
  MUTUALGPU_PROVIDER_KEY="${record#* }"
  [[ "$execution_unit_id" =~ ^[0-9a-fA-F-]{36}$ && -n "$MUTUALGPU_PROVIDER_KEY" ]] || fail "line $key_line contains an invalid provider binding"
  credential_source="protected file '$key_file', line $key_line"
}

choose_credential() {
  local choice
  echo
  echo "Provider authentication"
  echo "  1) Read the static binding from a protected file (recommended)"
  echo "  2) Enter the static provider passcode without echo"
  read -r -p "Select credential source [1]: " choice
  choice="${choice:-1}"
  case "$choice" in
    1)
      read -r -p "Protected credential file: " key_file
      [[ -n "$key_file" ]] || fail "a credential file is required"
      read -r -p "Credential line [4]: " key_line
      key_line="${key_line:-4}"
      load_key_file
      ;;
    2)
      read -r -s -p "Static provider passcode: " MUTUALGPU_PROVIDER_KEY
      echo
      [[ -n "$MUTUALGPU_PROVIDER_KEY" ]] || fail "a provider passcode is required"
      credential_source="hidden terminal input"
      ;;
    *) fail "credential source must be 1 or 2" ;;
  esac
}

command -v mise >/dev/null || fail "mise is required; install the tools declared in $worker_dir/mise.toml"
command -v npm >/dev/null || fail "npm is required by the MutualGPU Node SDK binding"
[[ -f "$worker_dir/node_modules/@mutualgpu/provider-core/package.json" ]] || fail "Node dependencies are missing; run 'npm install --prefix workers/flux2'"

if [[ -n "$key_file" ]]; then
  load_key_file
elif [[ -z "${MUTUALGPU_PROVIDER_KEY:-}" ]]; then
  [[ -t 0 ]] || fail "set MUTUALGPU_PROVIDER_KEY_FILE or MUTUALGPU_PROVIDER_KEY for a non-interactive launch"
  choose_credential
fi

api_url="${MUTUALGPU_API_URL:-https://mutualgpu.com}"
capability="${MUTUALGPU_FLUX2_CAPABILITY:-flux2-klein-4b}"
model="${MUTUALGPU_FLUX2_MODEL:-black-forest-labs/FLUX.2-klein-4B}"
requested_device="${MUTUALGPU_FLUX2_DEVICE:-auto}"
[[ "$api_url" == https://* ]] || fail "MUTUALGPU_API_URL must use https://"
[[ -n "$capability" ]] || fail "MUTUALGPU_FLUX2_CAPABILITY cannot be blank"

echo
echo "MutualGPU FLUX.2 worker"
echo "  API:         $api_url"
echo "  Capability:  $capability"
echo "  Model:       $model"
echo "  Device:      $requested_device"
echo "  Credential:  $credential_source"
[[ -n "$execution_unit_id" ]] && echo "  Unit:        $execution_unit_id"
echo "  Artifacts:   runtime cache only; model weights and outputs are Git-ignored"
echo
echo "[preflight] Synchronizing the declared Python environment and probing the accelerator..."

export MUTUALGPU_PROVIDER_KEY
export MUTUALGPU_API_URL="$api_url"
export MUTUALGPU_FLUX2_CAPABILITY="$capability"
export MUTUALGPU_FLUX2_MODEL="$model"
export MUTUALGPU_FLUX2_DEVICE="$requested_device"
export MUTUALGPU_FLUX2_PYTHON="${MUTUALGPU_FLUX2_PYTHON:-python}"

if ! probe="$(mise exec -C "$worker_dir" -- env -u SSL_CERT_DIR uv run python src/inference.py --probe)"; then
  echo "[preflight] Failed. Check the Python dependency environment and CUDA/MPS availability." >&2
  exit 1
fi
device="$(printf '%s\n' "$probe" | sed -n 's/.*"device":"\([^"]*\)".*/\1/p')"
[[ -n "$device" ]] || fail "accelerator probe returned an unexpected response"
echo "[preflight] Ready on $device."

echo "[launch] Starting the MutualGPU SDK binding. Press Ctrl-C for graceful shutdown."
exec mise exec -C "$worker_dir" -- env -u SSL_CERT_DIR uv run -- npm start
