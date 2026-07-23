import { fileURLToPath } from "node:url";
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";
import { buildEnrollment, loadConfig } from "./config.mjs";
import { Flux2Runtime } from "./inference-runtime.mjs";
import { createTaskHandler } from "./provider-binding.mjs";

const config = loadConfig();
const activity = { state: "starting", taskId: null, completed: 0, failed: 0, lastEventAt: new Date() };
const runtime = new Flux2Runtime({
  python: config.python,
  scriptPath: fileURLToPath(new URL("./inference.py", import.meta.url)),
  model: config.model,
  safetyModel: config.safetyModel,
  device: config.device,
  localFilesOnly: config.localFilesOnly,
  allowCpu: config.allowCpu,
  generationTimeoutMs: config.generationTimeoutMs,
  log: message => process.stderr.write(message)
});
const provider = new ProviderClient(new NodeGrpcTransport(config.apiUrl, config.providerKey));

let stopping = false;
let heartbeat = null;
const stop = signal => {
  if (stopping) return;
  stopping = true;
  if (heartbeat) clearInterval(heartbeat);
  console.info(`Stopping FLUX.2 worker after ${signal}.`);
  runtime.close();
  provider.close();
};
process.once("SIGINT", () => stop("SIGINT"));
process.once("SIGTERM", () => stop("SIGTERM"));

let startupStage = "FLUX.2 runtime preflight";
try {
  const runtimeInfo = await runtime.start();
  startupStage = "MutualGPU enrollment";
  console.info(`[1/3] Runtime ready on ${runtimeInfo.device}; enrolling '${config.capabilityName}'.`);
  await provider.enroll(buildEnrollment(config));
  startupStage = "MutualGPU provider connection";
  console.info("[2/3] Enrollment accepted; opening the provider session.");
  await provider.connect(createTaskHandler({ runtime, activity }));
  activity.state = "idle";
  activity.lastEventAt = new Date();
  console.info(`[3/3] Provider connected. '${config.capabilityName}' is bound to ${runtimeInfo.device} with model '${runtimeInfo.model}'.`);
  console.info(`[stream] Heartbeat every ${config.heartbeatMs / 1000}s; waiting for task assignments.`);
  const connectedAt = Date.now();
  heartbeat = setInterval(() => {
    const memoryMiB = Math.round(process.memoryUsage().rss / 1024 / 1024);
    const task = activity.taskId ? ` task=${activity.taskId}` : "";
    console.info(
      `[heartbeat] connected state=${activity.state}${task} uptime=${Math.floor((Date.now() - connectedAt) / 1000)}s ` +
      `completed=${activity.completed} failed=${activity.failed} node_rss=${memoryMiB}MiB`
    );
  }, config.heartbeatMs);
  heartbeat.unref?.();
} catch (error) {
  runtime.close();
  provider.close();
  const category = safeErrorCategory(error);
  console.error(`FLUX.2 worker failed during ${startupStage} (${category}).`);
  console.error(remediation(category));
  process.exitCode = 1;
}

function safeErrorCategory(error) {
  if (error?.name && error.name !== "Error") return error.name;
  const message = typeof error?.message === "string" ? error.message : "";
  const grpcStatus = /MutualGPU enrollment failed \((\d+)\)/.exec(message)?.[1];
  if (grpcStatus) return `GrpcStatus${grpcStatus}`;
  const httpStatus = /HTTP request failed \((\d+)\)/.exec(message)?.[1];
  if (httpStatus) return `HttpStatus${httpStatus}`;
  if (/certificate|\bTLS\b|\bSSL\b/i.test(message)) return "TlsError";
  if (error?.code && typeof error.code === "string") return error.code;
  return "Error";
}

function remediation(category) {
  switch (category) {
    case "GrpcStatus16": return "The static provider credential was not accepted. Verify the protected key file and selected line against the live registry.";
    case "GrpcStatus6": return "The capability name already has a different shared contract. Set MUTUALGPU_FLUX2_CAPABILITY to a new versioned name.";
    case "MissingDependencies": return "Synchronize the Python project environment declared by pyproject.toml, then retry.";
    case "GpuUnavailable": return "No permitted CUDA or MPS accelerator is available to this process.";
    case "TlsError": return "Check the API certificate trust chain and local TLS environment.";
    default: return "Review the stage above; credentials and request data were not written to this diagnostic.";
  }
}
