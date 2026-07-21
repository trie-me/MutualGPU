import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiBaseUrl = required("MUTUALGPU_API_URL");
const presharedKey = required("MUTUALGPU_PROVIDER_KEY");
const executionUnitId = required("MUTUALGPU_EXECUTION_UNIT_ID");
const capabilityName = process.env.MUTUALGPU_DEMO_CAPABILITY ?? "exasplat";
const machineTier = process.env.MUTUALGPU_DEMO_MACHINE_TIER ?? "Large";
const computeTier = process.env.MUTUALGPU_DEMO_COMPUTE_TIER ?? "Large";
const memoryGiB = positiveInteger(process.env.MUTUALGPU_DEMO_MEMORY_GIB ?? "32", "MUTUALGPU_DEMO_MEMORY_GIB");
const resultSourceUrl = new URL(process.env.MUTUALGPU_DEMO_RESULT_URL ?? "https://github.com/jwg4/file_examples/raw/refs/heads/master/valid/files.zip");
const apiUrl = new URL(apiBaseUrl);
if (apiUrl.protocol !== "https:") throw new TypeError("MUTUALGPU_API_URL must use https.");
if (!isGuid(executionUnitId)) throw new TypeError("MUTUALGPU_EXECUTION_UNIT_ID must be a GUID configured by the API host.");
if (resultSourceUrl.protocol !== "https:") throw new TypeError("MUTUALGPU_DEMO_RESULT_URL must use https.");

const definition = {
  machine: { tier: machineTier, specifications: { computeTier, memoryGiB } },
  capabilities: [{
    name: capabilityName,
    inputs: [
      {
        key: "image_url",
        type: "Image",
        required: true,
        label: "Image",
        description: "Input image to convert into a 3D Gaussian splat.",
        contentTypes: ["image/png", "image/jpeg", "image/webp"]
      },
      {
        key: "num_gaussians",
        type: "Integer",
        required: false,
        label: "Number of Gaussians",
        description: "Target Gaussian count; the provider rounds it to a multiple of 32.",
        default: "262144"
      },
      {
        key: "num_inference_steps",
        type: "Integer",
        required: false,
        label: "Inference steps",
        description: "Flow-matching sampler steps. More steps improve fidelity with roughly linear runtime cost.",
        default: "20"
      },
      {
        key: "guidance_scale",
        type: "Number",
        required: false,
        label: "Guidance scale",
        description: "Classifier-free guidance strength; values at or below 1 disable guidance.",
        default: "3"
      },
      {
        key: "output_format",
        type: "String",
        required: false,
        label: "Output format",
        description: "Generated Gaussian-splat file format.",
        default: "ply",
        allowedValues: ["ply", "splat"]
      },
      {
        key: "seed",
        type: "Integer",
        required: false,
        label: "Seed",
        description: "Optional random seed for reproducible output. Leave empty for a random seed."
      },
      {
        key: "enable_safety_checker",
        type: "Boolean",
        required: false,
        label: "Enable safety checker",
        description: "The demo provider does not bundle a qualified safety checker; submit false.",
        default: "false"
      }
    ],
    output: { hasMetadata: true },
    description: `${capabilityName} synthetic image-to-splat form exercised by the live exchange demo.`
  }]
};

const provider = new ProviderClient(new NodeGrpcTransport(apiUrl, presharedKey));
await provider.enroll(definition);
console.log(`MutualGPU demo provider '${capabilityName}' (${computeTier}, ${memoryGiB} GiB) is connected.`);

await provider.connect(async task => {
  console.log(`Running synthetic demo task ${task.taskId} (attempt ${task.attemptId}).`);
  await task.accept();
  await task.reportProgress({ phase: "prepare synthetic execution", percent: 10, message: "Preparing the local demonstration result." });
  await pause(1_500);
  await task.reportProgress({ phase: `simulate ${capabilityName} inference`, percent: 60, message: "Representing the inference portion of the demo." });
  await pause(3_500);
  await task.reportProgress({ phase: "package Gaussian splat", percent: 90, message: "Packaging the synthetic provider result." });

  // The public fixture is fetched and then published through MutualGPU's ordinary
  // result upload. Requestors therefore receive the normal presigned result link,
  // not an untrusted direct provider URL.
  const resultZip = await downloadResultZip(resultSourceUrl);
  const { receipt } = await task.uploadResult({
    resultZip,
    metadata: {
      provider: `node-live-demo:${capabilityName}`,
      taskId: task.taskId,
      sourceResultUrl: resultSourceUrl.href,
      message: "Synthetic demo result; no GPU workload was executed."
    }
  });
  await task.complete(receipt);
  console.log(`Completed synthetic demo task ${task.taskId}.`);
});

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

function isGuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function positiveInteger(value, name) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isInteger(parsed) || parsed <= 0) throw new TypeError(`${name} must be a positive integer.`);
  return parsed;
}

async function downloadResultZip(url) {
  const response = await fetch(url, { headers: { Accept: "application/zip" } });
  if (!response.ok) throw new Error(`Demo result download failed (${response.status}) from ${url}.`);
  const length = Number.parseInt(response.headers.get("content-length") ?? "", 10);
  if (Number.isFinite(length) && length > 50 * 1024 * 1024) throw new Error("Demo result ZIP exceeds MutualGPU's 50 MiB result limit.");
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length < 4 || bytes[0] !== 0x50 || bytes[1] !== 0x4b) throw new Error(`Demo result from ${url} is not a ZIP file.`);
  if (bytes.length > 50 * 1024 * 1024) throw new Error("Demo result ZIP exceeds MutualGPU's 50 MiB result limit.");
  return bytes;
}

function pause(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
