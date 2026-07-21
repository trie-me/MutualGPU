set shell := ["zsh", "-cu"]

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

# Real Chromium integration: executes the BrowserWebSocketTransport in a browser,
# performs HTTPS enrollment, then completes the WSS Connected handshake.
browser-sdk-integration:
    @test -n "${MUTUALGPU_PROVIDER_KEY:-}" || { echo "MUTUALGPU_PROVIDER_KEY is required." >&2; exit 2; }
    MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_PROVIDER_KEY="$MUTUALGPU_PROVIDER_KEY" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" npm run test:browser-integration --prefix sdk/typescript

# Same integration test using the selected key from a protected '<uuid> <key>' file.
browser-sdk-integration-file key_file line="1":
    @test -r "{{key_file}}" || { echo "Key file is not readable: {{key_file}}" >&2; exit 2; }
    @test "{{line}}" -gt 0 2>/dev/null || { echo "line must be a positive integer." >&2; exit 2; }
    @provider_line=$(sed -n '{{line}}p' "{{key_file}}"); test -n "$provider_line" || { echo "No key exists on line {{line}}." >&2; exit 2; }; provider_key=${provider_line#* }; test "$provider_line" != "$provider_key" || { echo "The selected key line is malformed." >&2; exit 2; }; MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_PROVIDER_KEY="$provider_key" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" npm run test:browser-integration --prefix sdk/typescript
