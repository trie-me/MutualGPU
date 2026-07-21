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
  assert.equal(calls[0].init.cache, "no-store");
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

test("requestor client maps the requestor endpoint error matrix", async () => {
  const cases = [
    {
      name: "missing task",
      action: client => client.getTask("missing"),
      response: new Response(null, { status: 404 }),
      status: 404
    },
    {
      name: "unavailable capability",
      action: client => client.submitTask({}),
      response: json({ code: "capability_unavailable" }, { status: 409 }),
      status: 409,
      code: "capability_unavailable"
    },
    {
      name: "stale contract",
      action: client => client.submitTask({}),
      response: json({ code: "capability_contract_changed" }, { status: 409 }),
      status: 409,
      code: "capability_contract_changed"
    },
    {
      name: "validation problem",
      action: client => client.submitTask({}),
      response: json({ title: "One or more validation errors occurred.", errors: { seed: ["Invalid."] } }, {
        status: 400,
        headers: { "Content-Type": "application/problem+json" }
      }),
      status: 400
    },
    {
      name: "reevaluation conflict",
      action: client => client.reevaluateTask("task"),
      response: json({ code: "task_not_running" }, { status: 409 }),
      status: 409,
      code: "task_not_running"
    },
    {
      name: "result unavailable",
      action: client => client.getTaskResult("task"),
      response: json({ code: "result_not_available" }, { status: 409 }),
      status: 409,
      code: "result_not_available"
    },
    {
      name: "malformed JSON",
      action: client => client.createWebGpuEnrollment(),
      response: new Response("{not-json", { status: 502, headers: { "Content-Type": "application/json" } }),
      status: 502,
      body: "{not-json"
    }
  ];

  for (const scenario of cases) {
    let calls = 0;
    const client = new RequestorClient("https://mutualgpu.example", {
      fetchImpl: async () => ++calls === 1 ? new Response("MutualGPU") : scenario.response
    });
    await assert.rejects(scenario.action(client), error => {
      assert.ok(error instanceof RequestorApiError, scenario.name);
      assert.equal(error.status, scenario.status, scenario.name);
      assert.equal(error.code, scenario.code, scenario.name);
      if (scenario.body) assert.equal(error.body, scenario.body, scenario.name);
      return true;
    });
    assert.equal(calls, 2, scenario.name);
  }
});

test("requestor client can retry a failed root bootstrap", async () => {
  const calls = [];
  const client = new RequestorClient("https://mutualgpu.example", {
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      if (calls.length === 1) return json({ code: "startup_unavailable" }, { status: 503 });
      if (url.pathname === "/") return new Response("MutualGPU");
      return json([]);
    }
  });

  await assert.rejects(client.listCapabilities(), error =>
    error instanceof RequestorApiError && error.status === 503 && error.code === "startup_unavailable");
  assert.deepEqual(await client.listCapabilities(), []);
  assert.deepEqual(calls.map(call => call.url.pathname), ["/", "/", "/api/capabilities/"]);
  assert.equal(calls[0].init.cache, "no-store");
  assert.equal(calls[1].init.cache, "no-store");
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
