const MODEL_ID = "black-forest-labs/FLUX.2-klein-4B";
const MODEL_REVISION = "e7b7dc27f91deacad38e78976d1f2b499d76a294";
const MODEL_REPOSITORY = "KatzenStuff/flux-2-klein-4b-webgpu";
const ARTIFACT_REVISION = "2cd85d938ff8afb954661262c12c6d10676a20e1";
const DEFAULT_MODEL_BASE_URL = `https://huggingface.co/${MODEL_REPOSITORY}/resolve/${ARTIFACT_REVISION}/models/klein-4b`;
const MODEL_BASE_URL = String(
  globalThis.MUTUALGPU_FLUX2_WEBGPU_MODEL_BASE_URL ?? DEFAULT_MODEL_BASE_URL
).replace(/\/+$/, "");
const ORT_MODULE_URL = "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort.webgpu.bundle.min.mjs";
const TRANSFORMERS_MODULE_URL = "https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.8.1";
const PIPELINE_MANIFEST_URL = `${MODEL_BASE_URL}/pipeline-1024/manifest.json`;
const RUNTIME_BUILD = "20260726-klein-fp16-adapter-4";
const MODEL_CACHE_NAME = `mutualgpu-flux2-klein-4b-${ARTIFACT_REVISION}-v1`;
const MODEL_CACHE_WORKER_URL = `/model-cache-worker.js?v=${encodeURIComponent(MODEL_CACHE_NAME)}`;
const TOKEN_COUNT = 512;
const TOKEN_EMBEDDING_WIDTH = 7680;
const MASK_64 = (1n << 64n) - 1n;
const UINT32_SCALE = 1 / 0x1_0000_0000;

let runtimePromise;
let sessionCreationTail = Promise.resolve();
let sessionCreationsQueued = 0;

export function loadFlux2WebGpuRuntime({ adapter, onStatus = () => {} } = {}) {
  runtimePromise ??= initialize(adapter, onStatus).catch(error => {
    runtimePromise = null;
    throw error;
  });
  return runtimePromise;
}

async function initialize(requestedAdapter, onStatus) {
  if (!navigator.gpu) throw new Error("WebGPU is unavailable.");
  onStatus(`FLUX.2 runtime diagnostic build ${RUNTIME_BUILD}; ${browserEnvironmentSummary()}.`);
  await initializeModelCache(onStatus);
  const { adapter } = await selectF16Adapter(requestedAdapter, onStatus);

  onStatus("Loading the FLUX.2 Klein 4B model manifests from Hugging Face.");
  const pipelineLoaded = await fetchJson(PIPELINE_MANIFEST_URL);
  const pipelineManifest = resolveManifestUrls(pipelineLoaded.value, pipelineLoaded.url);
  assertPipelineManifest(pipelineManifest);
  const [textLoaded, transformerLoaded, vaeLoaded] = await Promise.all([
    fetchJson(resolveModelUrl(pipelineManifest.textEncoderManifestUrl)),
    fetchJson(resolveModelUrl(pipelineManifest.transformerManifestUrl)),
    fetchJson(resolveModelUrl(pipelineManifest.vaeManifestUrl))
  ]);
  const textManifest = resolveManifestUrls(textLoaded.value, textLoaded.url);
  const transformerManifest = resolveManifestUrls(transformerLoaded.value, transformerLoaded.url);
  const vaeManifest = resolveManifestUrls(vaeLoaded.value, vaeLoaded.url);
  assertComponentManifest(textManifest, "text-encoder");
  assertComponentManifest(transformerManifest, "transformer");
  assertComponentManifest(vaeManifest, "vae-decoder");
  if (transformerManifest.parts?.length !== 4) {
    throw new Error("The FLUX.2 transformer manifest must contain four validated partitions.");
  }
  if (vaeManifest.dtype !== "float16") {
    throw new Error("The FLUX.2 browser integration requires the validated FP16 VAE.");
  }

  onStatus("Loading the browser inference and tokenizer runtimes.");
  const [ort, transformers] = await Promise.all([
    import(ORT_MODULE_URL),
    import(TRANSFORMERS_MODULE_URL)
  ]);
  ort.env.wasm.numThreads = 1;
  // Pin ORT to the adapter we checked. Otherwise it can request a second,
  // different adapter on a dual-GPU system.
  ort.env.webgpu.adapter = adapter;
  transformers.env.allowLocalModels = false;
  transformers.env.allowRemoteModels = true;
  transformers.env.useBrowserCache = true;
  transformers.env.cacheKey = `${MODEL_CACHE_NAME}-tokenizer`;
  const tokenizer = await transformers.AutoTokenizer.from_pretrained(MODEL_REPOSITORY, {
    local_files_only: false,
    revision: ARTIFACT_REVISION
  });

  onStatus("FLUX.2 Klein 4B manifests and browser runtimes are ready.");
  return createRuntime({
    adapterName: adapter.info?.description || adapter.info?.vendor || "WebGPU adapter",
    ort,
    pipelineManifest,
    textManifest,
    transformerManifest,
    vaeManifest,
    tokenizer,
    onStatus
  });
}

async function selectF16Adapter(requestedAdapter, onStatus) {
  const choices = requestedAdapter
    ? [["caller-selected", requestedAdapter, null]]
    : [
      ["high-performance", null, { powerPreference: "high-performance" }],
      ["default", null, undefined],
      ["low-power", null, { powerPreference: "low-power" }]
    ];
  const diagnostics = [];

  for (const [preference, suppliedAdapter, options] of choices) {
    let adapter = suppliedAdapter;
    try {
      adapter ??= await navigator.gpu.requestAdapter(options);
    } catch (error) {
      diagnostics.push(`${preference}=request failed (${shortText(error?.message || "unknown error")})`);
      continue;
    }
    if (!adapter) {
      diagnostics.push(`${preference}=unavailable`);
      continue;
    }
    const diagnostic = describeWebGpuAdapter(adapter);
    onStatus(`WebGPU ${preference} candidate: ${diagnostic}.`);
    if (adapter.features.has("shader-f16")) {
      onStatus(`Selected ${preference} WebGPU adapter with shader-f16.`);
      return { adapter, preference };
    }
    diagnostics.push(`${preference}=${diagnostic}`);
  }

  throw new Error(
    "No Chrome WebGPU adapter exposed shader-f16 for the FP16 FLUX.2 model. " +
    `Tried ${diagnostics.join("; ")}. Check chrome://gpu for a hardware WebGPU/Vulkan backend.`
  );
}

function describeWebGpuAdapter(adapter) {
  const info = adapter.info || {};
  const identity = [
    info.description && `description=${info.description}`,
    info.vendor && `vendor=${info.vendor}`,
    info.architecture && `architecture=${info.architecture}`,
    info.device && `device=${info.device}`,
    info.driver && `driver=${info.driver}`
  ].filter(Boolean).join(", ") || "identity unavailable";
  const features = [...adapter.features].sort();
  return `${identity}; shader-f16=${features.includes("shader-f16")}; features=[${features.join(", ") || "none"}]`;
}

function createRuntime(state) {
  let transformerSessions = [];
  let vaeSession = null;
  let rotaryCos = null;
  let rotarySin = null;
  let bnMean = null;
  let bnStandardDeviation = null;
  let promptCache = null;
  let busy = false;
  let aborted = false;
  let observedWebGpuDevice = null;
  let lastDeviceLoss = null;
  let activeStage = "idle";

  function stage(name, onStatus, message) {
    activeStage = name;
    if (message) onStatus(message);
  }

  function observeWebGpuDevice(onStatus) {
    const device = state.ort.env?.webgpu?.device;
    if (!device || device === observedWebGpuDevice || !device.lost) return;
    observedWebGpuDevice = device;
    void device.lost.then(info => {
      lastDeviceLoss = {
        reason: String(info?.reason || "unknown"),
        message: shortText(info?.message || "no message")
      };
      onStatus(`WebGPU device lost: ${lastDeviceLoss.reason}; ${lastDeviceLoss.message}.`);
    }).catch(() => {});
  }

  async function releaseImagePipeline() {
    const releases = transformerSessions.map(session => session.release());
    if (vaeSession) releases.push(vaeSession.release());
    await Promise.all(releases);
    transformerSessions = [];
    vaeSession = null;
    rotaryCos?.dispose();
    rotarySin?.dispose();
    rotaryCos = null;
    rotarySin = null;
    bnMean = null;
    bnStandardDeviation = null;
  }

  async function encodePrompt(prompt, onStatus) {
    const normalized = prompt.normalize("NFKC").replace(/\s+/g, " ").trim();
    if (!normalized) throw new Error("The prompt must not be empty.");
    if (promptCache?.prompt === normalized) return promptCache.bits.slice();

    if (transformerSessions.length || vaeSession) {
      onStatus("Releasing the image pipeline before loading the Qwen prompt encoder.");
      await releaseImagePipeline();
    }
    throwIfAborted(aborted);
    onStatus("Tokenizing the prompt for the FLUX.2 Qwen encoder.");
    const { inputIds, causalPaddingMask } = tokenizePrompt(state.tokenizer, normalized);
    stage("text_encoder_graph_download", onStatus, "Downloading the Qwen prompt-encoder graph.");
    const graph = await fetchBuffer(state.textManifest.graph, onStatus, "Qwen prompt-encoder graph");
    throwIfAborted(aborted);
    stage("text_encoder_session_create", onStatus, "Creating the Qwen prompt-encoder WebGPU session.");
    const session = await createSession(state.ort, graph, state.textManifest.externalData, false, "NCHW", {
      label: "Qwen prompt encoder",
      onStatus
    });
    observeWebGpuDevice(onStatus);
    try {
      throwIfAborted(aborted);
      stage("text_encoder_run", onStatus, "Encoding the prompt on WebGPU.");
      const inputIdsTensor = new state.ort.Tensor("int64", inputIds, [1, TOKEN_COUNT]);
      const maskTensor = new state.ort.Tensor(
        "bool",
        causalPaddingMask,
        [1, 1, TOKEN_COUNT, TOKEN_COUNT]
      );
      const output = await session.run({
        input_ids: inputIdsTensor,
        causal_padding_mask: maskTensor
      });
      inputIdsTensor.dispose();
      maskTensor.dispose();
      const embeddings = output.prompt_embeddings;
      if (!(embeddings?.data instanceof Float32Array)) {
        throw new Error("The Qwen encoder returned an unexpected prompt tensor.");
      }
      const bits = float32ToHalfBits(embeddings.data);
      embeddings.dispose();
      promptCache = { prompt: normalized, bits: bits.slice() };
      return bits;
    } finally {
      await session.release();
    }
  }

  async function loadImagePipeline(onStatus) {
    if (transformerSessions.length === 4 && vaeSession) return;
    await releaseImagePipeline();
    throwIfAborted(aborted);
    onStatus("Loading four FP16 transformer partitions on WebGPU.");
    for (const part of state.transformerManifest.parts) {
      throwIfAborted(aborted);
      const partLabel = `transformer partition ${part.index + 1} of ${state.transformerManifest.parts.length}`;
      stage(`transformer_part_${part.index + 1}_graph_download`, onStatus, `Downloading ${partLabel} graph.`);
      const graph = await fetchBuffer(part.graph, onStatus, `${partLabel} graph`);
      stage(`transformer_part_${part.index + 1}_session_create`, onStatus, `Creating the ${partLabel} WebGPU session.`);
      transformerSessions.push(await createSession(
        state.ort,
        graph,
        part.externalData,
        part.index < state.transformerManifest.parts.length - 1,
        "NCHW",
        { label: partLabel, onStatus }
      ));
      observeWebGpuDevice(onStatus);
      onStatus(`Loaded transformer partition ${part.index + 1} of ${state.transformerManifest.parts.length}.`);
    }

    const [rotaryCosBuffer, rotarySinBuffer, meanBuffer, standardDeviationBuffer, vaeGraph] =
      await Promise.all([
        fetchBuffer(state.transformerManifest.inputs.rotaryCos, onStatus, "rotary cosine input"),
        fetchBuffer(state.transformerManifest.inputs.rotarySin, onStatus, "rotary sine input"),
        fetchBuffer(state.pipelineManifest.latent.bnMean, onStatus, "latent normalization mean"),
        fetchBuffer(state.pipelineManifest.latent.bnStandardDeviation, onStatus, "latent normalization standard deviation"),
        fetchBuffer(state.vaeManifest.graph, onStatus, "VAE decoder graph")
      ]);
    throwIfAborted(aborted);
    rotaryCos = new state.ort.Tensor(
      "float32",
      new Float32Array(rotaryCosBuffer),
      state.transformerManifest.inputs.rotaryCos.shape
    );
    rotarySin = new state.ort.Tensor(
      "float32",
      new Float32Array(rotarySinBuffer),
      state.transformerManifest.inputs.rotarySin.shape
    );
    bnMean = new Float32Array(meanBuffer);
    bnStandardDeviation = new Float32Array(standardDeviationBuffer);
    stage("vae_session_create", onStatus, "Creating the FP16 VAE decoder WebGPU session.");
    vaeSession = await createSession(state.ort, vaeGraph, [], false, "NHWC", {
      label: "FP16 VAE decoder",
      onStatus
    });
    observeWebGpuDevice(onStatus);
  }

  async function generate({
    prompt,
    width = 1024,
    height = 1024,
    steps = 4,
    seed = 0,
    inputUrl,
    onStatus = () => {}
  }) {
    if (busy) throw new Error("This browser provider is already running a FLUX.2 assignment.");
    if (inputUrl) throw new Error("The replacement FLUX.2 WebGPU model does not provide image-to-image inference.");
    if (width !== 1024 || height !== 1024) throw new Error("The replacement FLUX.2 WebGPU model is fixed at 1024×1024.");
    if (steps !== 4) throw new Error("The distilled replacement FLUX.2 WebGPU model is fixed at four denoising steps.");
    if (!Number.isSafeInteger(seed) || seed < 0 || seed > 2147483647) {
      throw new Error("The FLUX.2 seed must be an integer between 0 and 2147483647.");
    }

    busy = true;
    aborted = false;
    const started = performance.now();
    const stepTimes = [];
    try {
      stage("prompt_encode", onStatus, "Starting FLUX.2 browser inference diagnostics.");
      const promptBits = await encodePrompt(prompt, onStatus);
      throwIfAborted(aborted);
      stage("image_pipeline_load", onStatus, "Preparing the FLUX.2 transformer and VAE pipeline.");
      await loadImagePipeline(onStatus);
      throwIfAborted(aborted);

      const promptTensor = makeFloat16Tensor(
        state.ort,
        promptBits,
        [1, TOKEN_COUNT, TOKEN_EMBEDDING_WIDTH]
      );
      let latentBits = makeSeededLatents(
        seed,
        product(state.pipelineManifest.latent.packedShape)
      );
      try {
        for (let step = 0; step < state.pipelineManifest.steps; step += 1) {
          throwIfAborted(aborted);
          const stepStarted = performance.now();
          stage(`denoise_step_${step + 1}`, onStatus, `Denoising on WebGPU — step ${step + 1} of ${state.pipelineManifest.steps}.`);
          const timestep = makeFloat16Tensor(
            state.ort,
            Uint16Array.of(numberToHalf(state.pipelineManifest.scheduler.modelTimesteps[step])),
            [1]
          );
          const hiddenStates = makeFloat16Tensor(
            state.ort,
            latentBits,
            state.pipelineManifest.latent.packedShape
          );
          const first = await transformerSessions[0].run({
            hidden_states: hiddenStates,
            prompt_embeddings: promptTensor,
            timestep,
            rotary_cos: rotaryCos,
            rotary_sin: rotarySin
          });
          hiddenStates.dispose();
          timestep.dispose();
          const second = await transformerSessions[1].run({
            image_hidden: first.image_hidden,
            text_hidden: first.text_hidden,
            single_mod: first.single_mod,
            rotary_cos: rotaryCos,
            rotary_sin: rotarySin
          });
          const third = await transformerSessions[2].run({
            single_hidden_one: second.single_hidden_one,
            single_mod: first.single_mod,
            rotary_cos: rotaryCos,
            rotary_sin: rotarySin
          });
          const fourth = await transformerSessions[3].run({
            single_hidden_two: third.single_hidden_two,
            single_mod: first.single_mod,
            rotary_cos: rotaryCos,
            rotary_sin: rotarySin,
            temb: first.temb
          });
          const noiseBits = float16Bits(fourth.noise_pred.data);
          const next = new Uint16Array(latentBits.length);
          const delta =
            state.pipelineManifest.scheduler.sigmas[step + 1] -
            state.pipelineManifest.scheduler.sigmas[step];
          for (let index = 0; index < next.length; index += 1) {
            next[index] = numberToHalf(
              halfToNumber(latentBits[index]) + delta * halfToNumber(noiseBits[index])
            );
          }
          first.text_hidden.dispose();
          first.image_hidden.dispose();
          first.temb.dispose();
          first.single_mod.dispose();
          second.single_hidden_one.dispose();
          third.single_hidden_two.dispose();
          fourth.noise_pred.dispose();
          latentBits = next;
          stepTimes.push(performance.now() - stepStarted);
        }

        throwIfAborted(aborted);
        stage("vae_run", onStatus, "Decoding the final latents with the FP16 VAE on WebGPU.");
        const vaeInput = unpackForVae(
          latentBits,
          state.pipelineManifest,
          bnMean,
          bnStandardDeviation
        );
        const vaeStarted = performance.now();
        const latentSample = makeFloat16Tensor(
          state.ort,
          float32ToHalfBits(vaeInput),
          state.pipelineManifest.latent.vaeShape
        );
        const decoded = await vaeSession.run({ latent_sample: latentSample });
        latentSample.dispose();
        const vaeMs = performance.now() - vaeStarted;
        const sample = decoded.sample?.data;
        if (!sample) throw new Error("The FLUX.2 VAE returned no image tensor.");
        const image = await encodePng(sample, width, height);
        decoded.sample.dispose();
        return {
          image,
          metadata: {
            model: MODEL_ID,
            modelRevision: MODEL_REVISION,
            artifactRepository: MODEL_REPOSITORY,
            backend: "onnxruntime-web@1.27.0-webgpu",
            dtype: "float16",
            quantized: false,
            operation: "text-to-image",
            seed,
            width,
            height,
            numInferenceSteps: steps,
            guidanceScale: 1,
            adapter: state.adapterName,
            timings: {
              denoisingStepMs: stepTimes,
              denoisingMs: stepTimes.reduce((total, value) => total + value, 0),
              vaeMs,
              totalMs: performance.now() - started
            }
          }
        };
      } finally {
        promptTensor.dispose();
      }
    } catch (error) {
      throw withRuntimeDiagnostic(error, {
        build: RUNTIME_BUILD,
        stage: activeStage,
        elapsedMs: Math.round(performance.now() - started),
        environment: browserEnvironmentSummary(),
        memory: memorySummary(),
        deviceLoss: lastDeviceLoss
      });
    } finally {
      busy = false;
    }
  }

  return Object.freeze({
    generate,
    abort() {
      aborted = true;
    }
  });
}

function assertPipelineManifest(manifest) {
  assertComponentManifest(manifest, "pipeline");
  if (
    manifest.resolution !== 1024 ||
    manifest.steps !== 4 ||
    manifest.guidanceScale !== 1 ||
    !manifest.textEncoderManifestUrl ||
    !manifest.transformerManifestUrl ||
    !manifest.vaeManifestUrl
  ) {
    throw new Error("The Hugging Face pipeline manifest is not the validated 1024px four-step Klein product.");
  }
}

function assertComponentManifest(manifest, component) {
  if (
    manifest?.formatVersion !== 1 ||
    manifest.modelId !== MODEL_ID ||
    manifest.revision !== MODEL_REVISION ||
    manifest.component !== component
  ) {
    throw new Error(`The ${component} manifest does not match the pinned FLUX.2 Klein 4B product.`);
  }
}

async function fetchJson(url) {
  // Preserve the requested manifest URL. Hugging Face redirects raw files to a
  // blob CDN whose URL is not a usable base for sibling manifest resources.
  const requestedUrl = new URL(String(url), window.location.href).toString();
  const response = await fetch(requestedUrl, { credentials: "omit", cache: "no-store" });
  if (!response.ok) throw new Error(`Model manifest download failed (${response.status}).`);
  return { value: await response.json(), url: requestedUrl };
}

function resolveManifestUrls(value, manifestUrl) {
  if (Array.isArray(value)) {
    return value.map(item => resolveManifestUrls(item, manifestUrl));
  }
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) => {
    if (typeof item === "string" && (key === "url" || key.endsWith("ManifestUrl"))) {
      return [key, new URL(item, manifestUrl).toString()];
    }
    return [key, resolveManifestUrls(item, manifestUrl)];
  }));
}

async function fetchBuffer(item, onStatus = () => {}, label = "model artifact") {
  const url = cacheableArtifactUrl(item);
  const started = performance.now();
  let response;
  onStatus(`Caching ${humanBytes(item.byteLength)} ${label}.`);
  try {
    const cacheState = await cacheModelArtifact(item, label);
    onStatus(`${cacheState === "hit" ? "Using cached" : "Persisted"} ${label} (${humanBytes(item.byteLength)}).`);
    response = await fetch(url, { credentials: "omit", cache: "no-store" });
    const responseLength = response.headers.get("content-length");
    const source = safeUrlHost(response.url || url);
    onStatus(`Received ${label} response: HTTP ${response.status} from ${source}; content-length ${responseLength || "not supplied"}.`);
    if (!response.ok) throw new Error(`Model artifact download failed (${response.status}).`);
    const data = await response.arrayBuffer();
    if (data.byteLength !== item.byteLength) {
      throw new Error(`Model artifact length mismatch: expected ${item.byteLength}, received ${data.byteLength}.`);
    }
    onStatus(`Verified ${label}: ${humanBytes(data.byteLength)} in ${formatDuration(performance.now() - started)}.`);
    return data;
  } catch (error) {
    throw withRuntimeDiagnostic(error, {
      stage: "artifact_download",
      artifact: label,
      expectedBytes: item.byteLength,
      receivedBytes: response ? Number(response.headers.get("content-length")) || null : null,
      responseStatus: response?.status ?? null,
      source: safeUrlHost(response?.url || url),
      elapsedMs: Math.round(performance.now() - started),
      environment: browserEnvironmentSummary(),
      memory: memorySummary()
    });
  }
}

function resolveModelUrl(url) {
  const value = String(url);
  if (/^https:\/\//.test(value)) return value;
  const path = value.split("?")[0]
    .replace(/^\/models\/klein-4b\//, "")
    .replace(/^\/+/, "");
  const query = value.includes("?") ? `?${value.split("?").slice(1).join("?")}` : "";
  return `${MODEL_BASE_URL}/${path}${query}`;
}

function cacheableArtifactUrl(item) {
  const url = new URL(resolveModelUrl(item.url));
  url.searchParams.set("mutualgpu_bytes", String(item.byteLength));
  return url.toString();
}

async function initializeModelCache(onStatus) {
  if (!globalThis.isSecureContext || !("serviceWorker" in navigator) || !("caches" in globalThis)) {
    throw withRuntimeDiagnostic(new Error("Persistent model caching requires Cache Storage and a controlling service worker in a secure browser context."), {
      stage: "artifact_cache",
      build: RUNTIME_BUILD,
      environment: browserEnvironmentSummary()
    });
  }

  onStatus(`Starting persistent FLUX.2 artifact cache ${MODEL_CACHE_NAME}.`);
  const expectedWorkerUrl = new URL(MODEL_CACHE_WORKER_URL, location.href).toString();
  const registration = await navigator.serviceWorker.register(MODEL_CACHE_WORKER_URL, {
    scope: "/",
    type: "module"
  });
  await navigator.serviceWorker.ready;
  if (navigator.serviceWorker.controller?.scriptURL !== expectedWorkerUrl) {
    await waitForServiceWorkerController(expectedWorkerUrl);
  }
  if (navigator.serviceWorker.controller?.scriptURL !== expectedWorkerUrl) {
    throw withRuntimeDiagnostic(new Error("The FLUX.2 model cache worker did not take control of this page."), {
      stage: "artifact_cache",
      build: RUNTIME_BUILD,
      environment: browserEnvironmentSummary()
    });
  }

  const storage = navigator.storage;
  let persistent = false;
  if (storage?.persisted) persistent = await storage.persisted();
  if (!persistent && storage?.persist) persistent = await storage.persist();
  const estimate = storage?.estimate ? await storage.estimate() : {};
  const quota = Number(estimate?.quota);
  const usage = Number(estimate?.usage);
  onStatus(
    `Persistent model cache ready (${persistent ? "storage persistence granted" : "browser-managed persistence"}; ` +
    `${Number.isFinite(usage) ? humanBytes(usage) : "unknown usage"} of ${Number.isFinite(quota) ? humanBytes(quota) : "unknown quota"} used).`
  );
  return registration;
}

function waitForServiceWorkerController(expectedWorkerUrl) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      navigator.serviceWorker.removeEventListener("controllerchange", changed);
      reject(new Error("Timed out while activating the FLUX.2 model cache worker."));
    }, 10_000);
    function changed() {
      if (navigator.serviceWorker.controller?.scriptURL !== expectedWorkerUrl) return;
      clearTimeout(timeout);
      navigator.serviceWorker.removeEventListener("controllerchange", changed);
      resolve();
    }
    navigator.serviceWorker.addEventListener("controllerchange", changed);
  });
}

async function cacheModelArtifact(item, label) {
  const url = new URL(cacheableArtifactUrl(item));
  url.searchParams.set("mutualgpu_cache_only", "1");
  let response;
  try {
    response = await fetch(url, { credentials: "omit", cache: "no-store" });
  } catch (error) {
    throw withRuntimeDiagnostic(error, {
      stage: "artifact_cache",
      artifact: label,
      expectedBytes: item.byteLength,
      source: safeUrlHost(url),
      environment: browserEnvironmentSummary(),
      memory: memorySummary()
    });
  }
  if (!response.ok) {
    const detail = shortText(await response.text(), 180);
    throw withRuntimeDiagnostic(new Error(`The browser could not persist ${label} (${response.status}${detail ? `: ${detail}` : ""}).`), {
      stage: "artifact_cache",
      artifact: label,
      expectedBytes: item.byteLength,
      responseStatus: response.status,
      source: safeUrlHost(url),
      environment: browserEnvironmentSummary(),
      memory: memorySummary()
    });
  }
  return response.headers.get("x-mutualgpu-cache") === "hit" ? "hit" : "stored";
}

async function createSession(
  ort,
  graph,
  externalData = [],
  keepOutputsOnGpu = false,
  preferredLayout = "NCHW",
  { label = "ONNX graph", onStatus = () => {} } = {}
) {
  const started = performance.now();
  try {
    return await serializeSessionCreation(async () => {
      const preparedExternalData = await prepareExternalData(externalData, label, onStatus);
      onStatus(`Creating ${label} WebGPU session from ${humanBytes(graph.byteLength)} graph; ${preparedExternalData.length} cache-backed external data file${preparedExternalData.length === 1 ? "" : "s"}; ${memorySummary()}.`);
      const session = await ort.InferenceSession.create(graph, {
        executionProviders: [{
          name: "webgpu",
          preferredLayout,
          validationMode: "basic"
        }],
        externalData: preparedExternalData,
        graphOptimizationLevel: "all",
        ...(keepOutputsOnGpu ? { preferredOutputLocation: "gpu-buffer" } : {})
      });
      onStatus(`Created ${label} WebGPU session in ${formatDuration(performance.now() - started)}; ${memorySummary()}.`);
      return session;
    }, onStatus);
  } catch (error) {
    throw withRuntimeDiagnostic(error, {
      stage: "webgpu_session_create",
      component: label,
      graphBytes: graph.byteLength,
      externalDataFiles: externalData.length,
      elapsedMs: Math.round(performance.now() - started),
      environment: browserEnvironmentSummary(),
      memory: memorySummary()
    });
  }
}

async function serializeSessionCreation(operation, onStatus) {
  const predecessor = sessionCreationTail.catch(() => {});
  let release;
  sessionCreationTail = new Promise(resolve => { release = resolve; });
  const queued = sessionCreationsQueued > 0;
  sessionCreationsQueued += 1;
  if (queued) {
    onStatus("Waiting for the previous ONNX Runtime WebGPU session operation to finish.");
  }
  await predecessor;
  try {
    return await operation();
  } finally {
    sessionCreationsQueued -= 1;
    release();
  }
}

async function prepareExternalData(files, label, onStatus) {
  if (!files.length) return [];
  const totalBytes = files.reduce((total, file) => total + file.byteLength, 0);
  const loaded = new Array(files.length);
  let nextIndex = 0;
  let completed = 0;
  let failed = false;
  const parallelDownloads = Math.min(4, files.length);
  onStatus(`Caching ${files.length} ${label} weight files (${humanBytes(totalBytes)}) with ${parallelDownloads} bounded transfers.`);

  async function worker() {
    while (!failed && nextIndex < files.length) {
      const index = nextIndex++;
      const file = files[index];
      try {
        await cacheModelArtifact(file, `${label} weight ${index + 1} of ${files.length}`);
      } catch (error) {
        failed = true;
        throw error;
      }
      loaded[index] = { path: file.path, data: cacheableArtifactUrl(file) };
      completed += 1;
      if (completed === files.length || completed % 16 === 0) {
        onStatus(`Cached ${completed} of ${files.length} ${label} weight files.`);
      }
    }
  }

  const results = await Promise.allSettled(Array.from({ length: parallelDownloads }, () => worker()));
  const failure = results.find(result => result.status === "rejected");
  if (failure) throw failure.reason;
  return loaded;
}

function tokenizePrompt(tokenizer, prompt) {
  const chatPrompt =
    `<|im_start|>user\n${prompt}<|im_end|>\n` +
    "<|im_start|>assistant\n<think>\n\n</think>\n\n";
  const encoded = tokenizer(chatPrompt, {
    truncation: true,
    max_length: TOKEN_COUNT,
    add_special_tokens: false
  });
  const values = Array.from(encoded.input_ids?.data ?? encoded.input_ids ?? [], Number);
  const mask = Array.from(encoded.attention_mask?.data ?? encoded.attention_mask ?? [], Number);
  const inputIds = new BigInt64Array(TOKEN_COUNT);
  const attentionMask = new Uint8Array(TOKEN_COUNT);
  const padTokenId = Number(tokenizer.pad_token_id ?? 151643);
  for (let index = 0; index < TOKEN_COUNT; index += 1) {
    inputIds[index] = BigInt(Math.trunc(values[index] ?? padTokenId));
    attentionMask[index] = mask[index] ? 1 : 0;
  }
  const causalPaddingMask = new Uint8Array(TOKEN_COUNT * TOKEN_COUNT);
  for (let row = 0; row < TOKEN_COUNT; row += 1) {
    for (let column = 0; column <= row; column += 1) {
      causalPaddingMask[row * TOKEN_COUNT + column] = attentionMask[column];
    }
  }
  return { inputIds, causalPaddingMask };
}

function makeSeededLatents(seed, length) {
  const generator = new DeterministicNormalGenerator(BigInt(seed));
  const values = new Uint16Array(length);
  for (let index = 0; index < length; index += 1) {
    values[index] = numberToHalf(generator.normal());
  }
  return values;
}

function splitMix64(state) {
  let value = (state + 0x9e3779b97f4a7c15n) & MASK_64;
  value = ((value ^ (value >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK_64;
  value = ((value ^ (value >> 27n)) * 0x94d049bb133111ebn) & MASK_64;
  return (value ^ (value >> 31n)) & MASK_64;
}

class DeterministicNormalGenerator {
  constructor(seed) {
    this.state = seed & MASK_64;
    this.spare = undefined;
  }

  uniform() {
    this.state = splitMix64(this.state);
    return (Number(this.state & 0xffff_ffffn) + 0.5) * UINT32_SCALE;
  }

  normal() {
    if (this.spare !== undefined) {
      const value = this.spare;
      this.spare = undefined;
      return value;
    }
    const radius = Math.sqrt(-2 * Math.log(this.uniform()));
    const angle = 2 * Math.PI * this.uniform();
    this.spare = radius * Math.sin(angle);
    return radius * Math.cos(angle);
  }
}

function unpackForVae(packed, manifest, mean, standardDeviation) {
  const packedSide = manifest.resolution / 16;
  const vaeSide = manifest.resolution / 8;
  const output = new Float32Array(32 * vaeSide * vaeSide);
  for (let y = 0; y < packedSide; y += 1) {
    for (let x = 0; x < packedSide; x += 1) {
      const token = y * packedSide + x;
      for (let channel = 0; channel < 32; channel += 1) {
        for (let dy = 0; dy < 2; dy += 1) {
          for (let dx = 0; dx < 2; dx += 1) {
            const patchChannel = channel * 4 + dy * 2 + dx;
            const normalized = halfToNumber(packed[token * 128 + patchChannel]);
            const value = normalized * standardDeviation[patchChannel] + mean[patchChannel];
            const outputIndex =
              channel * vaeSide * vaeSide +
              (y * 2 + dy) * vaeSide +
              x * 2 +
              dx;
            output[outputIndex] = value;
          }
        }
      }
    }
  }
  return output;
}

async function encodePng(sample, width, height) {
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("The browser cannot create a 2D image canvas.");
  const image = context.createImageData(width, height);
  const plane = width * height;
  const read =
    sample instanceof Float32Array
      ? index => sample[index]
      : sample instanceof Uint16Array
        ? index => halfToNumber(sample[index])
        : typeof Float16Array !== "undefined" && sample instanceof Float16Array
          ? index => Number(sample[index])
          : null;
  if (!read) throw new Error("The VAE returned an unsupported tensor representation.");
  for (let index = 0; index < plane; index += 1) {
    image.data[index * 4] = pixelByte(read(index));
    image.data[index * 4 + 1] = pixelByte(read(plane + index));
    image.data[index * 4 + 2] = pixelByte(read(plane * 2 + index));
    image.data[index * 4 + 3] = 255;
  }
  context.putImageData(image, 0, 0);
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      blob => blob ? resolve(blob) : reject(new Error("The generated PNG could not be encoded.")),
      "image/png"
    );
  });
}

function pixelByte(value) {
  return Math.round(Math.min(1, Math.max(0, value / 2 + 0.5)) * 255);
}

function makeFloat16Tensor(ort, bits, shape) {
  return new ort.Tensor("float16", bits, shape);
}

function float16Bits(data) {
  if (data instanceof Uint16Array) return new Uint16Array(data);
  if (typeof Float16Array !== "undefined" && data instanceof Float16Array) {
    return new Uint16Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength));
  }
  throw new Error("The transformer returned an unexpected float16 tensor.");
}

function float32ToHalfBits(values) {
  const output = new Uint16Array(values.length);
  for (let index = 0; index < values.length; index += 1) {
    output[index] = numberToHalf(values[index]);
  }
  return output;
}

function halfToNumber(bits) {
  const sign = bits & 0x8000 ? -1 : 1;
  const exponent = (bits >>> 10) & 0x1f;
  const fraction = bits & 0x03ff;
  if (exponent === 0) return sign * 2 ** -14 * (fraction / 1024);
  if (exponent === 0x1f) return fraction ? Number.NaN : sign * Number.POSITIVE_INFINITY;
  return sign * 2 ** (exponent - 15) * (1 + fraction / 1024);
}

function numberToHalf(value) {
  const float = new Float32Array(1);
  const integer = new Uint32Array(float.buffer);
  float[0] = value;
  const bits = integer[0];
  const sign = (bits >>> 16) & 0x8000;
  let exponent = ((bits >>> 23) & 0xff) - 127 + 15;
  let fraction = bits & 0x7fffff;
  if (exponent <= 0) {
    if (exponent < -10) return sign;
    fraction = (fraction | 0x800000) >>> (1 - exponent);
    return sign | ((fraction + 0x1000) >>> 13);
  }
  if (exponent >= 31) return sign | 0x7c00;
  fraction += 0x1000;
  if (fraction & 0x800000) {
    fraction = 0;
    exponent += 1;
    if (exponent >= 31) return sign | 0x7c00;
  }
  return sign | (exponent << 10) | (fraction >>> 13);
}

function withRuntimeDiagnostic(error, update) {
  const wrapped = error instanceof Error ? error : new Error(String(error || "Unknown FLUX.2 runtime failure."));
  const existing = wrapped.flux2Diagnostic || {};
  wrapped.flux2Diagnostic = {
    ...update,
    ...existing,
    stage: existing.stage || update.stage || "unknown",
    errorName: existing.errorName || wrapped.name || "Error",
    errorMessage: existing.errorMessage || shortText(wrapped.message || "runtime error")
  };
  return wrapped;
}

function browserEnvironmentSummary() {
  return [
    `origin=${location.origin}`,
    `secure=${Boolean(globalThis.isSecureContext)}`,
    `isolated=${Boolean(globalThis.crossOriginIsolated)}`,
    `deviceMemory=${navigator.deviceMemory ?? "unknown"}GiB`
  ].join(", ");
}

function memorySummary() {
  const memory = performance.memory;
  if (!memory) return "JS heap telemetry unavailable";
  return `JS heap used ${humanBytes(memory.usedJSHeapSize)} of ${humanBytes(memory.jsHeapSizeLimit)} (total ${humanBytes(memory.totalJSHeapSize)})`;
}

function safeUrlHost(value) {
  try { return new URL(value, location.href).host || "same origin"; }
  catch { return "unavailable"; }
}

function formatDuration(milliseconds) {
  return `${(milliseconds / 1000).toFixed(milliseconds >= 10_000 ? 1 : 2)}s`;
}

function shortText(value, maximum = 280) {
  const text = String(value || "").replace(/\s+/g, " ").trim();
  return text.length > maximum ? `${text.slice(0, maximum - 1)}…` : text;
}

function product(values) {
  return values.reduce((total, value) => total * value, 1);
}

function humanBytes(value) {
  if (value >= 2 ** 30) return `${(value / 2 ** 30).toFixed(2)} GiB`;
  if (value >= 2 ** 20) return `${(value / 2 ** 20).toFixed(1)} MiB`;
  return `${Math.ceil(value / 2 ** 10)} KiB`;
}

function throwIfAborted(aborted) {
  if (aborted) throw new DOMException("FLUX.2 generation was cancelled.", "AbortError");
}

export {
  tokenizePrompt as tokenizeFlux2Prompt,
  makeSeededLatents as makeFlux2SeededLatents,
  halfToNumber as flux2HalfToNumber
};
