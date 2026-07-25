const SESSION_KEY = "mutualgpu.provider.enrollment";
const inputs = [...document.querySelectorAll("#capabilities input")];
const state = document.querySelector("#webgpu-state");
const block = document.querySelector("#webgpu-block");
const mint = document.querySelector("#mint-provider-identifier");
const identifier = document.querySelector("#provider-identifier");
const identifierPanel = document.querySelector("#provider-identifier-panel");
const copyIdentifier = document.querySelector("#copy-provider-identifier");
const start = document.querySelector("#start-hosting");
const status = document.querySelector("#enrollment-status");
const memoryGiB = document.querySelector("#memory-gib");
const capabilityCount = document.querySelector("#capability-count");
let webGpuReady = false;

function setStatus(text, type = "") {
  status.textContent = text;
  status.className = `provider-enrollment-status ${type}`;
}

function selectedCapabilities() {
  return inputs.filter(input => input.checked).map(input => input.value);
}

function updateSelection() {
  const selected = selectedCapabilities();
  if (capabilityCount) capabilityCount.textContent = `${selected.length} selected`;
  mint.disabled = !webGpuReady;
  start.disabled = !webGpuReady || selected.length === 0 || identifier.value.trim().length === 0;
}

function blockEnrollment(message) {
  state.className = "provider-state is-blocked";
  state.innerHTML = "<i></i><span>WebGPU unavailable</span>";
  block.textContent = message;
  block.hidden = false;
  for (const input of inputs) {
    input.disabled = true;
    input.closest(".provider-capability").classList.add("is-disabled");
  }
  setStatus("WebGPU is required before hosting can start.", "is-error");
  updateSelection();
}

async function verifyWebGpu() {
  if (!window.isSecureContext) {
    blockEnrollment("Provider enrollment needs a secure HTTPS context so the provider identifier cannot be sent over an insecure connection.");
    return;
  }
  if (!navigator.gpu) {
    blockEnrollment("This browser cannot offer compute because WebGPU is unavailable. Use a supported browser with hardware acceleration enabled.");
    return;
  }
  try {
    const adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) throw new Error("No compatible adapter returned");
    webGpuReady = true;
    state.className = "provider-state is-ready";
    state.innerHTML = "<i></i><span>WebGPU ready</span>";
    setStatus("Provide your access key, or mint a new one below.");
    updateSelection();
  } catch (error) {
    blockEnrollment(`This browser could not initialize WebGPU: ${error.message}`);
  }
}

inputs.forEach(input => input.addEventListener("change", updateSelection));
identifier.addEventListener("input", updateSelection);
mint.addEventListener("click", async () => {
  if (!webGpuReady) return;
  mint.disabled = true;
  setStatus("Creating a fresh provider identifier…");
  try {
    const response = await fetch("/api/webgpu-enrollments", { method: "POST", credentials: "same-origin", cache: "no-store" });
    if (!response.ok) throw new Error(`MutualGPU could not mint a provider identifier (${response.status}).`);
    const enrollment = await response.json();
    if (typeof enrollment?.providerKey !== "string" || enrollment.providerKey.length === 0) throw new Error("MutualGPU returned an invalid provider identifier.");
    identifier.value = enrollment.providerKey;
    identifierPanel.hidden = false;
    setStatus("New access key added. Save it for future use before you start hosting.", "is-success");
  } catch (error) {
    setStatus(error.message || "Provider identifier minting failed.", "is-error");
  } finally {
    updateSelection();
  }
});
copyIdentifier.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(identifier.value);
    setStatus("Access key copied. Save it for future use.", "is-success");
  } catch {
    setStatus("The browser could not copy the access key; select and copy it manually.", "is-error");
  }
});
start.addEventListener("click", () => {
  const capabilities = selectedCapabilities();
  const providerKey = identifier.value.trim();
  if (!webGpuReady || capabilities.length === 0 || !providerKey) return;
  sessionStorage.setItem(SESSION_KEY, JSON.stringify({
    providerKey,
    capabilities,
    memoryGiB: Number(memoryGiB.value),
    createdAt: Date.now()
  }));
  const hostTab = window.open("/host-compute.html", "_blank");
  if (!hostTab) {
    setStatus("Your browser blocked the hosting tab. Allow pop-ups for MutualGPU, then try again.", "is-error");
    return;
  }
  hostTab.focus();
});

updateSelection();
void verifyWebGpu();
