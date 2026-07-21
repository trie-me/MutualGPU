import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiBaseUrl = required("MUTUALGPU_API_URL");
const presharedKey = required("MUTUALGPU_PROVIDER_KEY");
const executionUnitId = required("MUTUALGPU_EXECUTION_UNIT_ID");
const apiUrl = new URL(apiBaseUrl);
if (apiUrl.protocol !== "https:") throw new TypeError("MUTUALGPU_API_URL must use https.");
if (!isGuid(executionUnitId)) throw new TypeError("MUTUALGPU_EXECUTION_UNIT_ID must be a GUID configured by the API host.");

const definition = {
  machine: { tier: "Large", specifications: { computeTier: "Large", memoryGiB: 32 } },
  capabilities: [{
    name: "tripo-splat",
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
        description: "Run safety checking on the input image before inference.",
        default: "true"
      }
    ],
    output: { hasMetadata: true },
    description: "TripoSplat-style image-to-splat form exercised by the local exchange demo."
  }]
};

const provider = new ProviderClient(new NodeGrpcTransport(apiUrl, presharedKey));
await provider.enroll(definition);
console.log("MutualGPU local demo provider is connected. Open the requestor UI and submit a task.");

await provider.connect(async task => {
  console.log(`Running synthetic demo task ${task.taskId} (attempt ${task.attemptId}).`);
  await task.accept();
  await task.reportProgress({ phase: "prepare synthetic execution", percent: 10, message: "Preparing the local demonstration result." });
  await pause(1_500);
  await task.reportProgress({ phase: "simulate TripoSplat inference", percent: 60, message: "Representing the inference portion of the demo." });
  await pause(3_500);
  await task.reportProgress({ phase: "package Gaussian splat", percent: 90, message: "Packaging the synthetic provider result." });

  // Valid empty ZIP (EOCD only). The API validates this exactly as it validates a
  // real handler result, while making no claim that local GPU work took place.
  const resultZip = Uint8Array.from(Buffer.from("UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==", "base64"));
  const { receipt } = await task.uploadResult({
    resultZip,
    metadata: {
      provider: "node-local-demo",
      taskId: task.taskId,
      message: "Synthetic local-demo result; no GPU workload was executed."
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

function pause(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
