import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
  flux2HalfToNumber,
  makeFlux2SeededLatents,
  tokenizeFlux2Prompt
} from "../../src/MutualGPU.Api/wwwroot/js/flux2-webgpu-runtime.js";
import { StreamingSha256 } from "../../src/MutualGPU.Api/wwwroot/js/streaming-sha256.js";

const root = new URL("../../src/MutualGPU.Api/wwwroot/", import.meta.url);

test("browser host advertises only the validated FP16 Klein text-to-image product", async () => {
  const [setup, host] = await Promise.all([
    readFile(new URL("offer-compute.html", root), "utf8"),
    readFile(new URL("js/host-compute.js", root), "utf8")
  ]);

  assert.match(setup, /flux2-klein-4b-text-to-image/);
  assert.doesNotMatch(setup, /flux2-klein-4b-image-to-image/);
  assert.match(setup, /full, unquantized FP16 model/);
  assert.match(setup, /WebGPU · 16 GiB/);
  assert.doesNotMatch(setup, /value="8"/);

  assert.match(host, /steps = integer\(.+4, 4/);
  assert.doesNotMatch(host, /"flux2-klein-4b-image-to-image":/);
  assert.match(host, /width = integer\(.+1024, 1024/);
  assert.match(host, /height = integer\(.+1024, 1024/);
});

test("browser runtime pins and validates the replacement Hugging Face model", async () => {
  const runtime = await readFile(new URL("js/flux2-webgpu-runtime.js", root), "utf8");

  assert.match(runtime, /black-forest-labs\/FLUX\.2-klein-4B/);
  assert.match(runtime, /e7b7dc27f91deacad38e78976d1f2b499d76a294/);
  assert.match(runtime, /KatzenStuff\/flux-2-klein-4b-webgpu/);
  assert.match(runtime, /2cd85d938ff8afb954661262c12c6d10676a20e1/);
  assert.match(runtime, /resolve\/\$\{ARTIFACT_REVISION\}\/models\/klein-4b/);
  assert.match(runtime, /onnxruntime-web@1\.27\.0/);
  assert.match(runtime, /RUNTIME_BUILD = "20260726-klein-fp16-adapter-4"/);
  assert.match(runtime, /ort\.webgpu\.bundle\.min\.mjs/);
  assert.doesNotMatch(runtime, /ort\.env\.wasm\.wasmPaths/);
  assert.match(runtime, /powerPreference: "high-performance"/);
  assert.match(runtime, /adapter\.features\.has\("shader-f16"\)/);
  assert.match(runtime, /ort\.env\.webgpu\.adapter = adapter/);
  assert.match(runtime, /\["default", null, undefined\]/);
  assert.match(runtime, /\["low-power", null, \{ powerPreference: "low-power" \}\]/);
  assert.match(runtime, /No Chrome WebGPU adapter exposed shader-f16/);
  assert.match(runtime, /transformers\.env\.useBrowserCache = true/);
  assert.match(runtime, /transformers\.env\.cacheKey = `\$\{MODEL_CACHE_NAME\}-tokenizer`/);
  assert.match(runtime, /pipeline-1024\/manifest\.json/);
  assert.match(runtime, /transformerManifest\.parts\?\.length !== 4/);
  assert.match(runtime, /vaeManifest\.dtype !== "float16"/);
  assert.match(runtime, /DeterministicNormalGenerator/);

  assert.doesNotMatch(runtime, /ryanhlewis/);
  assert.doesNotMatch(runtime, /trie-me\/flux2-klein-4b-webgpu/);
  assert.doesNotMatch(runtime, /custom-lowbit-webgpu/);
  assert.doesNotMatch(runtime, /enableFusedTransformer/);
});

test("browser runtime selects an FP16-capable adapter before passing it to ONNX Runtime", async () => {
  const host = await readFile(new URL("js/host-compute.js", root), "utf8");

  assert.match(host, /high-performance, default, and low-power adapter requests/);
  assert.doesNotMatch(host, /chrome:\/\/gpu/);
  assert.match(host, /loadFlux2WebGpuRuntime\(\{ onStatus: log \}\)/);
  assert.doesNotMatch(host, /requestAdapter\(/);
  assert.doesNotMatch(host, /requestDevice\(/);
  assert.match(host, /klein-fp16-adapter-4/);
});

test("browser provider records the precise FLUX.2 download and WebGPU-session failure stage", async () => {
  const [runtime, host, cacheWorker] = await Promise.all([
    readFile(new URL("js/flux2-webgpu-runtime.js", root), "utf8"),
    readFile(new URL("js/host-compute.js", root), "utf8"),
    readFile(new URL("model-cache-worker.js", root), "utf8")
  ]);

  assert.match(runtime, /Received \$\{label\} response: HTTP \$\{response\.status\}/);
  assert.match(runtime, /Verified \$\{label\}:/);
  assert.match(runtime, /stage: "artifact_download"/);
  assert.match(runtime, /stage: "webgpu_session_create"/);
  assert.match(runtime, /serializeSessionCreation/);
  assert.match(runtime, /prepareExternalData/);
  assert.match(runtime, /Math\.min\(4, files\.length\)/);
  assert.match(runtime, /data: cacheableArtifactUrl\(file\)/);
  assert.doesNotMatch(runtime, /data: new Uint8Array\(data\)/);
  assert.match(runtime, /navigator\.serviceWorker\.register/);
  assert.match(runtime, /mutualgpu_cache_only/);
  assert.match(runtime, /stage: "artifact_cache"/);
  assert.match(cacheWorker, /2cd85d938ff8afb954661262c12c6d10676a20e1/);
  assert.match(cacheWorker, /caches\.open\(MODEL_CACHE_NAME\)/);
  assert.match(cacheWorker, /await cache\.put\(cacheKey, verified\)/);
  assert.match(cacheWorker, /responseLength\(cached\) === expectedBytes/);
  assert.match(cacheWorker, /StreamingSha256/);
  assert.match(cacheWorker, /x-mutualgpu-sha256/);
  assert.match(cacheWorker, /self\.clients\.claim\(\)/);
  assert.match(runtime, /WebGPU device lost:/);
  assert.match(host, /describeAssignmentFailure/);
  assert.match(host, /stage === "artifact_download" \? "model_download"/);
  assert.match(host, /stage === "artifact_cache" \? "model_cache"/);
  assert.match(host, /stage === "webgpu_session_create" \? "webgpu_session_create"/);
  assert.match(host, /FLUX\.2 provider diagnostic:/);
  assert.match(host, /Assignment needs attention/);
  assert.match(host, /publicAssignmentFailure/);
  assert.match(host, /Provider disconnected after the runtime failure/);
});

test("streaming SHA-256 verifies artifacts across arbitrary chunk boundaries", () => {
  const encoder = new TextEncoder();
  const oneChunk = new StreamingSha256()
    .update(encoder.encode("abc"))
    .digestHex();
  const split = new StreamingSha256()
    .update(encoder.encode("a"))
    .update(encoder.encode("b"))
    .update(encoder.encode("c"))
    .digestHex();

  assert.equal(oneChunk, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  assert.equal(split, oneChunk);
  assert.equal(
    new StreamingSha256().digestHex(),
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  );
});

test("Klein prompt preparation uses the no-thinking Qwen template and a causal padding mask", () => {
  let renderedPrompt;
  let tokenizationOptions;
  const tokenizer = Object.assign((text, options) => {
    renderedPrompt = text;
    tokenizationOptions = options;
    return {
      input_ids: { data: Int32Array.of(151644, 872, 151645) },
      attention_mask: { data: Int32Array.of(1, 1, 1) }
    };
  }, { pad_token_id: 151643 });

  const prepared = tokenizeFlux2Prompt(tokenizer, "a lighthouse in fog");

  assert.equal(
    renderedPrompt,
    "<|im_start|>user\na lighthouse in fog<|im_end|>\n" +
      "<|im_start|>assistant\n<think>\n\n</think>\n\n"
  );
  assert.deepEqual(tokenizationOptions, {
    truncation: true,
    max_length: 512,
    add_special_tokens: false
  });
  assert.deepEqual([...prepared.inputIds.slice(0, 5)], [151644n, 872n, 151645n, 151643n, 151643n]);
  assert.equal(prepared.causalPaddingMask[0], 1);
  assert.equal(prepared.causalPaddingMask[512], 1);
  assert.equal(prepared.causalPaddingMask[513], 1);
  assert.equal(prepared.causalPaddingMask[3 * 512 + 3], 0);
});

test("browser latent generation is deterministic for a MutualGPU seed", () => {
  const first = makeFlux2SeededLatents(42, 6);
  const second = makeFlux2SeededLatents(42, 6);

  assert.deepEqual(first, second);
  assert.deepEqual(
    [...first].map(flux2HalfToNumber),
    [-1.4453125, 1.1240234375, 0.2340087890625, -0.53271484375, 0.505859375, -0.58203125]
  );
});
