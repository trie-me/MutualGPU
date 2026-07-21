import test from "node:test";
import assert from "node:assert/strict";
import { ProviderClient, ProviderClientError } from "../src/provider.js";
import { uploadProviderResult } from "../src/result-upload.js";
import { MutualGpuProtocol } from "../src/protocol.js";

test("provider enrollment hides server-owned capability identity fields", async () => {
  let enrolled;
  const provider = new ProviderClient({
    enroll: async definition => { enrolled = definition; },
    close() {}
  });

  await provider.enroll({
    machine: { tier: "Large", specifications: { computeTier: "Large", memoryGiB: 32 } },
    capabilities: [{ name: "render", inputs: [], output: { hasMetadata: true } }]
  });

  assert.deepEqual(enrolled.capabilities[0], {
    name: "render",
    inputs: [],
    output: { hasMetadata: true },
    id: { value: "00000000-0000-0000-0000-000000000000" },
    contractHash: "server-computed"
  });
  assert.deepEqual(enrolled.machine, { tier: "Large", specifications: { computeTier: "Large", memoryGiB: 32 } });
});

test("provider enrollment keeps scheduling tier separate from hardware specifications", async () => {
  const provider = new ProviderClient({ enroll: async () => {}, close() {} });
  const capability = { name: "render", inputs: [], output: {} };

  await assert.rejects(
    provider.enroll({ machine: { tier: "Automatic", specifications: { computeTier: "Large", memoryGiB: 32 } }, capabilities: [capability] }),
    /concrete T-shirt tier/);
  await assert.rejects(
    provider.enroll({ machine: { tier: "Large", specifications: { computeTier: "Large", memoryGiB: 0 } }, capabilities: [capability] }),
    /positive integer memoryGiB/);
});

test("canonical protobuf bytes round-trip with the .NET provider contracts", () => {
  const connect = MutualGpuProtocol.encodeProvider({ connect: { protocolVersion: 1, authorization: "key" } });
  const resultUpload = MutualGpuProtocol.encodeProvider({
    resultUpload: { taskId: "task", attemptId: "attempt", taskHandle: "handle" }
  });
  const assignment = MutualGpuProtocol.encodeServer({
    assignment: {
      taskId: "task", attemptId: "attempt", taskHandle: "handle", scalars: { seed: "42" },
      input: { url: "https://input.example/a", contentType: "image/png", length: 42, sha256: "digest" }
    }
  });

  assert.equal(Buffer.from(connect).toString("base64"), "CgcIARoDa2V5");
  assert.equal(Buffer.from(resultUpload).toString("base64"), "MhcKBmhhbmRsZRIEdGFzaxoHYXR0ZW1wdA==");
  assert.equal(Buffer.from(assignment).toString("base64"), "ElMKBHRhc2sSB2F0dGVtcHQaBmhhbmRsZSIKCgRzZWVkEgI0MiouChdodHRwczovL2lucHV0LmV4YW1wbGUvYRIJaW1hZ2UvcG5nGCoiBmRpZ2VzdA==");
  assert.deepEqual(MutualGpuProtocol.decodeProvider(connect), { connect: { protocolVersion: 1, activeTaskHandle: "", authorization: "key" } });
  assert.deepEqual(MutualGpuProtocol.decodeProvider(resultUpload), {
    resultUpload: { taskHandle: "handle", taskId: "task", attemptId: "attempt" }
  });
  assert.deepEqual(MutualGpuProtocol.decodeServer(assignment), {
    assignment: {
      taskId: "task", attemptId: "attempt", taskHandle: "handle", scalars: { seed: "42" },
      input: { url: "https://input.example/a", contentType: "image/png", length: 42, sha256: "digest" }
    }
  });
});

test("provider explicitly accepts one assignment and coalesces progress", async () => {
  const calls = [];
  let receive;
  const transport = {
    connect: async callback => { receive = callback; },
    accept: async assignment => calls.push(["accept", assignment.taskHandle]),
    progress: async (_, update) => calls.push(["progress", update.sequenceNumber]),
    fail: async () => calls.push(["fail"]), reject: async () => calls.push(["reject"]),
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => calls.push(["complete"])
  };
  const client = new ProviderClient(transport);
  await client.connect(async task => {
    await task.accept();
    await task.reportProgress({ percent: 1 });
    await task.reportProgress({ percent: 2 });
    await task.complete("receipt");
  });
  await receive({ taskHandle: "opaque" });
  assert.deepEqual(calls, [["accept", "opaque"], ["progress", 1], ["complete"]]);
});

test("provider can reject before acceptance and rejects a handler that never acknowledges", async () => {
  const calls = [];
  let receive;
  const transport = {
    connect: async callback => { receive = callback; },
    accept: async () => calls.push(["accept"]),
    reject: async (_, reason) => calls.push(["reject", reason]),
    progress: async () => {}, fail: async () => {}, refreshInputDownload: async () => "url",
    requestResultUpload: async () => "token", complete: async () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async task => { await task.reject("busy"); });
  await receive({ taskHandle: "first" });

  await client.connect(async () => {});
  await receive({ taskHandle: "second" });

  assert.deepEqual(calls, [["reject", "busy"], ["reject", "handler returned without accepting the assignment"]]);
});

test("provider reconnects an accepted assignment with its active task handle", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  const handles = [];
  let receive;
  let resume;
  const transport = {
    connect: async (callback, activeTaskHandle) => { receive = callback; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}, close: () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async assigned => {
    await assigned.accept();
    await new Promise(resolve => { resume = resolve; });
    await assigned.complete("receipt");
  });

  const handling = receive(task);
  await new Promise(resolve => setImmediate(resolve));
  await client.reconnect();
  assert.deepEqual(handles, ["", "handle"]);

  resume();
  await handling;
  client.close();
});

test("provider automatically reconnects a dropped session with its active task handle", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  const handles = [];
  let receive;
  let disconnect;
  let resume;
  const transport = {
    connect: async (callback, activeTaskHandle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}, close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0 });
  await client.connect(async assigned => {
    await assigned.accept();
    await new Promise(resolve => { resume = resolve; });
    await assigned.complete("receipt");
  });

  const handling = receive(task);
  await new Promise(resolve => setImmediate(resolve));
  disconnect(new Error("network lost"));
  for (let turn = 0; handles.length < 2 && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(handles, ["", "handle"]);

  resume();
  await handling;
  client.close();
});

test("task operations reject stale and unaccepted calls", async () => {
  let receive;
  const transport = {
    connect: async callback => { receive = callback; }, accept: async () => {}, reject: async () => {},
    progress: async () => {}, fail: async () => {}, refreshInputDownload: async () => "url",
    requestResultUpload: async () => "token", complete: async () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async task => {
    await assert.rejects(task.reportProgress({ percent: 1 }), error => error instanceof ProviderClientError && error.code === "invalid_task_state");
    await task.accept();
    await task.complete("receipt");
    await assert.rejects(task.refreshInputDownload(), error => error instanceof ProviderClientError && error.code === "invalid_task_state");
  });
  await receive({ taskHandle: "opaque" });
});

test("provider rejects an assignment that contains a cleartext input URL", async () => {
  let receive;
  const calls = [];
  const transport = {
    connect: async callback => { receive = callback; }, accept: async () => {},
    reject: async (_, reason) => calls.push(reason), progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async () => assert.fail("The handler must not receive an insecure descriptor."));
  await receive({ taskHandle: "opaque", input: { url: "http://objects.example/input" } });
  assert.deepEqual(calls, ["the assignment contains an invalid input URL"]);
});

test("shared result uploader sends scoped multipart parts and returns a receipt", async () => {
  let seen;
  const uploaded = await uploadProviderResult({
    apiBaseUrl: "https://mutualgpu.example/",
    presharedKey: "provider-key",
    task: { taskId: "task", attemptId: "attempt", taskHandle: "handle" },
    token: "single-use-token",
    result: {
      resultZip: new Uint8Array([0x50, 0x4b, 3, 4]),
      metadata: { frames: 12 },
      logs: "handler log"
    },
    fetchImpl: async (url, init) => {
      seen = { url, init };
      return { ok: true, status: 200, text: async () => JSON.stringify({ receipt: "receipt-1" }) };
    }
  });

  assert.equal(uploaded.receipt, "receipt-1");
  assert.match(uploaded.sha256, /^[0-9a-f]{64}$/);
  assert.equal(seen.url.pathname, "/provider/tasks/task/attempts/attempt/result");
  assert.equal(seen.init.headers.Authorization, "Bearer provider-key");
  assert.equal(seen.init.headers["X-MutualGPU-Task-Handle"], "handle");
  assert.equal(await seen.init.body.get("metadata").text(), "{\"frames\":12}");
  assert.equal(await seen.init.body.get("logs").text(), "handler log");
});

test("shared result uploader rejects a cleartext API base URL before sending credentials", async () => {
  await assert.rejects(
    uploadProviderResult({
      apiBaseUrl: "http://mutualgpu.example/",
      presharedKey: "provider-key",
      task: { taskId: "task", attemptId: "attempt", taskHandle: "handle" },
      token: "single-use-token",
      result: { resultZip: new Uint8Array([0x50, 0x4b, 3, 4]) }
    }),
    /require an https API base URL/);
});
