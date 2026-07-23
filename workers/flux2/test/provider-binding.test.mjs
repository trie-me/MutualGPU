import assert from "node:assert/strict";
import test from "node:test";
import { buildEnrollment, loadConfig, parseGenerationRequest } from "../src/config.mjs";
import { createTaskHandler } from "../src/provider-binding.mjs";

test("configuration requires an HTTPS API and preserves the SDK machine envelope", () => {
  const config = loadConfig({
    MUTUALGPU_API_URL: "https://mutualgpu.example",
    MUTUALGPU_PROVIDER_KEY: "not-logged",
    MUTUALGPU_FLUX2_MEMORY_GIB: "24"
  });
  const definition = buildEnrollment(config);
  assert.equal(config.capabilityName, "flux2-klein-4b");
  assert.equal(config.model, "black-forest-labs/FLUX.2-klein-4B");
  assert.deepEqual(definition.machine.specifications, { computeTier: "Large", memoryGiB: 24 });
  assert.equal(definition.capabilities[0].output.hasPreview, true);
  assert.equal(definition.capabilities[0].inputs.find(input => input.key === "guidance_scale").default, "7");
  assert.deepEqual(definition.capabilities[0].output.previewContentTypes, ["image/png"]);
});

test("generation scalars are parsed, bounded, and assigned a seed", () => {
  const request = parseGenerationRequest({
    prompt: "  a small tabby cat  ", width: "768", height: "512", guidance_scale: "6.5"
  }, () => 42);
  assert.deepEqual(request, {
    prompt: "a small tabby cat", steps: 4, guidanceScale: 6.5,
    width: 768, height: 512, seed: 42
  });
  assert.throws(() => parseGenerationRequest({ prompt: "cat", width: "513" }), /divisible by 8/);
});

test("binding rejects malformed work before accepting GPU execution", async () => {
  const events = [];
  const handler = createTaskHandler({ runtime: { generate: async () => assert.fail("must not run") } });
  await handler(fakeTask({ prompt: "" }, events));
  assert.deepEqual(events, [["reject", "Invalid FLUX.2 inputs: prompt is required."]]);
});

test("binding accepts, publishes, and completes a generated image through the SDK facade", async () => {
  const events = [];
  const runtime = {
    generate: async (request, { onProgress }) => {
      assert.equal(request.seed, 7);
      await onProgress({ phase: "inference", percent: 50, message: "step" });
      return {
        resultZip: Uint8Array.of(0x50, 0x4b),
        preview: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
        thumbnail: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
        metadata: { seed: 7 }, logs: "done\n"
      };
    }
  };
  const handler = createTaskHandler({ runtime, chooseSeed: () => 7, logger: silentLogger });
  await handler(fakeTask({ prompt: "cat" }, events));
  assert.equal(events[0][0], "accept");
  assert.equal(events.filter(([name]) => name === "progress").length, 4);
  assert.deepEqual(events.filter(([name]) => name === "progress").map(([, update]) => update.percent), [1, 50, 90, 95]);
  assert.equal(events.find(([name]) => name === "upload")[1].metadata.seed, 7);
  assert.deepEqual(events.at(-1), ["complete", "receipt-1"]);
});

test("binding retries the newest progress update when the SDK rate limit coalesces it", async () => {
  const events = [];
  let progressCalls = 0;
  const task = fakeTask({ prompt: "cat" }, events);
  task.reportProgress = async update => {
    progressCalls += 1;
    events.push(["progress", update]);
    return progressCalls === 2 ? false : true;
  };
  const runtime = {
    generate: async (_request, { onProgress }) => {
      await onProgress({ phase: "inference", percent: 50, message: "step" });
      await new Promise(resolve => setTimeout(resolve, 10));
      return generatedResult();
    }
  };
  const handler = createTaskHandler({ runtime, logger: silentLogger, progressRetryMs: 1 });
  await handler(task);
  assert.ok(events.filter(([name, update]) => name === "progress" && update.percent === 50).length >= 2);
  assert.deepEqual(events.at(-1), ["complete", "receipt-1"]);
});

test("binding reports inference failures with a stable safe diagnostic", async () => {
  const events = [];
  const handler = createTaskHandler({
    runtime: { generate: async () => { throw new Error("requester prompt and local path"); } },
    logger: silentLogger
  });
  await handler(fakeTask({ prompt: "cat" }, events));
  assert.deepEqual(events.at(-1), ["fail", "inference", "FLUX.2 inference failed on the provider."]);
});

test("binding reports safety rejection as inappropriate content after draining progress", async () => {
  const events = [];
  let releaseProgress;
  const task = fakeTask({ prompt: "cat" }, events);
  task.reportProgress = async update => {
    events.push(["progress", update]);
    if (update.percent !== 50) return true;
    await new Promise(resolve => { releaseProgress = resolve; });
    return true;
  };
  const safetyError = new Error("local safety details");
  safetyError.name = "SafetyCheckRejected";
  const handler = createTaskHandler({
    runtime: {
      generate: async (_request, { onProgress }) => {
        void onProgress({ phase: "inference", percent: 50, message: "step" });
        await new Promise(resolve => setImmediate(resolve));
        throw safetyError;
      }
    },
    logger: silentLogger
  });

  const handling = handler(task);
  while (!releaseProgress) await new Promise(resolve => setImmediate(resolve));
  assert.equal(events.some(([name]) => name === "fail"), false);
  releaseProgress();
  await handling;

  assert.deepEqual(events.at(-1), [
    "fail", "content_safety", "The generated image was blocked as inappropriate content."
  ]);
});

function fakeTask(scalars, events) {
  return {
    taskId: "task-1", scalars,
    accept: async () => events.push(["accept"]),
    reject: async reason => events.push(["reject", reason]),
    reportProgress: async update => events.push(["progress", update]),
    uploadResult: async result => { events.push(["upload", result]); return { receipt: "receipt-1", ignoredParts: [] }; },
    complete: async receipt => events.push(["complete", receipt]),
    fail: async (step, reason) => events.push(["fail", step, reason])
  };
}

function generatedResult() {
  return {
    resultZip: Uint8Array.of(0x50, 0x4b),
    preview: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
    thumbnail: Uint8Array.of(0x89, 0x50, 0x4e, 0x47),
    metadata: { seed: 7 }, logs: "done\n"
  };
}

const silentLogger = { info() {}, warn() {}, error() {} };
