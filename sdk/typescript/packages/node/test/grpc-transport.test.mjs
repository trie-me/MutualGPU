import { EventEmitter } from "node:events";
import test from "node:test";
import assert from "node:assert/strict";
import { NodeGrpcTransport } from "../src/grpc-transport.js";
import { MutualGpuProtocol } from "@mutualgpu/provider-core/protocol";

const frame = body => {
  const output = new Uint8Array(body.length + 5);
  new DataView(output.buffer).setUint32(1, body.length, false);
  output.set(body, 5);
  return output;
};

const unframe = body => new Uint8Array(body).slice(5);

test("node transport derives all SDK operations from one API base URL", () => {
  const transport = new NodeGrpcTransport("https://mutualgpu.example/", "provider-key");
  assert.equal(String(transport.apiBaseUrl), "https://mutualgpu.example/");
});

test("node transport reuses the enrollment HTTP/2 session for the provider stream", async () => {
  let connections = 0;
  let closes = 0;
  const paths = [];
  class Stream extends EventEmitter {
    constructor(path) { super(); this.path = path; this.destroyed = false; this.closed = false; }
    end() {
      queueMicrotask(() => {
        this.emit("data", frame(MutualGpuProtocol.encodeEnrollResponse({ executionUnitId: "unit" })));
        this.emit("trailers", { "grpc-status": "0" });
        this.emit("end");
      });
    }
    write(body) {
      const message = MutualGpuProtocol.decodeProvider(unframe(body));
      if (message.connect) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }))));
    }
  }
  const session = Object.assign(new EventEmitter(), {
    destroyed: false,
    closed: false,
    close() { closes += 1; this.closed = true; },
    request(headers) { paths.push(headers[":path"]); return new Stream(headers[":path"]); }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example", "provider-key", "https://mutualgpu.example/", globalThis.fetch,
    MutualGpuProtocol, { connect() { connections += 1; return session; } });

  await transport.enroll({ machine: {}, capabilities: [] });
  await transport.connect(async () => {});

  assert.equal(connections, 1);
  assert.equal(closes, 0);
  assert.deepEqual(paths, [
    "/mutualgpu.v1.ProviderControl/Enroll",
    "/mutualgpu.v1.ProviderControl/Connect"
  ]);
});

test("node transport opens a native HTTPS HTTP/2 gRPC session with canonical envelopes", async () => {
  const requests = [];
  class Stream extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.closed = false; }
    write(body) {
      const message = MutualGpuProtocol.decodeProvider(unframe(body));
      requests.push(message);
      if (message.connect) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }))));
      if (message.resultUpload) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ resultUpload: { uploadToken: "token" } }))));
    }
    close() { this.closed = true; this.emit("close"); }
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request(headers) { this.headers = headers; this.stream = new Stream(); return this.stream; }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example",
    "provider-key",
    "https://mutualgpu.example/",
    globalThis.fetch,
    MutualGpuProtocol,
    { connect(origin) { session.origin = origin; return session; } });

  await transport.connect(async () => {});
  const token = await transport.requestResultUpload({ taskId: "task", attemptId: "attempt", taskHandle: "handle" });

  assert.equal(session.origin, "https://mutualgpu.example");
  assert.equal(session.headers[":path"], "/mutualgpu.v1.ProviderControl/Connect");
  assert.equal(session.headers.authorization, "Bearer provider-key");
  assert.deepEqual(requests[0], { connect: { protocolVersion: 1, activeTaskHandle: "", authorization: "" } });
  assert.deepEqual(requests[1], { resultUpload: { taskId: "task", attemptId: "attempt", taskHandle: "handle" } });
  assert.equal(token, "token");
});

test("node transport maps every task-control operation and waits for server authorizations", async () => {
  const requests = [];
  class Stream extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.closed = false; }
    write(body) {
      const message = MutualGpuProtocol.decodeProvider(unframe(body));
      requests.push(message);
      if (message.connect) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }))));
      if (message.inputDownload) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ inputDownload: { url: "https://objects.example/input" } }))));
      if (message.resultUpload) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ resultUpload: { uploadToken: "token" } }))));
      if (message.completed) queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ completion: { taskId: message.completed.taskId } }))));
    }
    close() { this.closed = true; this.emit("close"); }
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request() { this.stream = new Stream(); return this.stream; }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example",
    "provider-key",
    "https://mutualgpu.example/",
    globalThis.fetch,
    MutualGpuProtocol,
    { connect() { return session; } });
  const task = { taskId: "task", attemptId: "attempt", taskHandle: "handle" };

  await transport.connect(async () => {});
  await transport.accept(task);
  await transport.reject(task, "busy");
  await transport.progress(task, { sequenceNumber: 9, phase: "render", percent: 50, message: "half" });
  assert.equal(await transport.refreshInputDownload(task), "https://objects.example/input");
  assert.equal(await transport.requestResultUpload(task), "token");
  await transport.complete(task, "receipt");
  await transport.fail(task, "execution", "bad shader");

  assert.deepEqual(requests, [
    { connect: { protocolVersion: 1, activeTaskHandle: "", authorization: "" } },
    { accepted: task },
    { rejected: { ...task, reason: "busy" } },
    { progress: { ...task, sequenceNumber: 9, phase: "render", percent: 50, message: "half" } },
    { inputDownload: task },
    { resultUpload: task },
    { completed: { ...task, receipt: "receipt" } },
    { failed: { ...task, step: "execution", reason: "bad shader" } }
  ]);
});

test("node transport sends an active handle on reconnect and reports an unexpected close", async () => {
  let request;
  class Stream extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.closed = false; }
    write(body) {
      request = MutualGpuProtocol.decodeProvider(unframe(body));
      queueMicrotask(() => this.emit("data", frame(MutualGpuProtocol.encodeServer({ connected: { executionUnitId: "unit" } }))));
    }
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request() { this.stream = new Stream(); return this.stream; }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example", "provider-key", "https://mutualgpu.example/", globalThis.fetch,
    MutualGpuProtocol, { connect() { return session; } });
  let disconnected;

  await transport.connect(async () => {}, "active-handle", error => { disconnected = error; });
  assert.equal(request.connect.activeTaskHandle, "active-handle");
  session.stream.emit("close");
  assert.match(disconnected.message, /closed/);
});

test("node transport rejects a cleartext endpoint", () => {
  assert.throws(
    () => new NodeGrpcTransport("http://mutualgpu.example", "provider-key", "https://mutualgpu.example/"),
    /requires an https endpoint/);
});

test("node transport reports a session-level connection failure", async () => {
  class Stream extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.closed = false; }
    write() {}
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request() { return new Stream(); }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example", "provider-key", "https://mutualgpu.example/", globalThis.fetch,
    MutualGpuProtocol, { connect() { return session; } });

  const connecting = transport.connect(async () => {});
  queueMicrotask(() => session.emit("error", new Error("connection refused")));
  await assert.rejects(connecting, /connection refused/);
});

test("node transport reports a session-level enrollment failure", async () => {
  class Stream extends EventEmitter {
    end() {}
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request() { return new Stream(); }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example", "provider-key", "https://mutualgpu.example/", globalThis.fetch,
    MutualGpuProtocol, { connect() { return session; } });

  const enrolling = transport.enroll({ machine: {}, capabilities: [] });
  queueMicrotask(() => session.emit("error", new Error("enrollment connection refused")));
  await assert.rejects(enrolling, /enrollment connection refused/);
});

test("node transport rejects an HTTP failure before gRPC negotiation", async () => {
  class Stream extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.closed = false; }
    write() {
      queueMicrotask(() => {
        this.emit("response", { ":status": 401 });
        this.emit("end");
      });
    }
  }
  const session = Object.assign(new EventEmitter(), {
    close() {},
    request() { return new Stream(); }
  });
  const transport = new NodeGrpcTransport(
    "https://mutualgpu.example", "provider-key", "https://mutualgpu.example/", globalThis.fetch,
    MutualGpuProtocol, { connect() { return session; } });

  await assert.rejects(transport.connect(async () => {}), /HTTP request failed \(401\)/);
});
