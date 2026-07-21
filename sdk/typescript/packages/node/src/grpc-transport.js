/**
 * Native Node HTTPS HTTP/2 gRPC transport for provider.proto. The service has a
 * unary enrollment operation and one bidirectional stream, so this uses Node's
 * built-in HTTP/2 client and the shared canonical protobuf codec. A generated
 * grpc-js-compatible client remains supported for hosts that already use one.
 */
import * as http2 from "node:http2";
import { uploadProviderResult } from "@mutualgpu/provider-core/result-upload";
import { MutualGpuProtocol } from "@mutualgpu/provider-core/protocol";

const grpcHeaders = (path, presharedKey) => ({
  ":method": "POST",
  ":path": path,
  "content-type": "application/grpc",
  "te": "trailers",
  "authorization": `Bearer ${presharedKey}`
});

const frame = message => {
  const body = message instanceof Uint8Array ? message : new Uint8Array(message);
  const framed = new Uint8Array(body.length + 5);
  new DataView(framed.buffer).setUint32(1, body.length, false);
  framed.set(body, 5);
  return framed;
};

class FrameReader {
  #buffer = new Uint8Array();

  push(chunk) {
    const next = chunk instanceof Uint8Array ? chunk : new Uint8Array(chunk);
    const combined = new Uint8Array(this.#buffer.length + next.length);
    combined.set(this.#buffer);
    combined.set(next, this.#buffer.length);
    this.#buffer = combined;
    const messages = [];
    let offset = 0;
    while (this.#buffer.length - offset >= 5) {
      if (this.#buffer[offset] !== 0) throw new Error("Compressed gRPC responses are not supported by MutualGPU.");
      const length = new DataView(this.#buffer.buffer, this.#buffer.byteOffset + offset + 1, 4).getUint32(0, false);
      if (length > 4 * 1024 * 1024) throw new Error("MutualGPU gRPC message exceeded its 4 MiB limit.");
      if (this.#buffer.length - offset - 5 < length) break;
      messages.push(this.#buffer.slice(offset + 5, offset + 5 + length));
      offset += 5 + length;
    }
    this.#buffer = this.#buffer.slice(offset);
    return messages;
  }
}

class NativeGrpcSession {
  #endpoint;
  #presharedKey;
  #codec;
  #http2;
  #session = null;
  #stream = null;
  #connected = false;
  #closed = false;
  #handshake = null;
  #onDisconnect = null;
  #inputWaiters = [];
  #uploadWaiters = [];
  #completionWaiters = [];

  constructor(endpoint, presharedKey, codec, http2Implementation) {
    this.#endpoint = new URL(endpoint);
    if (this.#endpoint.protocol !== "https:") throw new TypeError("MutualGPU native gRPC requires an https endpoint.");
    this.#presharedKey = presharedKey;
    this.#codec = codec;
    this.#http2 = http2Implementation;
  }

  async enroll(definition) {
    // Enrollment and the long-running provider stream share one HTTP/2/TLS
    // session. Closing the unary session immediately after its response can race
    // Kestrel's GOAWAY write on macOS and produces a spurious SslStream failure.
    const session = this.#getSession();
    const stream = session.request(grpcHeaders("/mutualgpu.v1.ProviderControl/Enroll", this.#presharedKey));
    const reader = new FrameReader();
    const responses = [];
    let rejectSession = () => {};
    const result = new Promise((resolve, reject) => {
      rejectSession = reject;
      let grpcStatus = "0";
      let grpcMessage = "";
      let httpStatus = 200;
      stream.on("response", headers => {
        httpStatus = Number(headers[":status"] ?? 200);
        grpcStatus = String(headers["grpc-status"] ?? grpcStatus);
        grpcMessage = String(headers["grpc-message"] ?? grpcMessage);
      });
      stream.on("data", chunk => { try { responses.push(...reader.push(chunk)); } catch (error) { reject(error); } });
      stream.on("trailers", trailers => {
        grpcStatus = String(trailers["grpc-status"] ?? grpcStatus);
        grpcMessage = String(trailers["grpc-message"] ?? grpcMessage);
      });
      // HTTP/2 sessions may fail before their request stream gets a chance to
      // surface an error. Propagate that as a normal enrollment rejection.
      session.once("error", rejectSession);
      stream.on("error", reject);
      stream.on("end", () => {
        if (httpStatus !== 200) {
          reject(new Error(`MutualGPU enrollment HTTP request failed (${httpStatus}).`));
          return;
        }
        if (grpcStatus !== "0" || responses.length !== 1) {
          reject(new Error(`MutualGPU enrollment failed (${grpcStatus}): ${grpcMessage}`));
          return;
        }
        try { resolve(this.#codec.decodeEnrollResponse(responses[0])); } catch (error) { reject(error); }
      });
    });
    try {
      stream.end(frame(this.#codec.encodeEnrollRequest({ definition: new TextEncoder().encode(JSON.stringify(definition)) })));
      return await result;
    } catch (error) {
      if (this.#session === session) this.#session = null;
      session.close();
      throw error;
    } finally {
      session.off("error", rejectSession);
    }
  }

  async connect(onAssignment, activeTaskHandle = "", onDisconnect = () => {}) {
    if (this.#stream && !this.#stream.destroyed && !this.#stream.closed) {
      throw new Error("The MutualGPU gRPC provider session is already connected.");
    }

    this.#closed = false;
    this.#connected = false;
    this.#onDisconnect = onDisconnect;
    const session = this.#getSession();
    const stream = session.request(grpcHeaders("/mutualgpu.v1.ProviderControl/Connect", this.#presharedKey));
    this.#session = session;
    this.#stream = stream;
    const reader = new FrameReader();

    const handshake = new Promise((resolve, reject) => { this.#handshake = { resolve, reject }; });
    const end = error => this.#end(stream, error);
    // A transport-level failure is not guaranteed to be forwarded to the
    // individual request stream by every HTTP/2 implementation.
    session.on("error", end);
    stream.on("data", chunk => {
      try {
        for (const payload of reader.push(chunk)) this.#receive(stream, this.#codec.decodeServer(payload), onAssignment);
      } catch (error) {
        end(error);
      }
    });
    stream.on("response", headers => {
      const status = Number(headers[":status"] ?? 200);
      if (status !== 200) end(new Error(`MutualGPU gRPC session HTTP request failed (${status}).`));
    });
    stream.on("trailers", trailers => {
      if (String(trailers["grpc-status"] ?? "0") !== "0") {
        end(new Error(`MutualGPU gRPC session failed (${trailers["grpc-status"]}): ${trailers["grpc-message"] ?? ""}`));
      }
    });
    stream.on("error", end);
    stream.on("end", () => end(new Error("The MutualGPU gRPC session ended.")));
    stream.on("close", () => end(new Error("The MutualGPU gRPC session closed.")));

    try {
      this.send({ connect: { protocolVersion: 1, activeTaskHandle, authorization: "" } });
    } catch (error) {
      end(error);
    }
    return handshake;
  }

  send(message) {
    const stream = this.#stream;
    if (!stream || stream.destroyed || stream.closed) throw new Error("MutualGPU gRPC session is not connected.");
    stream.write(frame(this.#codec.encodeProvider(message)));
  }

  requestInput(task) { return this.#requestResponse(this.#inputWaiters, { inputDownload: wire(task) }); }
  requestUpload(task) { return this.#requestResponse(this.#uploadWaiters, { resultUpload: wire(task) }); }
  complete(task, receipt) { return this.#requestResponse(this.#completionWaiters, { completed: { ...wire(task), receipt } }); }

  close() {
    this.#closed = true;
    const stream = this.#stream;
    const session = this.#session;
    this.#end(stream, new Error("The MutualGPU gRPC session was closed by the provider."));
    stream?.close();
    session?.close();
  }

  #receive(stream, message, onAssignment) {
    if (stream !== this.#stream) return;
    if (message.connected && !this.#connected) {
      this.#connected = true;
      this.#handshake?.resolve(message.connected);
      this.#handshake = null;
    }
    if (message.assignment) {
      void Promise.resolve(onAssignment(message.assignment)).catch(error => this.#end(stream, error));
    }
    if (message.inputDownload) this.#resolveInput(message.inputDownload.url);
    if (message.resultUpload) this.#uploadWaiters.shift()?.resolve(message.resultUpload.uploadToken);
    if (message.completion) this.#completionWaiters.shift()?.resolve(message.completion);
    if (message.error) this.#end(stream, new Error(message.error.message || message.error.code || "MutualGPU provider operation failed."));
  }

  #resolveInput(url) {
    try {
      if (new URL(url).protocol !== "https:") throw new TypeError("The MutualGPU server returned a non-HTTPS input URL.");
      this.#inputWaiters.shift()?.resolve(url);
    } catch (error) {
      this.#inputWaiters.shift()?.reject(error);
    }
  }

  #requestResponse(waiters, message) {
    return new Promise((resolve, reject) => {
      const waiter = { resolve, reject };
      waiters.push(waiter);
      try {
        this.send(message);
      } catch (error) {
        const index = waiters.indexOf(waiter);
        if (index >= 0) waiters.splice(index, 1);
        reject(error);
      }
    });
  }

  #end(stream, error) {
    if (!stream || stream !== this.#stream) return;
    const wasConnected = this.#connected;
    const session = this.#session;
    this.#stream = null;
    this.#session = null;
    this.#connected = false;
    this.#handshake?.reject(error);
    this.#handshake = null;
    this.#rejectAll(error);
    session?.close();
    if (wasConnected && !this.#closed) {
      try { this.#onDisconnect?.(error); } catch { /* observers cannot break cleanup */ }
    }
  }

  #rejectAll(error) {
    for (const waiter of [
      ...this.#inputWaiters.splice(0),
      ...this.#uploadWaiters.splice(0),
      ...this.#completionWaiters.splice(0)
    ]) waiter.reject(error);
  }

  #getSession() {
    if (this.#session && !this.#session.destroyed && !this.#session.closed) return this.#session;
    const session = this.#http2.connect(this.#endpoint.origin);
    this.#session = session;
    session.once("close", () => {
      if (this.#session === session) this.#session = null;
    });
    return session;
  }
}

/**
 * Uses the native HTTPS HTTP/2 transport when constructed with an endpoint. A
 * generated @grpc/grpc-js-compatible client remains accepted for existing hosts.
 */
export class NodeGrpcTransport {
  #native;
  #legacy;

  constructor(endpointOrClient, presharedKey, apiBaseUrl, fetchImpl = globalThis.fetch, codec = MutualGpuProtocol, http2Implementation = http2) {
    this.presharedKey = presharedKey;
    const nativeEndpoint = typeof endpointOrClient === "string" || endpointOrClient instanceof URL;
    this.apiBaseUrl = apiBaseUrl ?? (nativeEndpoint ? endpointOrClient : undefined);
    this.fetchImpl = fetchImpl;
    if (nativeEndpoint) {
      this.#native = new NativeGrpcSession(endpointOrClient, presharedKey, codec, http2Implementation);
    } else {
      this.#legacy = endpointOrClient;
    }
  }

  async enroll(definition) {
    return this.#native
      ? this.#native.enroll(definition)
      : this.#legacy.enroll(definition, { authorization: `Bearer ${this.presharedKey}` });
  }

  async connect(onAssignment, activeTaskHandle = "", onDisconnect = () => {}) {
    return this.#native
      ? this.#native.connect(onAssignment, activeTaskHandle, onDisconnect)
      : this.#legacy.connect({ authorization: `Bearer ${this.presharedKey}` }, onAssignment, activeTaskHandle, onDisconnect);
  }

  accept(task) { return this.#sendOrLegacy({ accepted: wire(task) }, () => this.#legacy.send({ accepted: wire(task) })); }
  reject(task, reason) { const message = { rejected: { ...wire(task), reason: reason || "" } }; return this.#sendOrLegacy(message, () => this.#legacy.send(message)); }
  progress(task, update) { const message = { progress: { ...wire(task), sequenceNumber: update.sequenceNumber, phase: update.phase || "", percent: update.percent ?? 0, message: update.message || "" } }; return this.#sendOrLegacy(message, () => this.#legacy.send(message)); }
  refreshInputDownload(task) { return this.#native ? this.#native.requestInput(task) : this.#legacy.requestInputDownload(wire(task)); }
  async requestResultUpload(task) { return this.#native ? this.#native.requestUpload(task) : tokenFrom(await this.#legacy.requestResultUpload(wire(task))); }
  uploadResult(task, token, result) { return uploadProviderResult({ apiBaseUrl: this.apiBaseUrl, presharedKey: this.presharedKey, task, token, result, fetchImpl: this.fetchImpl }); }
  complete(task, receipt) { return this.#native ? this.#native.complete(task, receipt) : this.#legacy.send({ completed: { ...wire(task), receipt } }); }
  fail(task, step, reason) { const message = { failed: { ...wire(task), step: step || "", reason: reason || "" } }; return this.#sendOrLegacy(message, () => this.#legacy.send(message)); }
  close() { this.#native?.close(); this.#legacy?.close?.(); }

  #sendOrLegacy(message, legacy) {
    if (this.#native) return this.#native.send(message);
    return legacy();
  }
}

const wire = task => ({ taskId: task.taskId, attemptId: task.attemptId, taskHandle: task.taskHandle });
const tokenFrom = authorization => typeof authorization === "string" ? authorization : authorization?.uploadToken;
