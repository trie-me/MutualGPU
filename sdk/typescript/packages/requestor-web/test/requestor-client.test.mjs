import assert from "node:assert/strict";
import test from "node:test";
import { RequestorApiError, RequestorClient } from "../src/requestor-client.js";

const json = (value, init = {}) => new Response(JSON.stringify(value), {
  ...init,
  headers: { "Content-Type": "application/json", ...init.headers }
});

test("requestor client covers every finite requestor endpoint with credentials", async () => {
  const calls = [];
  const responses = [
    new Response("<!doctype html><title>MutualGPU</title>", { headers: { "Content-Type": "text/html" } }),
    json([{ capabilityId: "capability" }]),
    json([{ taskId: "task" }]),
    json({ taskId: "task" }),
    json({ taskId: "created-json" }, { status: 201, headers: { Location: "/api/tasks/created-json" } }),
    json({ taskId: "created-image" }, { status: 201, headers: { Location: "/api/tasks/created-image" } }),
    new Response(null, { status: 202, headers: { Location: "/api/tasks/task" } }),
    json({ taskId: "task", artifacts: [] }),
    json({ executionUnitId: "unit", providerKey: "secret" })
  ];
  const client = new RequestorClient("https://mutualgpu.example/base", {
    fetchImpl: async function (url, init) {
      calls.push({ receiver: this, url, init });
      return responses.shift();
    }
  });
  const submission = {
    capabilityId: "capability",
    contractHash: "hash",
    scalars: { seed: "42" },
    resources: { computeTier: "Large", memoryGiB: 32 },
    idempotencyKey: "idempotency"
  };

  assert.deepEqual(await client.listCapabilities(), [{ capabilityId: "capability" }]);
  assert.deepEqual(await client.listTasks(), [{ taskId: "task" }]);
  assert.equal((await client.getTask("task")).taskId, "task");
  assert.equal((await client.submitTask(submission)).taskId, "created-json");
  assert.equal((await client.submitTask(submission, new Blob(["image"], { type: "image/png" }))).taskId, "created-image");
  assert.deepEqual(await client.reevaluateTask("task"), { location: "/api/tasks/task", task: null });
  assert.deepEqual(await client.getTaskResult("task"), { taskId: "task", artifacts: [] });
  assert.deepEqual(await client.createWebGpuEnrollment(), { executionUnitId: "unit", providerKey: "secret" });

  assert.deepEqual(calls.map(call => [call.init.method ?? "GET", call.url.pathname]), [
    ["GET", "/"],
    ["GET", "/api/capabilities/"],
    ["GET", "/api/tasks/"],
    ["GET", "/api/tasks/task"],
    ["POST", "/api/tasks/"],
    ["POST", "/api/tasks/"],
    ["POST", "/api/tasks/task/reevaluate"],
    ["GET", "/api/tasks/task/result"],
    ["POST", "/api/webgpu-enrollments"]
  ]);
  assert.ok(calls.every(call => call.init.credentials === "include"));
  assert.ok(calls.every(call => call.receiver === globalThis));
  assert.deepEqual(JSON.parse(calls[4].init.body), submission);
  assert.equal(calls[4].init.headers["Content-Type"], "application/json");
  assert.equal(calls[5].init.headers, undefined);
  assert.deepEqual(JSON.parse(calls[5].init.body.get("submission")), submission);
  assert.equal(await calls[5].init.body.get("image").text(), "image");
});

test("requestor client retries once after the API issues a missing identity cookie", async () => {
  const calls = [];
  const client = new RequestorClient("https://mutualgpu.example", {
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      if (calls.length === 1) return new Response("MutualGPU");
      return calls.length === 2
        ? json({ code: "requestor_identity_missing" }, { status: 400 })
        : json([]);
    }
  });

  assert.deepEqual(await client.listTasks(), []);
  assert.equal(calls.length, 3);
  assert.ok(calls.every(call => call.init.credentials === "include"));
});

test("requestor client preserves problem details after the identity retry budget", async () => {
  let calls = 0;
  const client = new RequestorClient("https://mutualgpu.example", {
    fetchImpl: async () => ++calls === 1
      ? new Response("MutualGPU")
      : json({ code: "requestor_identity_missing", detail: "Cookie was not sent." }, { status: 400 })
  });

  await assert.rejects(client.listTasks(), error =>
    error instanceof RequestorApiError &&
    error.status === 400 &&
    error.code === "requestor_identity_missing" &&
    error.message === "Cookie was not sent.");
  assert.equal(calls, 3);
});

test("requestor client shares one root bootstrap across concurrent endpoint calls", async () => {
  const calls = [];
  let releaseBootstrap;
  const bootstrap = new Promise(resolve => { releaseBootstrap = resolve; });
  const client = new RequestorClient("https://mutualgpu.example", {
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      if (url.pathname === "/") {
        await bootstrap;
        return new Response("MutualGPU");
      }
      return json([]);
    }
  });

  const capabilities = client.listCapabilities();
  const tasks = client.listTasks();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls.map(call => call.url.pathname), ["/"]);
  releaseBootstrap();
  assert.deepEqual(await Promise.all([capabilities, tasks]), [[], []]);
  assert.deepEqual(calls.map(call => call.url.pathname), ["/", "/api/capabilities/", "/api/tasks/"]);
});

test("requestor client rejects cleartext endpoints and invalid task inputs", async () => {
  assert.throws(() => new RequestorClient("http://mutualgpu.example"), /require an https API URL/);
  const client = new RequestorClient("https://mutualgpu.example", { fetchImpl: async () => assert.fail("fetch must not run") });
  await assert.rejects(client.getTask(""), /task ID is required/);
  await assert.rejects(client.submitTask(null), /task submission is required/);
  await assert.rejects(client.submitTask({}, "not-an-image"), /Blob or File/);
});
