#!/usr/bin/env bash
set -euo pipefail

mode="${1:-demo}"
case "$mode" in
  demo) npm_command="demo:node" ;;
  smoke) npm_command="smoke:node" ;;
  *)
    echo "Usage: $0 [demo|smoke]" >&2
    exit 64
    ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
example_dir="$(cd "$script_dir/.." && pwd)"
api_project="$example_dir/src/MutualGPU.Api/MutualGPU.Api.csproj"
api_content_root="$example_dir/src/MutualGPU.Api"
api_assembly="$api_content_root/bin/Debug/net10.0/MutualGPU.Api.dll"
sdk_dir="$example_dir/sdk/typescript"

api_url="${MUTUALGPU_API_URL:-https://localhost:7043}"
execution_unit_id="${MUTUALGPU_EXECUTION_UNIT_ID:-00000000-0000-0000-0000-000000000001}"
provider_key="${MUTUALGPU_PROVIDER_KEY:-local-demo-key}"

if [[ "$api_url" != https://* ]]; then
  echo "MUTUALGPU_API_URL must use https:// (received: $api_url)" >&2
  exit 64
fi

if ! dotnet dev-certs https --check >/dev/null 2>&1; then
  echo "No .NET HTTPS development certificate was found." >&2
  echo "Run: just mutualgpu-dev-cert" >&2
  exit 1
fi

if curl --silent --output /dev/null --connect-timeout 1 "$api_url/health/ready"; then
  echo "A server is already running at $api_url." >&2
  echo "Stop the existing MutualGPU demo before starting another composition." >&2
  exit 1
fi

dotnet build "$api_project" --nologo --verbosity quiet

# BSD/macOS mktemp only substitutes a trailing X run. Keeping the suffix after
# the Xs creates a literal filename and makes the second demo invocation fail.
api_log="$(mktemp "${TMPDIR:-/tmp}/mutualgpu-api.XXXXXX")"
api_pid=""
tail_pid=""

cleanup() {
  status=$?
  trap - EXIT INT TERM

  if [[ -n "$tail_pid" ]] && kill -0 "$tail_pid" 2>/dev/null; then
    kill "$tail_pid" 2>/dev/null || true
    wait "$tail_pid" 2>/dev/null || true
  fi

  if [[ -n "$api_pid" ]] && kill -0 "$api_pid" 2>/dev/null; then
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
  fi

  rm -f "$api_log"
  exit "$status"
}
trap cleanup EXIT INT TERM

(
  cd "$api_content_root"
  exec env \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$api_url" \
    NetCats__FiberDiagnostics__Enabled=true \
    MutualGPU__Demo__SimulateForest=true \
    MutualGPU__Providers__0__ExecutionUnitId="$execution_unit_id" \
    MutualGPU__Providers__0__PresharedKey="$provider_key" \
    dotnet "$api_assembly"
) >"$api_log" 2>&1 &
api_pid=$!

tail -n 0 -f "$api_log" &
tail_pid=$!

for attempt in $(seq 1 60); do
  if ! kill -0 "$api_pid" 2>/dev/null; then
    echo "MutualGPU API stopped before it became ready:" >&2
    cat "$api_log" >&2
    exit 1
  fi

  if curl --fail --silent "$api_url/health/ready" >/dev/null 2>&1; then
    # Ensure readiness belongs to the process started above rather than a host
    # that won a port race between the preflight and Kestrel binding.
    sleep 0.1
    if ! kill -0 "$api_pid" 2>/dev/null; then
      echo "MutualGPU API stopped during startup:" >&2
      cat "$api_log" >&2
      exit 1
    fi
    break
  fi

  if [[ "$attempt" == 60 ]]; then
    echo "MutualGPU API did not become ready at $api_url/health/ready:" >&2
    cat "$api_log" >&2
    exit 1
  fi

  sleep 0.5
done

node_options="${NODE_OPTIONS:-}"
if [[ " $node_options " != *" --use-system-ca "* ]]; then
  export NODE_OPTIONS="${node_options:+$node_options }--use-system-ca"
fi

echo
echo "MutualGPU API ready at $api_url"
if [[ "$mode" == "demo" ]]; then
  echo "Open $api_url and submit a task after the simulated provider connects."
fi
echo

cd "$sdk_dir"
MUTUALGPU_API_URL="$api_url" \
MUTUALGPU_PROVIDER_KEY="$provider_key" \
MUTUALGPU_EXECUTION_UNIT_ID="$execution_unit_id" \
npm run "$npm_command"
