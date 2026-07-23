import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";

const MAX_ZIP_BYTES = 50 * 1024 * 1024;
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;

export class Flux2Runtime {
  #options;
  #child = null;
  #ready = null;
  #pending = null;
  #nextId = 1;
  #closed = false;

  constructor(options) { this.#options = options; }
  get info() { return this.#ready?.info; }

  async start() {
    if (this.#child && this.#ready?.info) return this.#ready.info;
    this.#closed = false;
    const child = spawn(this.#options.python, ["-u", this.#options.scriptPath, "--serve"], {
      stdio: ["pipe", "pipe", "pipe"],
      env: runtimeEnvironment(this.#options)
    });
    this.#child = child;
    const readyPromise = new Promise((resolve, reject) => { this.#ready = { resolve, reject, info: null }; });
    createInterface({ input: child.stdout }).on("line", line => this.#receiveLine(child, line));
    child.stderr.on("data", chunk => {
      const text = chunk.toString();
      if (text) (this.#options.log ?? (message => process.stderr.write(message)))(`[python] ${text}`);
    });
    child.once("error", error => this.#processEnded(child, error));
    child.once("exit", (code, signal) => this.#processEnded(child, new Error(`FLUX.2 runtime exited (${signal ?? code ?? "unknown"}).`)));

    const timeout = setTimeout(() => {
      this.#processEnded(child, new Error("FLUX.2 runtime startup timed out."));
      child.kill("SIGTERM");
    }, this.#options.startupTimeoutMs ?? 120_000);
    timeout.unref?.();
    try {
      return await readyPromise;
    } finally {
      clearTimeout(timeout);
    }
  }

  async generate(request, { onProgress = () => {}, signal } = {}) {
    if (this.#pending) throw new Error("FLUX.2 already owns an active generation.");
    await this.start();
    if (signal?.aborted) throw abortError();
    const outputDirectory = await mkdtemp(join(tmpdir(), "mutualgpu-flux2-"));
    const id = String(this.#nextId++);
    let abortListener;
    try {
      const result = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
          reject(new Error("FLUX.2 generation timed out."));
          this.#child?.kill("SIGTERM");
        }, this.#options.generationTimeoutMs);
        timeout.unref?.();
        this.#pending = { id, outputDirectory, onProgress, resolve, reject, timeout };
      });
      abortListener = () => {
        this.#pending?.reject(abortError());
        this.#child?.kill("SIGTERM");
      };
      signal?.addEventListener("abort", abortListener, { once: true });
      this.#child.stdin.write(`${JSON.stringify({ type: "generate", id, outputDirectory, ...request })}\n`);
      return await result;
    } finally {
      signal?.removeEventListener("abort", abortListener);
      if (this.#pending?.id === id) this.#clearPending();
      await rm(outputDirectory, { recursive: true, force: true });
    }
  }

  close() {
    this.#closed = true;
    const child = this.#child;
    this.#ready?.reject(new Error("FLUX.2 runtime is closing."));
    this.#ready = null;
    this.#pending?.reject(new Error("FLUX.2 runtime is closing."));
    this.#clearPending();
    this.#child = null;
    child?.kill("SIGTERM");
  }

  #receiveLine(child, line) {
    if (child !== this.#child) return;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      this.#processEnded(child, new Error("FLUX.2 runtime emitted an invalid protocol message."));
      child.kill("SIGTERM");
      return;
    }
    if (message.type === "ready") {
      const info = Object.freeze({ device: message.device, model: message.model });
      if (this.#ready) this.#ready.info = info;
      this.#ready?.resolve(info);
      return;
    }
    if (message.type === "startup_error") {
      const error = new Error("FLUX.2 runtime preflight failed.");
      error.name = message.category ?? "Flux2StartupError";
      this.#processEnded(child, error);
      child.kill("SIGTERM");
      return;
    }
    const pending = this.#pending;
    if (!pending || message.id !== pending.id) return;
    if (message.type === "progress") {
      void Promise.resolve(pending.onProgress({
        phase: message.phase ?? "inference",
        percent: Math.max(1, Math.min(94, Number(message.percent) || 1)),
        message: message.message ?? "Running FLUX.2 inference."
      })).catch(() => {});
      return;
    }
    if (message.type === "error") {
      const category = message.category ?? "Flux2Error";
      const error = new Error(category === "SafetyCheckRejected" ? "The generated image was blocked as inappropriate content." : "FLUX.2 inference failed.");
      error.name = category;
      pending.reject(error);
      this.#clearPending();
      return;
    }
    if (message.type === "result") {
      void collectResult(pending.outputDirectory, message.metadata)
        .then(pending.resolve, pending.reject)
        .finally(() => this.#clearPending());
    }
  }

  #processEnded(child, error) {
    if (child !== this.#child) return;
    this.#child = null;
    this.#ready?.reject(error);
    this.#ready = null;
    this.#pending?.reject(error);
    this.#clearPending();
  }

  #clearPending() {
    if (!this.#pending) return;
    clearTimeout(this.#pending.timeout);
    this.#pending = null;
  }
}

async function collectResult(outputDirectory, metadata) {
  const [resultZip, preview, thumbnail] = await Promise.all([
    readFile(join(outputDirectory, "result.zip")),
    readFile(join(outputDirectory, "image.png")),
    readFile(join(outputDirectory, "thumbnail.png"))
  ]);
  assertSize(resultZip, MAX_ZIP_BYTES, "result ZIP");
  assertSize(preview, MAX_IMAGE_BYTES, "preview");
  assertSize(thumbnail, MAX_IMAGE_BYTES, "thumbnail");
  assertSignature(resultZip, [0x50, 0x4b], "result ZIP");
  assertSignature(preview, [0x89, 0x50, 0x4e, 0x47], "preview");
  assertSignature(thumbnail, [0x89, 0x50, 0x4e, 0x47], "thumbnail");
  return {
    resultZip, preview, thumbnail, metadata,
    logs: `FLUX.2 completed in ${metadata?.durationMs ?? "unknown"} ms.\n`
  };
}

function assertSize(value, maximum, name) {
  if (value.length === 0 || value.length > maximum) throw new Error(`FLUX.2 ${name} has an invalid size.`);
}

function assertSignature(value, signature, name) {
  if (signature.some((byte, index) => value[index] !== byte)) throw new Error(`FLUX.2 ${name} has an invalid format.`);
}

function runtimeEnvironment(options) {
  const environment = { ...process.env };
  for (const name of Object.keys(environment)) {
    if (/^MUTUALGPU_.*(?:KEY|PASSWORD|SECRET|TOKEN)$/i.test(name)) delete environment[name];
  }
  return {
    ...environment,
    MUTUALGPU_FLUX2_MODEL: options.model,
    MUTUALGPU_FLUX2_SAFETY_MODEL: options.safetyModel,
    MUTUALGPU_FLUX2_DEVICE: options.device,
    MUTUALGPU_FLUX2_LOCAL_FILES_ONLY: String(options.localFilesOnly),
    MUTUALGPU_FLUX2_ALLOW_CPU: String(options.allowCpu)
  };
}

function abortError() {
  const error = new Error("FLUX.2 generation was cancelled.");
  error.name = "AbortError";
  return error;
}
