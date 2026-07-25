import { BrowserWebSocketTransport, ProviderClient } from "/js/mutualgpu-provider-sdk.js?v=20260725-klein-fp16-3";
import { loadFlux2WebGpuRuntime } from "/js/flux2-webgpu-runtime.js?v=20260725-klein-fp16-3";

const SESSION_KEY = "mutualgpu.provider.enrollment";
const CAPABILITIES = {
  "flux2-klein-4b-text-to-image": {
    label: "FLUX.2 Klein 4B text-to-image",
    description: "Generate one 1024×1024 PNG with the full FP16 WebGPU pipeline.",
    inputs: fluxInputs()
  }
};
let provider = null;
let fluxRuntime = null;
let stopped = false;
let completed = 0;

function fluxInputs() {
  return [
    { key: "prompt", type: "String", required: true, label: "Prompt", description: "Text describing the image to create.", displayOrder: 0 },
    { key: "seed", type: "Integer", required: false, label: "Seed", description: "Optional deterministic seed. A random seed is used when omitted.", minimum: 0, maximum: 2147483647, displayOrder: 1 }
  ];
}

const $ = selector => document.querySelector(selector);
const now = () => new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });

function log(message, level = "info") {
  const line = document.createElement("div");
  line.className = `provider-log-line is-${level}`;
  line.innerHTML = `<time class="provider-log-time">${now()}</time><i class="provider-log-mark ${level === "info" ? "" : `is-${level}`}">●</i><span></span>`;
  line.querySelector("span").textContent = message;
  $("#terminal").append(line);
  $("#terminal").scrollTop = $("#terminal").scrollHeight;
}

function setStep(id, state, label) {
  const step = document.querySelector(`#${id}`);
  if (state === "done" && !step.classList.contains("is-done")) completed += 1;
  step.className = `host-step is-${state}`;
  step.querySelector(".host-step-state").textContent = label;
  $("#progress-count").textContent = `${completed} / 5`;
}

function fail(id, message) {
  setStep(id, "failed", "Blocked");
  $("#host-badge").className = "provider-state is-blocked";
  $("#host-badge").innerHTML = "<i></i><span>Host blocked</span>";
  $("#host-status").textContent = "Blocked";
  $("#summary-title").textContent = "Host did not start";
  $("#summary-copy").textContent = message;
  $("#disconnect-copy").textContent = "Nothing is connected.";
  log(message, "error");
}

function enrollmentDefinition(selected, memoryGiB) {
  const hardware = hardwareProfile(memoryGiB);
  return {
    machine: { tier: hardware.tier, specifications: { computeTier: hardware.tier, memoryGiB: hardware.memoryGiB } },
    capabilities: selected.map(name => ({ name, ...CAPABILITIES[name], output: {
      hasThumbnail: true,
      hasPreview: true,
      hasMetadata: true,
      hasLogs: true,
      previewContentTypes: ["image/png"],
      metadataSchema: "{\"type\":\"object\"}"
    } }))
  };
}

function hardwareProfile(memoryGiB) {
  if (memoryGiB === 16) return { tier: "Small", memoryGiB };
  if (memoryGiB === 24) return { tier: "Medium", memoryGiB };
  if (memoryGiB === 32 || memoryGiB === 48) return { tier: "Large", memoryGiB };
  throw new Error("The selected provider memory is not valid for a MutualGPU scheduler tier.");
}

async function handleAssignment(task) {
  const abortInference = () => fluxRuntime?.abort();
  $("#active-work").textContent = "Text-to-image task";
  log(`Assignment ${task.taskId} received.`);
  if (!fluxRuntime) {
    await task.reject("The FLUX.2 WebGPU runtime is not ready.");
    $("#active-work").textContent = "Waiting for compatible work";
    return;
  }
  try {
    const request = validateTaskScalars(task.scalars ?? {});
    await task.accept();
    task.signal?.addEventListener("abort", abortInference, { once: true });
    await task.reportProgress({ phase: "prepare", percent: 2, message: "Preparing the FLUX.2 WebGPU runtime." });
    const result = await fluxRuntime.generate({
      ...request,
      inputUrl: task.input?.url,
      onStatus: message => {
        log(message);
        void task.reportProgress({ phase: "inference", percent: 45, message }).catch(() => {});
      }
    });
    await task.reportProgress({ phase: "upload", percent: 94, message: "Packaging and publishing the FLUX.2 PNG." });
    const thumbnail = await createThumbnail(result.image);
    const metadata = { ...result.metadata };
    const resultZip = createZip([
      { name: "image.png", data: new Uint8Array(await result.image.arrayBuffer()) },
      { name: "metadata.json", data: new TextEncoder().encode(JSON.stringify(metadata, null, 2)) },
      { name: "logs.txt", data: new TextEncoder().encode("FLUX.2 WebGPU generation completed in the provider browser.\n") }
    ]);
    const published = await task.uploadResult({
      resultZip: new Blob([resultZip], { type: "application/zip" }),
      preview: { data: result.image, contentType: "image/png", fileName: "image.png" },
      thumbnail: { data: thumbnail, contentType: "image/png", fileName: "thumbnail.png" },
      metadata,
      logs: "FLUX.2 WebGPU generation completed in the provider browser.\n"
    });
    await task.complete(published.receipt);
    log(`Assignment ${task.taskId} completed.`);
  } catch (error) {
    if (task.signal?.aborted) {
      log(`Assignment ${task.taskId} was cancelled by the requestor.`, "warn");
      return;
    }
    const failure = describeAssignmentFailure(error);
    log(`Assignment ${task.taskId} failed [${failure.step}]: ${failure.reason}`, "error");
    try {
      await task.reportProgress({ phase: "failed", percent: 45, message: failure.reason });
    } catch { }
    try { await task.fail(failure.step, failure.reason); } catch (reportError) {
      log(`Could not report assignment failure to MutualGPU: ${reportError?.message || "transport error"}.`, "warn");
    }
  } finally {
    task.signal?.removeEventListener("abort", abortInference);
    $("#active-work").textContent = "Waiting for compatible work";
  }
}

function stop(message) {
  if (stopped) return;
  stopped = true;
  fluxRuntime?.abort();
  provider?.close();
  provider = null;
  $("#disconnect").disabled = true;
  $("#host-badge").className = "provider-state is-blocked";
  $("#host-badge").innerHTML = "<i></i><span>Disconnected</span>";
  $("#host-status").textContent = "Disconnected";
  $("#active-work").textContent = "None";
  $("#summary-title").textContent = "Hosting stopped";
  $("#summary-copy").textContent = message;
  $("#disconnect-copy").textContent = "This tab no longer offers compute.";
  log(message);
}

async function start() {
  let enrollment;
  try {
    enrollment = JSON.parse(sessionStorage.getItem(SESSION_KEY) || "null");
  } catch { enrollment = null; }
  const selected = enrollment?.capabilities?.filter(name => CAPABILITIES[name]) ?? [];
  $("#selected").textContent = selected.map(name => CAPABILITIES[name].label).join(", ") || "None selected";
  log("Hosting tab opened; beginning provider startup.");
  if (!enrollment?.providerKey || selected.length === 0) {
    fail("passcode", "No tab-scoped provider identifier was found. Return to Offer compute to mint or provide one.");
    return;
  }
  setStep("passcode", "running", "Loading");
  setStep("passcode", "done", "Ready");
  log("Provider identifier loaded from this tab session.");

  setStep("webgpu", "running", "Initializing");
  try {
    if (!navigator.gpu) throw new Error("WebGPU is unavailable");
    const adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) throw new Error("No compatible adapter returned");
    const device = await adapter.requestDevice();
    device.destroy();
    if (stopped) return;
    setStep("webgpu", "done", "Ready");
    $("#gpu-status").textContent = "Ready";
    log("WebGPU adapter and device initialized.");
    log("Validating the FLUX.2 FP16 WebGPU manifests before enrollment. The first assignment downloads approximately 14.6 GiB of model assets.");
    fluxRuntime = await loadFlux2WebGpuRuntime({ onStatus: log });
    if (stopped) return;
    log("FLUX.2 FP16 WebGPU runtime is ready.");
  } catch (error) {
    fail("webgpu", `WebGPU initialization failed: ${error.message}`);
    return;
  }

  try {
    setStep("register", "running", "Enrolling");
    log(`Enrolling ${selected.length} selected FLUX.2 capability contract${selected.length === 1 ? "" : "s"}.`);
    const transport = new BrowserWebSocketTransport(location.origin, enrollment.providerKey);
    provider = new ProviderClient(transport);
    await provider.enroll(enrollmentDefinition(selected, enrollment.memoryGiB));
    if (stopped) return;
    setStep("register", "done", "Enrolled");
    log("Capability enrollment accepted by MutualGPU.");

    setStep("host", "running", "Connecting");
    log("Opening authenticated MutualGPU provider WebSocket.");
    await provider.connect(handleAssignment);
    if (stopped) return;
    setStep("host", "done", "Connected");
    setStep("ready", "done", "Waiting");
    $("#disconnect").disabled = false;
    $("#host-badge").className = "provider-state is-ready";
    $("#host-badge").innerHTML = "<i></i><span>Provider connected</span>";
    $("#host-status").textContent = "Connected in this tab";
    $("#active-work").textContent = "Waiting for compatible work";
    $("#summary-title").textContent = "Provider session connected";
    $("#summary-copy").textContent = "MutualGPU enrollment and the authenticated browser WSS connection are active.";
    $("#disconnect-copy").textContent = "Disconnect immediately stops this provider session.";
    log("WSS handshake complete; waiting for compatible work.");
  } catch (error) {
    provider?.close();
    provider = null;
    fail($("#register").classList.contains("is-running") ? "register" : "host", error.message || "Provider startup failed.");
  }
}

$("#disconnect").addEventListener("click", () => stop("Hosting session disconnected by provider."));
window.addEventListener("pagehide", () => stop("Hosting tab closed; provider session disconnected."));
void start();

function validateTaskScalars(scalars) {
  const prompt = String(scalars.prompt ?? "").trim();
  if (!prompt || prompt.length > 2000) throw new Error("The prompt is invalid.");
  const steps = integer(scalars.num_inference_steps ?? "4", 4, 4, "num_inference_steps");
  const width = integer(scalars.width ?? "1024", 1024, 1024, "width");
  const height = integer(scalars.height ?? "1024", 1024, 1024, "height");
  const seed = scalars.seed == null || scalars.seed === "" ? crypto.getRandomValues(new Uint32Array(1))[0] & 0x7fffffff : integer(scalars.seed, 0, 2147483647, "seed");
  return { prompt, steps, width, height, seed };
}

function integer(value, minimum, maximum, name) {
  if (!/^-?\d+$/.test(String(value))) throw new Error(`${name} must be an integer.`);
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < minimum || number > maximum) throw new Error(`${name} must be between ${minimum} and ${maximum}.`);
  return number;
}

function describeAssignmentFailure(error) {
  const diagnostic = error?.flux2Diagnostic;
  const stage = String(diagnostic?.stage || "inference");
  const step = stage === "artifact_download" ? "model_download" :
    stage === "webgpu_session_create" ? "webgpu_session_create" :
      diagnostic?.deviceLoss ? "webgpu_device_lost" : "inference";
  const details = [
    `stage=${stage}`,
    diagnostic?.build ? `build=${diagnostic.build}` : null,
    diagnostic?.artifact ? `artifact=${diagnostic.artifact}` : null,
    diagnostic?.component ? `component=${diagnostic.component}` : null,
    Number.isFinite(diagnostic?.responseStatus) ? `http=${diagnostic.responseStatus}` : null,
    Number.isFinite(diagnostic?.expectedBytes) ? `expected=${formatBytes(diagnostic.expectedBytes)}` : null,
    Number.isFinite(diagnostic?.graphBytes) ? `graph=${formatBytes(diagnostic.graphBytes)}` : null,
    Number.isFinite(diagnostic?.elapsedMs) ? `elapsed=${(diagnostic.elapsedMs / 1000).toFixed(1)}s` : null,
    diagnostic?.memory ? diagnostic.memory : null,
    diagnostic?.deviceLoss ? `deviceLost=${diagnostic.deviceLoss.reason}:${diagnostic.deviceLoss.message}` : null,
    diagnostic?.errorName ? `${diagnostic.errorName}: ${diagnostic.errorMessage}` : (error?.message || "runtime error")
  ].filter(Boolean).join("; ");
  return { step, reason: clipText(`FLUX.2 provider diagnostic: ${details}`, 1400) };
}

function formatBytes(value) {
  if (value >= 2 ** 30) return `${(value / 2 ** 30).toFixed(2)}GiB`;
  if (value >= 2 ** 20) return `${(value / 2 ** 20).toFixed(1)}MiB`;
  return `${Math.ceil(value / 2 ** 10)}KiB`;
}

function clipText(value, maximum) {
  const text = String(value || "").replace(/\s+/g, " ").trim();
  return text.length > maximum ? `${text.slice(0, maximum - 1)}…` : text;
}

async function createThumbnail(image) {
  const bitmap = await createImageBitmap(image);
  const scale = Math.min(1, 256 / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(bitmap.width * scale));
  canvas.height = Math.max(1, Math.round(bitmap.height * scale));
  canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("Could not create thumbnail.")), "image/png"));
}

function createZip(entries) {
  const encoder = new TextEncoder();
  const locals = [];
  const central = [];
  let offset = 0;
  for (const entry of entries) {
    const name = encoder.encode(entry.name);
    const data = entry.data;
    const checksum = crc32(data);
    const local = new Uint8Array(30 + name.length + data.length);
    const view = new DataView(local.buffer);
    view.setUint32(0, 0x04034b50, true); view.setUint16(4, 20, true); view.setUint32(14, checksum, true); view.setUint32(18, data.length, true); view.setUint32(22, data.length, true); view.setUint16(26, name.length, true);
    local.set(name, 30); local.set(data, 30 + name.length); locals.push(local);
    const record = new Uint8Array(46 + name.length);
    const recordView = new DataView(record.buffer);
    recordView.setUint32(0, 0x02014b50, true); recordView.setUint16(4, 20, true); recordView.setUint16(6, 20, true); recordView.setUint32(16, checksum, true); recordView.setUint32(20, data.length, true); recordView.setUint32(24, data.length, true); recordView.setUint16(28, name.length, true); recordView.setUint32(42, offset, true);
    record.set(name, 46); central.push(record); offset += local.length;
  }
  const centralLength = central.reduce((size, record) => size + record.length, 0);
  const end = new Uint8Array(22);
  const endView = new DataView(end.buffer);
  endView.setUint32(0, 0x06054b50, true); endView.setUint16(8, entries.length, true); endView.setUint16(10, entries.length, true); endView.setUint32(12, centralLength, true); endView.setUint32(16, offset, true);
  const output = new Uint8Array(offset + centralLength + end.length);
  let cursor = 0;
  for (const part of [...locals, ...central, end]) { output.set(part, cursor); cursor += part.length; }
  return output;
}

function crc32(data) {
  let value = 0xffffffff;
  for (const byte of data) {
    value ^= byte;
    for (let bit = 0; bit < 8; bit += 1) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
  }
  return (value ^ 0xffffffff) >>> 0;
}
