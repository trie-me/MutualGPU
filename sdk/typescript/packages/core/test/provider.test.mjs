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
  const cancelled = MutualGpuProtocol.encodeServer({ cancelled: { taskId: "task", attemptId: "attempt", taskHandle: "handle" } });

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
  assert.deepEqual(MutualGpuProtocol.decodeServer(cancelled), { cancelled: { taskId: "task", attemptId: "attempt", taskHandle: "handle" } });
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

test("requestor cancellation aborts only the matching provider task", async () => {
  let receive;
  let cancel;
  let signal;
  const transport = {
    connect: async (onAssignment, _handle, _disconnect, onCancellation) => { receive = onAssignment; cancel = onCancellation; },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async task => {
    await task.accept();
    signal = task.signal;
    await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true }));
  });

  const handling = receive({ taskId: "task", attemptId: "attempt", taskHandle: "handle" });
  await new Promise(resolve => setImmediate(resolve));
  cancel({ taskId: "other", attemptId: "attempt", taskHandle: "handle" });
  assert.equal(signal.aborted, false);
  cancel({ taskId: "task", attemptId: "attempt", taskHandle: "handle" });
  await handling;
  assert.equal(signal.aborted, true);
  client.close();
});

test("explicit provider shutdown lets an aborted handler return without reporting a failure", async () => {
  let receive;
  const calls = [];
  const transport = {
    connect: async callback => { receive = callback; }, accept: async () => {}, reject: async () => {}, progress: async () => {},
    fail: async () => calls.push("fail"), refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}, close: () => {}
  };
  const client = new ProviderClient(transport);
  await client.connect(async task => {
    await task.accept();
    await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true }));
  });
  const handling = receive({ taskId: "task", attemptId: "attempt", taskHandle: "handle" });
  await new Promise(resolve => setImmediate(resolve));
  client.close();
  await handling;
  assert.deepEqual(calls, []);
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
  let handlerCalls = 0;
  const transport = {
    connect: async (callback, activeTaskHandle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", complete: async () => {}, close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0 });
  await client.connect(async assigned => {
    handlerCalls += 1;
    await assigned.accept();
    await new Promise(resolve => { resume = resolve; });
    await assigned.complete("receipt");
  });

  const handling = receive(task);
  await new Promise(resolve => setImmediate(resolve));
  disconnect(new Error("network lost"));
  for (let turn = 0; handles.length < 2 && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(handles, ["", "handle"]);
  assert.equal(handlerCalls, 1, "rebind must not restart the computation handler");

  resume();
  await handling;
  client.close();
});

test("progress retains only its newest value across a transient reconnect", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  const progress = [];
  const handles = [];
  let receive;
  let disconnect;
  let finish;
  const transport = {
    connect: async (callback, activeTaskHandle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, fail: async () => {}, complete: async () => {},
    progress: async (_task, update) => { progress.push(update); },
    refreshInputDownload: async () => "url", requestResultUpload: async () => "token", close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0, progressIntervalMs: 5 });
  await client.connect(async assigned => {
    await assigned.accept();
    await assigned.reportProgress({ percent: 1 });
    await assigned.reportProgress({ percent: 2 });
    await assigned.reportProgress({ percent: 3 });
    disconnect(new Error("network lost"));
    await new Promise(resolve => { finish = resolve; });
    await assigned.complete("receipt");
  });

  const handling = receive(task);
  for (let turn = 0; handles.length < 2 && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.deepEqual(progress.map(value => value.percent), [1, 3]);
  assert.deepEqual(handles, ["", "handle"]);

  finish();
  await handling;
  client.close();
});

test("task control waits for rebind instead of failing during reconnect backoff", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  let receive;
  let disconnect;
  let refresh;
  let finish;
  const transport = {
    connect: async (callback, _handle, onDisconnect) => { receive = callback; disconnect = onDisconnect; },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {}, complete: async () => {},
    refreshInputDownload: async () => { refresh(); return "https://objects.example/input"; },
    requestResultUpload: async () => "token", close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0 });
  await client.connect(async assigned => {
    await assigned.accept();
    disconnect(new Error("network lost"));
    const url = await assigned.refreshInputDownload();
    assert.equal(url, "https://objects.example/input");
    await new Promise(resolve => { finish = resolve; });
    await assigned.complete("receipt");
  });
  const handling = receive(task);
  await new Promise(resolve => { refresh = resolve; });
  for (let turn = 0; typeof finish !== "function" && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));
  assert.equal(typeof finish, "function");
  finish();
  await handling;
  client.close();
});

test("progress sent during reconnect is buffered without losing the accepted task", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  const handles = [];
  const progress = [];
  let receive;
  let disconnect;
  let finish;
  const transport = {
    connect: async (callback, activeTaskHandle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, fail: async () => {}, complete: async () => {},
    progress: async (_task, update) => { progress.push(update); },
    refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0, progressIntervalMs: 1 });
  await client.connect(async assigned => {
    await assigned.accept();
    disconnect(new Error("network lost"));
    assert.equal(await assigned.reportProgress({ percent: 55 }), false, "progress must be buffered while disconnected");
    assert.equal(await assigned.refreshInputDownload(), "https://objects.example/input");
    await new Promise(resolve => { finish = resolve; });
    await assigned.complete("receipt");
  });

  const handling = receive(task);
  for (let turn = 0; handles.length < 2 && turn < 20; turn += 1) await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.deepEqual(handles, ["", "handle"]);
  assert.deepEqual(progress.map(update => update.percent), [55]);
  assert.equal(typeof finish, "function");

  finish();
  await handling;
  client.close();
});

test("completion can be retried with the same receipt after its acknowledgement is lost", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  const handles = [];
  let receive;
  let disconnect;
  let completionCalls = 0;
  const transport = {
    connect: async (callback, activeTaskHandle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(activeTaskHandle); },
    accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
    refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token",
    complete: async () => {
      completionCalls += 1;
      if (completionCalls === 1) {
        disconnect(new Error("completion acknowledgement lost"));
        throw new Error("completion acknowledgement lost");
      }
    },
    close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0 });
  await client.connect(async assigned => {
    await assigned.accept();
    await assert.rejects(assigned.complete("receipt"), /acknowledgement lost/);
    await assigned.complete("receipt");
  });

  await receive(task);
  assert.deepEqual(handles, ["", "handle"]);
  assert.equal(completionCalls, 2);
  client.close();
});

test("recovery expiry aborts the active task without reporting a second handler failure", async () => {
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
  let receive;
  let disconnect;
  let clock = 0;
  let rejection;
  const transport = {
    connect: async (callback, _activeTaskHandle, onDisconnect) => {
      if (!receive) { receive = callback; disconnect = onDisconnect; return; }
      throw new Error("still unavailable");
    },
    accept: async () => {}, reject: async () => { rejection = true; }, progress: async () => {}, fail: async () => { rejection = true; },
    refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", complete: async () => {}, close: () => {}
  };
  const client = new ProviderClient(transport, { reconnectDelay: () => 0, recoveryDeadlineMs: 3, now: () => clock++ });
  await client.connect(async assigned => {
    await assigned.accept();
    await new Promise(resolve => assigned.signal.addEventListener("abort", resolve, { once: true }));
  });

  const handling = receive(task);
  await new Promise(resolve => setImmediate(resolve));
  disconnect(new Error("network lost"));
  await handling;
  assert.equal(rejection, undefined, "expiry must not reject or fail an already accepted task");
  client.close();
});

test("browser provider recycles a one-day-old connection as soon as it is idle", async () => {
  const clock = createLifecycleClock();
  const handles = [];
  let disconnect;
  let closes = 0;
  const transport = {
    connectionLifecycle: { idleRecycleAfterMs: 100, maximumConnectionAgeMs: 600 },
    connect: async (_, activeTaskHandle, onDisconnect) => { handles.push(activeTaskHandle); disconnect = onDisconnect; },
    close: () => { closes += 1; disconnect(new Error("connection recycled")); }
  };
  const client = new ProviderClient(transport, {
    reconnectDelay: () => 0,
    now: clock.now,
    scheduleTimeout: clock.scheduleTimeout,
    cancelTimeout: clock.cancelTimeout
  });

  await client.connect(async () => {});
  assert.equal(clock.nextDelay(), 100);
  clock.advance(100);
  clock.fire();
  for (let turn = 0; handles.length < 2 && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));

  assert.equal(closes, 1);
  assert.deepEqual(handles, ["", ""]);
  client.close();
});

test("browser lifecycle scheduling keeps the browser timer receiver", async () => {
  const originalSetTimeout = globalThis.setTimeout;
  let scheduled = false;
  globalThis.setTimeout = function (callback, delay) {
    assert.equal(this, globalThis, "browser timer methods must be invoked on globalThis");
    scheduled = true;
    return originalSetTimeout.call(globalThis, callback, delay);
  };

  const client = new ProviderClient({
    connectionLifecycle: { idleRecycleAfterMs: 100, maximumConnectionAgeMs: 600 },
    connect: async () => {},
    close: () => {}
  });

  try {
    await client.connect(async () => {});
    assert.equal(scheduled, true);
  } finally {
    client.close();
    globalThis.setTimeout = originalSetTimeout;
  }
});

test("six-day connection recycling drains a saturated provider before reconnecting", async () => {
  const clock = createLifecycleClock();
  const handles = [];
  const rejections = [];
  let disconnect;
  let receive;
  let finish;
  let closes = 0;
  const transport = {
    connectionLifecycle: { idleRecycleAfterMs: 100, maximumConnectionAgeMs: 600 },
    connect: async (callback, activeTaskHandle, onDisconnect) => {
      receive = callback;
      disconnect = onDisconnect;
      handles.push(activeTaskHandle);
    },
    accept: async () => {},
    reject: async (assignment, reason) => rejections.push([assignment.taskHandle, reason]),
    progress: async () => {}, fail: async () => {}, refreshInputDownload: async () => "url",
    requestResultUpload: async () => "token", complete: async () => {},
    close: () => { closes += 1; disconnect(new Error("connection recycled")); }
  };
  const client = new ProviderClient(transport, {
    reconnectDelay: () => 0,
    now: clock.now,
    scheduleTimeout: clock.scheduleTimeout,
    cancelTimeout: clock.cancelTimeout
  });
  await client.connect(async task => {
    await task.accept();
    await new Promise(resolve => { finish = resolve; });
    await task.complete("receipt");
  });

  const handling = receive({ taskId: "task", attemptId: "attempt", taskHandle: "active-handle" });
  await new Promise(resolve => setImmediate(resolve));
  clock.advance(100);
  clock.fire();
  assert.equal(clock.nextDelay(), 500);
  clock.advance(500);
  clock.fire();
  assert.equal(closes, 0, "an active computation must not be interrupted");

  await receive({ taskId: "duplicate", attemptId: "duplicate-attempt", taskHandle: "duplicate-handle" });
  assert.deepEqual(rejections, [["duplicate-handle", "provider connection is draining for recycling"]]);
  assert.equal(closes, 0);

  finish();
  await handling;
  for (let turn = 0; handles.length < 2 && turn < 10; turn += 1) await new Promise(resolve => setImmediate(resolve));
  assert.equal(closes, 1);
  assert.deepEqual(handles, ["", ""]);
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
      return { ok: true, status: 200, text: async () => JSON.stringify({ receipt: "receipt-1", ignoredParts: [{ name: "logs", reason: "undeclared" }] }) };
    }
  });

  assert.equal(uploaded.receipt, "receipt-1");
  assert.match(uploaded.sha256, /^[0-9a-f]{64}$/);
  assert.deepEqual(uploaded.ignoredParts, [{ name: "logs", reason: "undeclared" }]);
  assert.equal(seen.url.pathname, "/provider/tasks/task/attempts/attempt/result");
  assert.equal(seen.init.headers.Authorization, "Bearer provider-key");
  assert.equal(seen.init.headers["X-MutualGPU-Task-Handle"], "handle");
  assert.equal(await seen.init.body.get("metadata").text(), "{\"frames\":12}");
  assert.equal(await seen.init.body.get("logs").text(), "handler log");
});

function createLifecycleClock() {
  let current = 1;
  let timer = null;
  let sequence = 0;
  return {
    now: () => current,
    scheduleTimeout: (callback, delay) => {
      timer = { id: ++sequence, callback, delay };
      return timer.id;
    },
    cancelTimeout: id => { if (timer?.id === id) timer = null; },
    advance: milliseconds => { current += milliseconds; },
    nextDelay: () => timer?.delay,
    fire: () => {
      assert.ok(timer, "expected a scheduled lifecycle check");
      const pending = timer;
      timer = null;
      pending.callback();
    }
  };
}

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
