import test from "node:test";
import assert from "node:assert/strict";
import { BrowserWebSocketTransport } from "../src/websocket-transport.js";
import { MutualGpuProtocol } from "@mutualgpu/provider-core/protocol";

test("browser transport enrolls through the authenticated protobuf HTTP endpoint", async () => {
  let encoded;
  let request;
  const transport = new BrowserWebSocketTransport(
    "wss://mutualgpu.example/provider/connect",
    "provider-key",
    {
      encodeEnrollRequest: value => { encoded = value; return new Uint8Array([1, 2, 3]); },
      decodeEnrollResponse: bytes => ({ executionUnitId: `unit-${bytes.length}` })
    },
    "https://mutualgpu.example/",
    async (url, init) => {
      request = { url, init };
      return { ok: true, status: 200, arrayBuffer: async () => new Uint8Array([4, 5]).buffer };
    });

  const response = await transport.enroll({ machine: { compute: "Medium" } });

  assert.equal(response.executionUnitId, "unit-2");
  assert.equal(request.url.pathname, "/provider/enroll");
  assert.equal(request.init.headers.Authorization, "Bearer provider-key");
  assert.deepEqual(JSON.parse(new TextDecoder().decode(encoded.definition)), { machine: { compute: "Medium" } });
});

test("browser transport uses the canonical codec and waits for Connected before resolving", async () => {
  const previous = globalThis.WebSocket;
  const sent = [];
  class FakeWebSocket {
    constructor(url) { this.url = url; queueMicrotask(() => this.onopen?.()); }
    send(message) {
      sent.push(new Uint8Array(message));
      queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }).buffer }));
    }
    close() { this.onclose?.(); }
  }
  globalThis.WebSocket = FakeWebSocket;
  try {
    const transport = new BrowserWebSocketTransport("wss://mutualgpu.example/provider/connect", "provider-key", "https://mutualgpu.example/");
    await transport.connect(async () => {});
    assert.equal(sent.length, 1);
    assert.deepEqual(MutualGpuProtocol.decodeProvider(sent[0]), {
      connect: { protocolVersion: 1, activeTaskHandle: "", authorization: "provider-key" }
    });
  } finally {
    globalThis.WebSocket = previous;
  }
});

test("browser transport maps task control, input refresh, upload authorization, and completion", async () => {
  const previous = globalThis.WebSocket;
  const sent = [];
  class FakeWebSocket {
    constructor() { queueMicrotask(() => this.onopen?.()); }
    send(bytes) {
      const message = MutualGpuProtocol.decodeProvider(new Uint8Array(bytes));
      sent.push(message);
      if (message.connect) queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }).buffer }));
      if (message.inputDownload) queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ inputDownload: { url: "https://objects.example/input" } }).buffer }));
      if (message.resultUpload) queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ resultUpload: { uploadToken: "token" } }).buffer }));
      if (message.completed) queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ completion: { taskId: "task" } }).buffer }));
    }
    close() { this.onclose?.(); }
  }
  globalThis.WebSocket = FakeWebSocket;
  try {
    const transport = new BrowserWebSocketTransport("wss://mutualgpu.example/provider/connect", "provider-key", "https://mutualgpu.example/");
    const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };
    await transport.connect(async () => {});
    transport.accept(task);
    transport.reject(task, "busy");
    transport.progress(task, { sequenceNumber: 9, phase: "render", percent: 50, message: "half" });
    assert.equal(await transport.refreshInputDownload(task), "https://objects.example/input");
    assert.equal(await transport.requestResultUpload(task), "token");
    await transport.complete(task, "receipt");
    transport.fail(task, "execution", "bad shader");

    assert.deepEqual(sent, [
      { connect: { protocolVersion: 1, activeTaskHandle: "", authorization: "provider-key" } },
      { accepted: task },
      { rejected: { ...task, reason: "busy" } },
      { progress: { ...task, sequenceNumber: 9, phase: "render", percent: 50, message: "half" } },
      { inputDownload: task },
      { resultUpload: task },
      { completed: { ...task, receipt: "receipt" } },
      { failed: { ...task, step: "execution", reason: "bad shader" } }
    ]);
  } finally {
    globalThis.WebSocket = previous;
  }
});

test("browser transport rebinds the supplied active handle and reports a closed session", async () => {
  const previous = globalThis.WebSocket;
  let socket;
  let connect;
  class FakeWebSocket {
    constructor() { socket = this; queueMicrotask(() => this.onopen?.()); }
    send(bytes) {
      connect = MutualGpuProtocol.decodeProvider(new Uint8Array(bytes)).connect;
      queueMicrotask(() => this.onmessage?.({ data: MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }).buffer }));
    }
    close() { this.onclose?.(); }
  }
  globalThis.WebSocket = FakeWebSocket;
  try {
    const transport = new BrowserWebSocketTransport("wss://mutualgpu.example/provider/connect", "provider-key", "https://mutualgpu.example/");
    let disconnected;
    await transport.connect(async () => {}, "active-handle", error => { disconnected = error; });
    assert.equal(connect.activeTaskHandle, "active-handle");
    socket.close();
    assert.match(disconnected.message, /closed/);
  } finally {
    globalThis.WebSocket = previous;
  }
});

test("browser transport rejects cleartext control-plane endpoints", () => {
  assert.throws(
    () => new BrowserWebSocketTransport("ws://mutualgpu.example/provider/connect", "provider-key", "https://mutualgpu.example/"),
    /require a wss session URL/);
  assert.throws(
    () => new BrowserWebSocketTransport("wss://mutualgpu.example/provider/connect", "provider-key", "http://mutualgpu.example/"),
    /require an https API base URL/);
});
