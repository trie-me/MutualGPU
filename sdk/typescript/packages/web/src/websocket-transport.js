/**
 * Chrome provider transport. Each WebSocket binary frame is one canonical
 * ProviderMessage/ServerMessage protobuf envelope; enrollment remains the
 * authenticated unary HTTP mapping because browsers cannot attach gRPC metadata.
 */
import { uploadProviderResult } from "@mutualgpu/provider-core/result-upload";
import { MutualGpuProtocol } from "@mutualgpu/provider-core/protocol";

export class BrowserWebSocketTransport {
  #inputWaiters = [];
  #uploadWaiters = [];
  #completionWaiters = [];
  #socket = null;
  #onDisconnect = null;
  #disconnectReported = false;

  constructor(urlOrApiBaseUrl, presharedKey, codecOrApiBaseUrl = MutualGpuProtocol, apiBaseUrl, fetchImpl = globalThis.fetch) {
    const supplied = new URL(urlOrApiBaseUrl);
    const baseUrlOnly = supplied.protocol === "https:";
    if (!baseUrlOnly && supplied.protocol !== "wss:") throw new TypeError("MutualGPU browser providers require an https API URL or wss session URL.");
    const endpoint = baseUrlOnly ? new URL("/provider/connect", supplied) : supplied;
    if (baseUrlOnly) endpoint.protocol = "wss:";
    this.url = endpoint.href;
    this.presharedKey = presharedKey;
    this.codec = typeof codecOrApiBaseUrl === "string" ? MutualGpuProtocol : codecOrApiBaseUrl;
    this.apiBaseUrl = baseUrlOnly
      ? supplied.href
      : typeof codecOrApiBaseUrl === "string" ? codecOrApiBaseUrl : apiBaseUrl;
    if (this.apiBaseUrl && new URL(this.apiBaseUrl).protocol !== "https:") {
      throw new TypeError("MutualGPU browser providers require an https API base URL.");
    }
    this.fetchImpl = fetchImpl;
  }

  async enroll(definition) {
    if (!this.apiBaseUrl || typeof this.fetchImpl !== "function") throw new TypeError("apiBaseUrl and fetch are required to enroll a browser provider");
    const body = this.codec.encodeEnrollRequest({ definition: new TextEncoder().encode(JSON.stringify(definition)) });
    const response = await this.fetchImpl(new URL("/provider/enroll", this.apiBaseUrl), {
      method: "POST",
      headers: { Authorization: `Bearer ${this.presharedKey}`, "Content-Type": "application/x-protobuf" },
      body
    });
    if (!response.ok) throw new Error(`MutualGPU provider enrollment failed (${response.status}): ${await response.text()}`);
    return this.codec.decodeEnrollResponse(new Uint8Array(await response.arrayBuffer()));
  }

  async connect(onAssignment, activeTaskHandle = "", onDisconnect = () => {}) {
    if (typeof WebSocket !== "function") throw new TypeError("WebSocket is required to connect a browser provider.");
    if (this.#socket) throw new Error("The MutualGPU browser provider session is already connected.");

    const socket = this.#socket = new WebSocket(this.url);
    socket.binaryType = "arraybuffer";
    this.#onDisconnect = onDisconnect;
    this.#disconnectReported = false;

    await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = () => reject(new Error("The MutualGPU provider WebSocket failed to open.")); });

    let connected = false;
    let resolveConnected;
    let rejectConnected;
    const handshake = new Promise((resolve, reject) => { resolveConnected = resolve; rejectConnected = reject; });
    const disconnect = error => {
      if (!connected) rejectConnected?.(error);
      this.#reportDisconnect(error);
    };

    socket.onmessage = event => {
      void this.#receive(event.data, onAssignment, () => { connected = true; resolveConnected(); })
        .catch(error => { disconnect(error); socket.close(); });
    };
    socket.onerror = () => disconnect(new Error("The MutualGPU provider WebSocket failed."));
    socket.onclose = () => disconnect(new Error("The MutualGPU provider WebSocket closed."));

    try {
      this.#send({ connect: { protocolVersion: 1, activeTaskHandle, authorization: this.presharedKey } });
    } catch (error) {
      disconnect(error);
      socket.close();
    }
    await handshake;
  }

  accept(task) { this.#send({ accepted: wire(task) }); }
  reject(task, reason) { this.#send({ rejected: { ...wire(task), reason: reason || "" } }); }
  progress(task, update) { this.#send({ progress: { ...wire(task), sequenceNumber: update.sequenceNumber, phase: update.phase || "", percent: update.percent ?? 0, message: update.message || "" } }); }
  refreshInputDownload(task) { return this.#request(this.#inputWaiters, { inputDownload: wire(task) }); }
  requestResultUpload(task) { return this.#request(this.#uploadWaiters, { resultUpload: wire(task) }); }
  uploadResult(task, token, result) { return uploadProviderResult({ apiBaseUrl: this.apiBaseUrl, presharedKey: this.presharedKey, task, token, result, fetchImpl: this.fetchImpl }); }
  complete(task, receipt) { return this.#request(this.#completionWaiters, { completed: { ...wire(task), receipt } }); }
  fail(task, step, reason) { this.#send({ failed: { ...wire(task), step: step || "", reason: reason || "" } }); }

  close() { this.#socket?.close(); }

  async #receive(data, onAssignment, connected) {
    const bytes = data instanceof Blob
      ? new Uint8Array(await data.arrayBuffer())
      : data instanceof ArrayBuffer
        ? new Uint8Array(data)
        : ArrayBuffer.isView(data)
          ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
          : (() => { throw new TypeError("The MutualGPU server sent a non-binary WebSocket frame."); })();
    const message = this.codec.decodeServer(bytes);
    if (message.connected) connected();
    if (message.assignment) await onAssignment(message.assignment);
    if (message.inputDownload) this.#resolveInput(message.inputDownload.url);
    if (message.resultUpload) this.#uploadWaiters.shift()?.resolve(message.resultUpload.uploadToken);
    if (message.completion) this.#completionWaiters.shift()?.resolve(message.completion);
    if (message.error) throw new Error(message.error.message || "The MutualGPU server rejected the provider operation.");
  }

  #resolveInput(url) {
    try {
      if (new URL(url).protocol !== "https:") throw new TypeError("The MutualGPU server returned a non-HTTPS input URL.");
      this.#inputWaiters.shift()?.resolve(url);
    } catch (error) {
      this.#inputWaiters.shift()?.reject(error);
    }
  }

  #send(body) {
    const socket = this.#socket;
    if (!socket || (socket.readyState !== undefined && socket.readyState !== 1)) {
      throw new Error("The MutualGPU browser provider session is not connected.");
    }
    socket.send(this.codec.encodeProvider(body));
  }

  #request(waiters, body) {
    return new Promise((resolve, reject) => {
      const waiter = { resolve, reject };
      waiters.push(waiter);
      try {
        this.#send(body);
      } catch (error) {
        const index = waiters.indexOf(waiter);
        if (index >= 0) waiters.splice(index, 1);
        reject(error);
      }
    });
  }

  #reportDisconnect(error) {
    if (this.#disconnectReported) return;
    this.#disconnectReported = true;
    this.#socket = null;
    this.#rejectPending(error);
    try { this.#onDisconnect?.(error); } catch { /* an observer cannot break cleanup */ }
  }

  #rejectPending(error) {
    for (const waiter of [
      ...this.#inputWaiters.splice(0),
      ...this.#uploadWaiters.splice(0),
      ...this.#completionWaiters.splice(0)
    ]) waiter.reject(error);
  }
}

const wire = task => ({ taskId: task.taskId, attemptId: task.attemptId, taskHandle: task.taskHandle });
