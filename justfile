set shell := ["zsh", "-cu"]

mutualgpu-test:
    docker compose -f compose.postgres.yaml up -d --wait
    MUTUALGPU_TEST_POSTGRES="Host=localhost;Port=${MUTUALGPU_POSTGRES_PORT:-55432};Database=mutualgpu;Username=mutualgpu;Password=mutualgpu-local" dotnet test NetCats.Examples.MutualGPU.slnx --disable-build-servers --verbosity minimal -m:1
    node --test tests/frontend/*.test.mjs
    npm test --prefix sdk/typescript

mutualgpu-dev-cert:
    dotnet dev-certs https --trust

mutualgpu-local-smoke:
    ./scripts/run-local-composition.sh smoke

mutualgpu-local-demo:
    ./scripts/run-local-composition.sh demo

mutualgpu-local-harness: mutualgpu-dev-cert
    ./scripts/run-local-composition.sh harness

mutualgpu-postgres-up:
    docker compose -f compose.postgres.yaml up -d --wait

mutualgpu-postgres-down:
    docker compose -f compose.postgres.yaml down

mutualgpu-postgres-reset:
    docker compose -f compose.postgres.yaml down --volumes
    docker compose -f compose.postgres.yaml up -d --wait

# Interactively configure and run the real FLUX.2 provider.
flux2-worker:
    @workers/flux2/run-worker.sh

# Run FLUX.2 with an exact static credential from a protected '<uuid> <key>' file.
flux2-worker-file key_file line="4":
    @workers/flux2/run-worker.sh "{{key_file}}" "{{line}}"

# Bind the synthetic local Node provider to the public demo API.
# Export MUTUALGPU_EXECUTION_UNIT_ID and MUTUALGPU_PROVIDER_KEY first.
remote-demo-provider:
    @test -n "${MUTUALGPU_EXECUTION_UNIT_ID:-}" || { echo "MUTUALGPU_EXECUTION_UNIT_ID is required." >&2; exit 2; }
    @test -n "${MUTUALGPU_PROVIDER_KEY:-}" || { echo "MUTUALGPU_PROVIDER_KEY is required." >&2; exit 2; }
    MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" npm run demo:node --prefix sdk/typescript

# Bind using a one-based line number from a protected '<uuid> <key>' key file.
# Example: just remote-demo-provider-file /private/tmp/mutualgpu-provider-keys.v2DiHW 1
remote-demo-provider-file key_file line="1":
    @test -r "{{key_file}}" || { echo "Key file is not readable: {{key_file}}" >&2; exit 2; }
    @test "{{line}}" -gt 0 2>/dev/null || { echo "line must be a positive integer." >&2; exit 2; }
    @provider_line=$(sed -n '{{line}}p' "{{key_file}}"); test -n "$provider_line" || { echo "No key exists on line {{line}}." >&2; exit 2; }; execution_unit_id=${provider_line%% *}; provider_key=${provider_line#* }; test "$execution_unit_id" != "$provider_key" || { echo "The selected key line is malformed." >&2; exit 2; }; MUTUALGPU_EXECUTION_UNIT_ID="$execution_unit_id" MUTUALGPU_PROVIDER_KEY="$provider_key" MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" npm run demo:node --prefix sdk/typescript

# Start one configurable synthetic provider in a detached screen session.
# Invoke this repeatedly with different key lines and capabilities to build a test matrix.
remote-dummy-provider key_file capability line="1" machine_tier="Large" compute_tier="Large" memory_gib="32":
    @bash scripts/run-live-dummy-provider.sh "{{key_file}}" "{{line}}" "{{capability}}" "{{machine_tier}}" "{{compute_tier}}" "{{memory_gib}}"

# Inspect detached provider sessions.
remote-dummy-providers-status:
    @screen -ls || true

# Tail one configurable provider's log.
remote-dummy-provider-logs capability:
    @case "{{capability}}" in ''|*[!A-Za-z0-9_-]*) echo "Capability must contain only letters, digits, hyphens, or underscores." >&2; exit 2;; esac; tail -n 80 "${MUTUALGPU_DUMMY_PROVIDER_STATE_DIR:-${TMPDIR:-/tmp}/mutualgpu-live-dummy-producers}/{{capability}}.log"

# Stop one configurable provider without affecting other test providers.
remote-dummy-provider-stop capability:
    @case "{{capability}}" in ''|*[!A-Za-z0-9_-]*) echo "Capability must contain only letters, digits, hyphens, or underscores." >&2; exit 2;; esac; screen -S "mutualgpu-{{capability}}" -X quit >/dev/null 2>&1 || true; echo "Stopped MutualGPU dummy provider {{capability}}."

# Real Chromium integration: executes the BrowserWebSocketTransport in a browser,
# performs direct SDK enrollment, then completes the WSS Connected handshake.
browser-sdk-integration:
    @test -n "${MUTUALGPU_PROVIDER_KEY:-}" || { echo "MUTUALGPU_PROVIDER_KEY is required." >&2; exit 2; }
    MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_PROVIDER_KEY="$MUTUALGPU_PROVIDER_KEY" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" npm run test:browser-integration --prefix sdk/typescript

# Same integration test using the selected key from a protected '<uuid> <key>' file.
browser-sdk-integration-file key_file line="1":
    @test -r "{{key_file}}" || { echo "Key file is not readable: {{key_file}}" >&2; exit 2; }
    @test "{{line}}" -gt 0 2>/dev/null || { echo "line must be a positive integer." >&2; exit 2; }
    @provider_line=$(sed -n '{{line}}p' "{{key_file}}"); test -n "$provider_line" || { echo "No key exists on line {{line}}." >&2; exit 2; }; provider_key=${provider_line#* }; test "$provider_line" != "$provider_key" || { echo "The selected key line is malformed." >&2; exit 2; }; MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_PROVIDER_KEY="$provider_key" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" npm run test:browser-integration --prefix sdk/typescript
