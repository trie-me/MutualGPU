import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { Flux2Runtime } from "../src/inference-runtime.mjs";

test("runtime keeps a subprocess resident, receives progress, and collects bounded artifacts", async () => {
  process.env.MUTUALGPU_PROVIDER_KEY = "must-not-reach-python";
  const runtime = new Flux2Runtime({
    python: "python3",
    scriptPath: fileURLToPath(new URL("./fake-inference.py", import.meta.url)),
    model: "fake-model",
    device: "auto",
    localFilesOnly: true,
    allowCpu: false,
    generationTimeoutMs: 5_000,
    startupTimeoutMs: 5_000
  });
  try {
    const info = await runtime.start();
    assert.deepEqual(info, { device: "fake-gpu", model: "fake-model" });
    const progress = [];
    const result = await runtime.generate({ prompt: "cat" }, { onProgress: update => progress.push(update) });
    assert.equal(result.resultZip.subarray(0, 2).toString(), "PK");
    assert.deepEqual([...result.preview.subarray(0, 4)], [0x89, 0x50, 0x4e, 0x47]);
    assert.equal(result.metadata.durationMs, 1);
    assert.equal(progress[0].percent, 50);
  } finally {
    runtime.close();
    delete process.env.MUTUALGPU_PROVIDER_KEY;
  }
});

test("runtime preserves a safety rejection without terminating its subprocess", async () => {
  const runtime = new Flux2Runtime({
    python: "python3",
    scriptPath: fileURLToPath(new URL("./fake-inference.py", import.meta.url)),
    model: "fake-model",
    device: "auto",
    localFilesOnly: true,
    allowCpu: false,
    generationTimeoutMs: 5_000,
    startupTimeoutMs: 5_000
  });
  try {
    await assert.rejects(
      runtime.generate({ prompt: "blocked" }),
      error => error.name === "SafetyCheckRejected" && /inappropriate content/i.test(error.message)
    );

    const result = await runtime.generate({ prompt: "cat" });

    assert.equal(result.metadata.durationMs, 1);
  } finally {
    runtime.close();
  }
});
