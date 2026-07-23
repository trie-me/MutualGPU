import { randomInt } from "node:crypto";

const TIERS = new Set(["Small", "Medium", "Large", "ExtraLarge"]);
const MEMORY_LIMITS = {
  Small: [8, 16], Medium: [8, 24], Large: [8, 48], ExtraLarge: [8, 128]
};

export class AssignmentValidationError extends Error {
  constructor(message) {
    super(message);
    this.name = "AssignmentValidationError";
  }
}

export function loadConfig(environment = process.env) {
  const apiUrl = requiredUrl(environment, "MUTUALGPU_API_URL");
  const providerKey = required(environment, "MUTUALGPU_PROVIDER_KEY");
  const machineTier = tier(environment.MUTUALGPU_FLUX2_MACHINE_TIER ?? "Large", "MUTUALGPU_FLUX2_MACHINE_TIER");
  const computeTier = tier(environment.MUTUALGPU_FLUX2_COMPUTE_TIER ?? machineTier, "MUTUALGPU_FLUX2_COMPUTE_TIER");
  const [minimumMemory, maximumMemory] = MEMORY_LIMITS[computeTier];
  const memoryGiB = integer(environment.MUTUALGPU_FLUX2_MEMORY_GIB ?? "16", "MUTUALGPU_FLUX2_MEMORY_GIB", minimumMemory, maximumMemory);
  return Object.freeze({
    apiUrl,
    providerKey,
    capabilityName: environment.MUTUALGPU_FLUX2_CAPABILITY ?? "flux2-klein-4b",
    machineTier,
    computeTier,
    memoryGiB,
    python: environment.MUTUALGPU_FLUX2_PYTHON ?? "python3",
    model: environment.MUTUALGPU_FLUX2_MODEL ?? "black-forest-labs/FLUX.2-klein-4B",
    safetyModel: environment.MUTUALGPU_FLUX2_SAFETY_MODEL ?? "CompVis/stable-diffusion-safety-checker",
    device: environment.MUTUALGPU_FLUX2_DEVICE ?? "auto",
    localFilesOnly: boolean(environment.MUTUALGPU_FLUX2_LOCAL_FILES_ONLY ?? "false", "MUTUALGPU_FLUX2_LOCAL_FILES_ONLY"),
    allowCpu: boolean(environment.MUTUALGPU_FLUX2_ALLOW_CPU ?? "false", "MUTUALGPU_FLUX2_ALLOW_CPU"),
    heartbeatMs: integer(environment.MUTUALGPU_FLUX2_HEARTBEAT_SECONDS ?? "15", "MUTUALGPU_FLUX2_HEARTBEAT_SECONDS", 1, 3600) * 1000,
    startupTimeoutMs: integer(environment.MUTUALGPU_FLUX2_STARTUP_TIMEOUT_SECONDS ?? "3600", "MUTUALGPU_FLUX2_STARTUP_TIMEOUT_SECONDS", 60, 7200) * 1000,
    generationTimeoutMs: integer(environment.MUTUALGPU_FLUX2_TIMEOUT_SECONDS ?? "900", "MUTUALGPU_FLUX2_TIMEOUT_SECONDS", 30, 3600) * 1000
  });
}

export function buildEnrollment(config) {
  return {
    machine: {
      tier: config.machineTier,
      specifications: { computeTier: config.computeTier, memoryGiB: config.memoryGiB }
    },
    capabilities: [{
      name: config.capabilityName,
      description: "Generates one PNG image with a locally hosted FLUX.2 pipeline.",
      inputs: [
        { key: "prompt", type: "String", required: true, label: "Prompt", description: "Text describing the image to generate.", displayOrder: 0 },
        { key: "num_inference_steps", type: "Integer", required: false, label: "Inference steps", default: "4", minimum: 1, maximum: 50, displayOrder: 1 },
        { key: "guidance_scale", type: "Number", required: false, label: "Guidance scale", default: "7", minimum: 0, maximum: 20, displayOrder: 2 },
        { key: "width", type: "Integer", required: false, label: "Width", default: "1024", minimum: 256, maximum: 1024, displayOrder: 3 },
        { key: "height", type: "Integer", required: false, label: "Height", default: "1024", minimum: 256, maximum: 1024, displayOrder: 4 },
        { key: "seed", type: "Integer", required: false, label: "Seed", description: "Optional deterministic seed. A random seed is chosen when omitted.", minimum: 0, maximum: 2147483647, displayOrder: 5 }
      ],
      output: {
        hasThumbnail: true,
        hasPreview: true,
        hasMetadata: true,
        hasLogs: true,
        previewContentTypes: ["image/png"]
      }
    }]
  };
}

export function parseGenerationRequest(scalars = {}, chooseSeed = () => randomInt(0, 2147483648)) {
  const prompt = text(scalars.prompt, "prompt", { required: true, maximumLength: 2000 });
  const steps = scalarInteger(scalars.num_inference_steps ?? "4", "num_inference_steps", 1, 50);
  const guidanceScale = scalarNumber(scalars.guidance_scale ?? "7", "guidance_scale", 0, 20);
  const width = scalarInteger(scalars.width ?? "1024", "width", 256, 1024);
  const height = scalarInteger(scalars.height ?? "1024", "height", 256, 1024);
  if (width % 8 !== 0 || height % 8 !== 0) throw new AssignmentValidationError("width and height must be divisible by 8");
  if (width * height > 1024 * 1024) throw new AssignmentValidationError("the requested image contains too many pixels");
  const seed = scalars.seed == null || scalars.seed === "" ? chooseSeed() : scalarInteger(scalars.seed, "seed", 0, 2147483647);
  return Object.freeze({ prompt, steps, guidanceScale, width, height, seed });
}

function required(environment, name) {
  const value = environment[name];
  if (typeof value !== "string" || value.trim() === "") throw new TypeError(`${name} is required.`);
  return value;
}

function requiredUrl(environment, name) {
  const value = new URL(required(environment, name));
  if (value.protocol !== "https:") throw new TypeError(`${name} must use https.`);
  return value;
}

function tier(value, name) {
  if (!TIERS.has(value)) throw new TypeError(`${name} must be Small, Medium, Large, or ExtraLarge.`);
  return value;
}

function integer(value, name, minimum, maximum) {
  if (!/^\d+$/.test(String(value))) throw new TypeError(`${name} must be a whole number.`);
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) throw new TypeError(`${name} must be between ${minimum} and ${maximum}.`);
  return parsed;
}

function boolean(value, name) {
  if (value === "true") return true;
  if (value === "false") return false;
  throw new TypeError(`${name} must be true or false.`);
}

function text(value, name, { required = false, maximumLength }) {
  if (typeof value !== "string") throw new AssignmentValidationError(`${name} must be text`);
  const normalized = value.trim();
  if (required && normalized === "") throw new AssignmentValidationError(`${name} is required`);
  if (normalized.length > maximumLength) throw new AssignmentValidationError(`${name} is too long`);
  return normalized;
}

function scalarInteger(value, name, minimum, maximum) {
  if (!/^-?\d+$/.test(String(value))) throw new AssignmentValidationError(`${name} must be an integer`);
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) throw new AssignmentValidationError(`${name} must be between ${minimum} and ${maximum}`);
  return parsed;
}

function scalarNumber(value, name, minimum, maximum) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < minimum || parsed > maximum) throw new AssignmentValidationError(`${name} must be between ${minimum} and ${maximum}`);
  return parsed;
}
