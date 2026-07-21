// packages/core/src/provider.js
var ProviderClientError = class extends Error {
  constructor(code, message) {
    super(message);
    this.name = "ProviderClientError";
    this.code = code;
  }
};
var ProviderClient = class {
  #transport;
  #handler;
  #active = null;
  #connection = null;
  #reconnecting = null;
  #closed = false;
  #progressSequence = 0;
  #lastProgressAt = 0;
  #reconnectDelay;
  constructor(transport, { reconnectDelay = (attempt) => Math.min(1e3 * 2 ** (attempt - 1), 3e4) } = {}) {
    this.#transport = transport;
    this.#reconnectDelay = reconnectDelay;
  }
  async enroll(definition) {
    return this.#transport.enroll(normalizeEnrollment(definition));
  }
  async connect(handler) {
    if (typeof handler !== "function") throw new TypeError("A task handler is required");
    this.#handler = handler;
    this.#closed = false;
    await this.#openConnection();
  }
  /** Reopens the session and rebinds an accepted task handle when one is active. */
  async reconnect() {
    if (!this.#handler) throw new ProviderClientError("not_connected", "Connect a provider task handler before reconnecting.");
    this.#closed = false;
    await this.#openConnection();
  }
  close() {
    this.#closed = true;
    this.#transport.close?.();
  }
  async #openConnection() {
    if (this.#connection) return this.#connection;
    const activeTaskHandle = this.#active?.assignment.taskHandle ?? "";
    const opening = Promise.resolve(this.#transport.connect(
      async (assignment) => this.#receive(assignment),
      activeTaskHandle,
      (error) => this.#disconnected(error)
    ));
    this.#connection = opening;
    try {
      await opening;
    } finally {
      if (this.#connection === opening) this.#connection = null;
    }
  }
  #disconnected() {
    if (this.#closed || this.#reconnecting) return;
    this.#reconnecting = this.#reconnectLoop().finally(() => {
      this.#reconnecting = null;
    });
  }
  async #reconnectLoop() {
    for (let attempt = 1; !this.#closed; attempt += 1) {
      const delay = Number(this.#reconnectDelay(attempt));
      if (Number.isFinite(delay) && delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
      if (this.#closed) return;
      try {
        await this.#openConnection();
        return;
      } catch {
      }
    }
  }
  async #receive(assignment) {
    if (this.#active) {
      await this.#transport.reject(assignment, "provider already owns an active task");
      return;
    }
    if (assignment.input?.url) {
      try {
        if (new URL(assignment.input.url).protocol !== "https:") throw new TypeError("non-HTTPS input URL");
      } catch {
        await this.#transport.reject(assignment, "the assignment contains an invalid input URL");
        return;
      }
    }
    const active = { assignment, state: "pending" };
    this.#active = active;
    this.#progressSequence = 0;
    this.#lastProgressAt = 0;
    const task = this.#taskFacade(active);
    try {
      await this.#handler(task);
      if (active.state === "pending") {
        await this.#reject(active, "handler returned without accepting the assignment");
      } else if (active.state === "accepted") {
        await this.#fail(active, "execution", "handler returned without completing the assignment");
      }
    } catch (error) {
      const reason = error instanceof Error ? error.message : "handler failed";
      if (active.state === "pending") await this.#reject(active, reason);
      else if (active.state === "accepted") await this.#fail(active, "execution", reason);
    } finally {
      if (this.#active === active) this.#active = null;
    }
  }
  #taskFacade(active) {
    const { assignment } = active;
    return Object.freeze({
      ...assignment,
      acknowledgementDeadline: new Date(Date.now() + 3e4),
      accept: () => this.#accept(active),
      reject: (reason) => this.#reject(active, reason),
      reportProgress: async (update) => {
        this.#requireAccepted(active);
        const now = Date.now();
        if (now - this.#lastProgressAt < 1e3) return false;
        this.#lastProgressAt = now;
        await this.#transport.progress(assignment, { ...update, sequenceNumber: ++this.#progressSequence });
        return true;
      },
      refreshInputDownload: () => {
        this.#requireAccepted(active);
        return this.#transport.refreshInputDownload(assignment);
      },
      requestResultUpload: () => {
        this.#requireAccepted(active);
        return this.#transport.requestResultUpload(assignment);
      },
      uploadResult: async (result) => {
        this.#requireAccepted(active);
        const token = await this.#transport.requestResultUpload(assignment);
        return this.#transport.uploadResult(assignment, token, result);
      },
      complete: (receipt) => this.#complete(active, receipt),
      fail: (step, reason) => this.#fail(active, step, reason)
    });
  }
  async #accept(active) {
    this.#requireState(active, "pending");
    await this.#transport.accept(active.assignment);
    active.state = "accepted";
  }
  async #reject(active, reason) {
    this.#requireState(active, "pending");
    await this.#transport.reject(active.assignment, reason);
    active.state = "terminal";
  }
  async #complete(active, receipt) {
    this.#requireAccepted(active);
    await this.#transport.complete(active.assignment, receipt);
    active.state = "terminal";
  }
  async #fail(active, step, reason) {
    this.#requireAccepted(active);
    await this.#transport.fail(active.assignment, step, reason);
    active.state = "terminal";
  }
  #requireAccepted(active) {
    this.#requireState(active, "accepted");
  }
  #requireState(active, expected) {
    if (this.#active !== active || active.state !== expected) {
      throw new ProviderClientError("invalid_task_state", `This task must be ${expected} before this operation.`);
    }
  }
};
var normalizeEnrollment = (definition) => {
  if (!definition || typeof definition !== "object" || !definition.machine || !Array.isArray(definition.capabilities)) {
    throw new TypeError("An enrollment requires a machine profile and capabilities array.");
  }
  const tiers = /* @__PURE__ */ new Set(["Small", "Medium", "Large", "ExtraLarge"]);
  const { tier, specifications } = definition.machine;
  if (!tiers.has(tier)) throw new TypeError("A machine must advertise a concrete T-shirt tier.");
  if (!specifications || !tiers.has(specifications.computeTier) || !Number.isInteger(specifications.memoryGiB) || specifications.memoryGiB <= 0) {
    throw new TypeError("Machine specifications require a concrete CPU/GPU tier and positive integer memoryGiB.");
  }
  return {
    ...definition,
    capabilities: definition.capabilities.map((capability) => {
      if (!capability || typeof capability !== "object") throw new TypeError("Every capability must be an object.");
      return {
        ...capability,
        id: { value: "00000000-0000-0000-0000-000000000000" },
        contractHash: "server-computed"
      };
    })
  };
};

// packages/core/src/result-upload.js
var ProviderUploadError = class extends Error {
  constructor(status, body) {
    super(`MutualGPU result upload failed (${status}): ${body || "no response body"}`);
    this.name = "ProviderUploadError";
    this.status = status;
  }
};
async function uploadProviderResult({ apiBaseUrl, presharedKey, task, token, result, fetchImpl = globalThis.fetch }) {
  if (!apiBaseUrl || !presharedKey || !task?.taskId || !task?.attemptId || !task?.taskHandle || !token) {
    throw new TypeError("apiBaseUrl, presharedKey, task identity, task handle, and upload token are required");
  }
  if (typeof fetchImpl !== "function") throw new TypeError("A fetch implementation is required to upload a result");
  const apiBase = new URL(apiBaseUrl);
  if (apiBase.protocol !== "https:") throw new TypeError("MutualGPU provider result uploads require an https API base URL");
  const zip = toBlob(result?.resultZip ?? result?.zip, "application/zip");
  const sha256 = await digest(zip);
  const form = new FormData();
  form.append("result", zip, fileName(result?.resultZip ?? result?.zip, "result.zip"));
  appendFile(form, "metadata", result?.metadata, "application/json", "metadata.json", true);
  appendFile(form, "thumbnail", result?.thumbnail, "image/png", "thumbnail.png");
  appendFile(form, "preview", result?.preview, "image/png", "preview.png");
  appendFile(form, "logs", result?.logs, "text/plain", "logs.txt");
  const endpoint = new URL(`/provider/tasks/${encodeURIComponent(task.taskId)}/attempts/${encodeURIComponent(task.attemptId)}/result`, apiBase);
  const response = await fetchImpl.call(globalThis, endpoint, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${presharedKey}`,
      "X-MutualGPU-Task-Handle": task.taskHandle,
      "X-MutualGPU-Upload-Token": token,
      "X-MutualGPU-Sha256": sha256
    },
    body: form
  });
  const body = await response.text();
  if (!response.ok) throw new ProviderUploadError(response.status, body);
  const receipt = JSON.parse(body).receipt;
  if (typeof receipt !== "string" || receipt.length === 0) throw new ProviderUploadError(response.status, "The upload response did not contain a receipt.");
  return { receipt, sha256 };
}
function appendFile(form, name, value, defaultType, defaultName, metadata = false) {
  if (value == null) return;
  const body = metadata && isPlainObject(value) ? JSON.stringify(value) : partBody(value);
  const blob = toBlob(body, partType(value, defaultType));
  form.append(name, blob, fileName(value, defaultName));
}
function toBlob(value, defaultType) {
  if (value == null) throw new TypeError("A ZIP result is required");
  if (value instanceof Blob) return value.type ? value : new Blob([value], { type: defaultType });
  return new Blob([value], { type: defaultType });
}
var partBody = (value) => isPlainObject(value) && (value.data ?? value.body ?? value.content) !== void 0 ? value.data ?? value.body ?? value.content : value;
var partType = (value, defaultType) => isPlainObject(value) && typeof value.contentType === "string" ? value.contentType : defaultType;
var fileName = (value, fallback) => isPlainObject(value) && typeof value.fileName === "string" ? value.fileName : fallback;
var isPlainObject = (value) => value !== null && typeof value === "object" && !(value instanceof Blob) && !ArrayBuffer.isView(value) && !(value instanceof ArrayBuffer);
async function digest(blob) {
  if (!globalThis.crypto?.subtle) throw new Error("Web Crypto is required to calculate the result SHA-256 checksum");
  const hash = await globalThis.crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
  return [...new Uint8Array(hash)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

// packages/core/src/protocol.js
var encoder = new TextEncoder();
var decoder = new TextDecoder();
var Writer = class {
  #chunks = [];
  uint(value) {
    let remaining = BigInt(value);
    if (remaining < 0n) throw new RangeError("protobuf unsigned integer must be non-negative");
    const bytes = [];
    do {
      let byte = Number(remaining & 0x7fn);
      remaining >>= 7n;
      if (remaining) byte |= 128;
      bytes.push(byte);
    } while (remaining);
    this.#chunks.push(Uint8Array.from(bytes));
    return this;
  }
  tag(field, wire2) {
    return this.uint(field << 3 | wire2);
  }
  string(field, value) {
    if (value) this.bytes(field, encoder.encode(String(value)));
    return this;
  }
  bytes(field, value) {
    const bytes = asBytes(value);
    this.tag(field, 2).uint(bytes.length);
    this.#chunks.push(bytes);
    return this;
  }
  message(field, encode) {
    const bytes = encode instanceof Uint8Array ? encode : encode();
    if (bytes.length) this.bytes(field, bytes);
    return this;
  }
  double(field, value) {
    if (value !== void 0 && value !== 0) {
      const bytes = new Uint8Array(8);
      new DataView(bytes.buffer).setFloat64(0, Number(value), true);
      this.tag(field, 1);
      this.#chunks.push(bytes);
    }
    return this;
  }
  finish() {
    const length = this.#chunks.reduce((total, chunk) => total + chunk.length, 0);
    const output = new Uint8Array(length);
    let offset = 0;
    for (const chunk of this.#chunks) {
      output.set(chunk, offset);
      offset += chunk.length;
    }
    return output;
  }
};
var Reader = class {
  #bytes;
  #offset = 0;
  constructor(value) {
    this.#bytes = asBytes(value);
  }
  get done() {
    return this.#offset >= this.#bytes.length;
  }
  uint() {
    let value = 0n;
    let shift = 0n;
    while (true) {
      if (this.#offset >= this.#bytes.length || shift > 63n) throw new TypeError("invalid protobuf varint");
      const byte = this.#bytes[this.#offset++];
      value |= BigInt(byte & 127) << shift;
      if (!(byte & 128)) return Number(value);
      shift += 7n;
    }
  }
  bytes() {
    const length = this.uint();
    if (length < 0 || this.#offset + length > this.#bytes.length) throw new TypeError("invalid protobuf length");
    const value = this.#bytes.slice(this.#offset, this.#offset + length);
    this.#offset += length;
    return value;
  }
  double() {
    if (this.#offset + 8 > this.#bytes.length) throw new TypeError("invalid protobuf double");
    const value = new DataView(this.#bytes.buffer, this.#bytes.byteOffset + this.#offset, 8).getFloat64(0, true);
    this.#offset += 8;
    return value;
  }
  skip(wire2) {
    if (wire2 === 0) {
      this.uint();
      return;
    }
    if (wire2 === 1) {
      this.#offset += 8;
    } else if (wire2 === 2) {
      this.bytes();
    } else if (wire2 === 5) {
      this.#offset += 4;
    } else throw new TypeError("unsupported protobuf wire type");
    if (this.#offset > this.#bytes.length) throw new TypeError("invalid protobuf field");
  }
  fields() {
    const fields = /* @__PURE__ */ new Map();
    while (!this.done) {
      const tag = this.uint();
      const field = tag >>> 3;
      const wire2 = tag & 7;
      if (!field) throw new TypeError("invalid protobuf field number");
      let value;
      if (wire2 === 0) value = this.uint();
      else if (wire2 === 1) value = this.double();
      else if (wire2 === 2) value = this.bytes();
      else {
        this.skip(wire2);
        continue;
      }
      const values = fields.get(field) ?? [];
      values.push(value);
      fields.set(field, values);
    }
    return fields;
  }
};
var asBytes = (value) => value instanceof Uint8Array ? value : value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(value);
var nested = (fields, field) => {
  const value = fields.get(field)?.at(-1);
  return value ? new Reader(value).fields() : /* @__PURE__ */ new Map();
};
var text = (fields, field) => {
  const value = fields.get(field)?.at(-1);
  return value ? decoder.decode(value) : "";
};
var number = (fields, field) => fields.get(field)?.at(-1) ?? 0;
var taskWire = (task) => new Writer().string(1, task.taskId).string(2, task.attemptId).string(3, task.taskHandle).finish();
var decodeTask = (fields) => ({ taskId: text(fields, 1), attemptId: text(fields, 2), taskHandle: text(fields, 3) });
var taskWriter = (writer, task) => writer.string(1, task.taskId).string(2, task.attemptId).string(3, task.taskHandle);
var handleFirstTaskWire = (task) => new Writer().string(1, task.taskHandle).string(2, task.taskId).string(3, task.attemptId).finish();
var decodeHandleFirstTask = (fields) => ({ taskHandle: text(fields, 1), taskId: text(fields, 2), attemptId: text(fields, 3) });
var encodeConnect = (value) => new Writer().tag(1, 0).uint(value.protocolVersion ?? 1).string(2, value.activeTaskHandle).string(3, value.authorization).finish();
var decodeConnect = (fields) => ({ protocolVersion: number(fields, 1), activeTaskHandle: text(fields, 2), authorization: text(fields, 3) });
var encodeRejected = (value) => taskWriter(new Writer(), value).string(4, value.reason).finish();
var decodeRejected = (fields) => ({ ...decodeTask(fields), reason: text(fields, 4) });
var encodeProgress = (value) => taskWriter(new Writer(), value).tag(4, 0).uint(value.sequenceNumber ?? 0).string(5, value.phase).double(6, value.percent).string(7, value.message).finish();
var decodeProgress = (fields) => ({ ...decodeTask(fields), sequenceNumber: number(fields, 4), phase: text(fields, 5), percent: number(fields, 6), message: text(fields, 7) });
var encodeFailed = (value) => taskWriter(new Writer(), value).string(4, value.step).string(5, value.reason).finish();
var decodeFailed = (fields) => ({ ...decodeTask(fields), step: text(fields, 4), reason: text(fields, 5) });
var encodeProvider = (value) => {
  const writer = new Writer();
  if (value.connect) writer.message(1, encodeConnect(value.connect));
  else if (value.accepted) writer.message(2, taskWire(value.accepted));
  else if (value.rejected) writer.message(3, encodeRejected(value.rejected));
  else if (value.progress) writer.message(4, encodeProgress(value.progress));
  else if (value.inputDownload) writer.message(5, handleFirstTaskWire(value.inputDownload));
  else if (value.resultUpload) writer.message(6, handleFirstTaskWire(value.resultUpload));
  else if (value.completed) writer.message(7, taskWriter(new Writer(), value.completed).string(4, value.completed.receipt).finish());
  else if (value.failed) writer.message(8, encodeFailed(value.failed));
  else throw new TypeError("a ProviderMessage body is required");
  return writer.finish();
};
var decodeProvider = (value) => {
  const fields = new Reader(value).fields();
  if (fields.has(1)) return { connect: decodeConnect(nested(fields, 1)) };
  if (fields.has(2)) return { accepted: decodeTask(nested(fields, 2)) };
  if (fields.has(3)) return { rejected: decodeRejected(nested(fields, 3)) };
  if (fields.has(4)) return { progress: decodeProgress(nested(fields, 4)) };
  if (fields.has(5)) return { inputDownload: decodeHandleFirstTask(nested(fields, 5)) };
  if (fields.has(6)) return { resultUpload: decodeHandleFirstTask(nested(fields, 6)) };
  if (fields.has(7)) {
    const message = nested(fields, 7);
    return { completed: { ...decodeTask(message), receipt: text(message, 4) } };
  }
  if (fields.has(8)) return { failed: decodeFailed(nested(fields, 8)) };
  throw new TypeError("ProviderMessage body is required");
};
var encodeInput = (value) => new Writer().string(1, value.url).string(2, value.contentType).tag(3, 0).uint(value.length ?? 0).string(4, value.sha256).finish();
var decodeInput = (fields) => ({ url: text(fields, 1), contentType: text(fields, 2), length: number(fields, 3), sha256: text(fields, 4) });
var encodeAssignment = (value) => {
  const writer = new Writer().string(1, value.taskId).string(2, value.attemptId).string(3, value.taskHandle);
  for (const [key, item] of Object.entries(value.scalars ?? {})) writer.message(4, new Writer().string(1, key).string(2, item).finish());
  if (value.input) writer.message(5, encodeInput(value.input));
  return writer.finish();
};
var decodeAssignment = (fields) => {
  const scalars = {};
  for (const value of fields.get(4) ?? []) {
    const entry = new Reader(value).fields();
    scalars[text(entry, 1)] = text(entry, 2);
  }
  return { taskId: text(fields, 1), attemptId: text(fields, 2), taskHandle: text(fields, 3), scalars, ...fields.has(5) ? { input: decodeInput(nested(fields, 5)) } : {} };
};
var encodeServer = (value) => {
  const writer = new Writer();
  if (value.connected) writer.message(1, new Writer().string(1, value.connected.executionUnitId).finish());
  else if (value.assignment) writer.message(2, encodeAssignment(value.assignment));
  else if (value.inputDownload) writer.message(3, new Writer().string(1, value.inputDownload.url).finish());
  else if (value.resultUpload) writer.message(4, new Writer().string(1, value.resultUpload.uploadToken).finish());
  else if (value.completion) writer.message(5, new Writer().string(1, value.completion.taskId).finish());
  else if (value.error) writer.message(6, new Writer().string(1, value.error.code).string(2, value.error.message).finish());
  else throw new TypeError("a ServerMessage body is required");
  return writer.finish();
};
var decodeServer = (value) => {
  const fields = new Reader(value).fields();
  if (fields.has(1)) return { connected: { executionUnitId: text(nested(fields, 1), 1) } };
  if (fields.has(2)) return { assignment: decodeAssignment(nested(fields, 2)) };
  if (fields.has(3)) return { inputDownload: { url: text(nested(fields, 3), 1) } };
  if (fields.has(4)) return { resultUpload: { uploadToken: text(nested(fields, 4), 1) } };
  if (fields.has(5)) return { completion: { taskId: text(nested(fields, 5), 1) } };
  if (fields.has(6)) {
    const error = nested(fields, 6);
    return { error: { code: text(error, 1), message: text(error, 2) } };
  }
  throw new TypeError("ServerMessage body is required");
};
var MutualGpuProtocol = Object.freeze({
  encodeProvider,
  decodeProvider,
  encodeServer,
  decodeServer,
  encodeEnrollRequest: (value) => new Writer().bytes(1, value.definition).finish(),
  decodeEnrollRequest: (value) => ({ definition: new Reader(value).fields().get(1)?.at(-1) ?? new Uint8Array() }),
  encodeEnrollResponse: (value) => new Writer().string(1, value.executionUnitId).finish(),
  decodeEnrollResponse: (value) => ({ executionUnitId: text(new Reader(value).fields(), 1) })
});
var protobuf = Object.freeze({ Writer, Reader });

// packages/web/src/websocket-transport.js
var BrowserWebSocketTransport = class {
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
    this.apiBaseUrl = baseUrlOnly ? supplied.href : typeof codecOrApiBaseUrl === "string" ? codecOrApiBaseUrl : apiBaseUrl;
    if (this.apiBaseUrl && new URL(this.apiBaseUrl).protocol !== "https:") {
      throw new TypeError("MutualGPU browser providers require an https API base URL.");
    }
    this.fetchImpl = fetchImpl;
  }
  async enroll(definition) {
    if (!this.apiBaseUrl || typeof this.fetchImpl !== "function") throw new TypeError("apiBaseUrl and fetch are required to enroll a browser provider");
    const body = this.codec.encodeEnrollRequest({ definition: new TextEncoder().encode(JSON.stringify(definition)) });
    const response = await this.fetchImpl.call(globalThis, new URL("/provider/enroll", this.apiBaseUrl), {
      method: "POST",
      headers: { Authorization: `Bearer ${this.presharedKey}`, "Content-Type": "application/x-protobuf" },
      body
    });
    if (!response.ok) throw new Error(`MutualGPU provider enrollment failed (${response.status}): ${await response.text()}`);
    return this.codec.decodeEnrollResponse(new Uint8Array(await response.arrayBuffer()));
  }
  async connect(onAssignment, activeTaskHandle = "", onDisconnect = () => {
  }) {
    if (typeof WebSocket !== "function") throw new TypeError("WebSocket is required to connect a browser provider.");
    if (this.#socket) throw new Error("The MutualGPU browser provider session is already connected.");
    const socket = this.#socket = new WebSocket(this.url);
    socket.binaryType = "arraybuffer";
    this.#onDisconnect = onDisconnect;
    this.#disconnectReported = false;
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = () => reject(new Error("The MutualGPU provider WebSocket failed to open."));
    });
    let connected = false;
    let resolveConnected;
    let rejectConnected;
    const handshake = new Promise((resolve, reject) => {
      resolveConnected = resolve;
      rejectConnected = reject;
    });
    const disconnect = (error) => {
      if (!connected) rejectConnected?.(error);
      this.#reportDisconnect(error);
    };
    socket.onmessage = (event) => {
      void this.#receive(event.data, onAssignment, () => {
        connected = true;
        resolveConnected();
      }).catch((error) => {
        disconnect(error);
        socket.close();
      });
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
  accept(task) {
    this.#send({ accepted: wire(task) });
  }
  reject(task, reason) {
    this.#send({ rejected: { ...wire(task), reason: reason || "" } });
  }
  progress(task, update) {
    this.#send({ progress: { ...wire(task), sequenceNumber: update.sequenceNumber, phase: update.phase || "", percent: update.percent ?? 0, message: update.message || "" } });
  }
  refreshInputDownload(task) {
    return this.#request(this.#inputWaiters, { inputDownload: wire(task) });
  }
  requestResultUpload(task) {
    return this.#request(this.#uploadWaiters, { resultUpload: wire(task) });
  }
  uploadResult(task, token, result) {
    return uploadProviderResult({ apiBaseUrl: this.apiBaseUrl, presharedKey: this.presharedKey, task, token, result, fetchImpl: this.fetchImpl });
  }
  complete(task, receipt) {
    return this.#request(this.#completionWaiters, { completed: { ...wire(task), receipt } });
  }
  fail(task, step, reason) {
    this.#send({ failed: { ...wire(task), step: step || "", reason: reason || "" } });
  }
  close() {
    this.#socket?.close();
  }
  async #receive(data, onAssignment, connected) {
    const bytes = data instanceof Blob ? new Uint8Array(await data.arrayBuffer()) : data instanceof ArrayBuffer ? new Uint8Array(data) : ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : (() => {
      throw new TypeError("The MutualGPU server sent a non-binary WebSocket frame.");
    })();
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
    if (!socket || socket.readyState !== void 0 && socket.readyState !== 1) {
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
    try {
      this.#onDisconnect?.(error);
    } catch {
    }
  }
  #rejectPending(error) {
    for (const waiter of [
      ...this.#inputWaiters.splice(0),
      ...this.#uploadWaiters.splice(0),
      ...this.#completionWaiters.splice(0)
    ]) waiter.reject(error);
  }
};
var wire = (task) => ({ taskId: task.taskId, attemptId: task.attemptId, taskHandle: task.taskHandle });
export {
  BrowserWebSocketTransport,
  ProviderClient
};
