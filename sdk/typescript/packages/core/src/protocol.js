/*
 * Generated-wire equivalent for provider.proto. The browser cannot use a native
 * gRPC runtime, so this deliberately tiny codec owns the canonical protobuf
 * envelopes shared by the browser WebSocket and Node gRPC adapters. Keep it in
 * lockstep with src/MutualGPU.Protocol/provider.proto; conformance fixtures test
 * its bytes against the .NET generated contracts.
 */
const encoder = new TextEncoder();
const decoder = new TextDecoder();

class Writer {
  #chunks = [];
  uint(value) {
    let remaining = BigInt(value);
    if (remaining < 0n) throw new RangeError("protobuf unsigned integer must be non-negative");
    const bytes = [];
    do { let byte = Number(remaining & 0x7fn); remaining >>= 7n; if (remaining) byte |= 0x80; bytes.push(byte); } while (remaining);
    this.#chunks.push(Uint8Array.from(bytes));
    return this;
  }
  tag(field, wire) { return this.uint((field << 3) | wire); }
  string(field, value) { if (value) this.bytes(field, encoder.encode(String(value))); return this; }
  bytes(field, value) { const bytes = asBytes(value); this.tag(field, 2).uint(bytes.length); this.#chunks.push(bytes); return this; }
  message(field, encode) { const bytes = encode instanceof Uint8Array ? encode : encode(); if (bytes.length) this.bytes(field, bytes); return this; }
  double(field, value) { if (value !== undefined && value !== 0) { const bytes = new Uint8Array(8); new DataView(bytes.buffer).setFloat64(0, Number(value), true); this.tag(field, 1); this.#chunks.push(bytes); } return this; }
  finish() { const length = this.#chunks.reduce((total, chunk) => total + chunk.length, 0); const output = new Uint8Array(length); let offset = 0; for (const chunk of this.#chunks) { output.set(chunk, offset); offset += chunk.length; } return output; }
}

class Reader {
  #bytes; #offset = 0;
  constructor(value) { this.#bytes = asBytes(value); }
  get done() { return this.#offset >= this.#bytes.length; }
  uint() {
    let value = 0n; let shift = 0n;
    while (true) {
      if (this.#offset >= this.#bytes.length || shift > 63n) throw new TypeError("invalid protobuf varint");
      const byte = this.#bytes[this.#offset++]; value |= BigInt(byte & 0x7f) << shift;
      if (!(byte & 0x80)) return Number(value);
      shift += 7n;
    }
  }
  bytes() { const length = this.uint(); if (length < 0 || this.#offset + length > this.#bytes.length) throw new TypeError("invalid protobuf length"); const value = this.#bytes.slice(this.#offset, this.#offset + length); this.#offset += length; return value; }
  double() { if (this.#offset + 8 > this.#bytes.length) throw new TypeError("invalid protobuf double"); const value = new DataView(this.#bytes.buffer, this.#bytes.byteOffset + this.#offset, 8).getFloat64(0, true); this.#offset += 8; return value; }
  skip(wire) { if (wire === 0) { this.uint(); return; } if (wire === 1) { this.#offset += 8; } else if (wire === 2) { this.bytes(); } else if (wire === 5) { this.#offset += 4; } else throw new TypeError("unsupported protobuf wire type"); if (this.#offset > this.#bytes.length) throw new TypeError("invalid protobuf field"); }
  fields() { const fields = new Map(); while (!this.done) { const tag = this.uint(); const field = tag >>> 3; const wire = tag & 7; if (!field) throw new TypeError("invalid protobuf field number"); let value; if (wire === 0) value = this.uint(); else if (wire === 1) value = this.double(); else if (wire === 2) value = this.bytes(); else { this.skip(wire); continue; } const values = fields.get(field) ?? []; values.push(value); fields.set(field, values); } return fields; }
}

const asBytes = value => value instanceof Uint8Array ? value : value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(value);
const nested = (fields, field) => { const value = fields.get(field)?.at(-1); return value ? new Reader(value).fields() : new Map(); };
const text = (fields, field) => { const value = fields.get(field)?.at(-1); return value ? decoder.decode(value) : ""; };
const number = (fields, field) => fields.get(field)?.at(-1) ?? 0;

const taskWire = task => new Writer().string(1, task.taskId).string(2, task.attemptId).string(3, task.taskHandle).finish();
const decodeTask = fields => ({ taskId: text(fields, 1), attemptId: text(fields, 2), taskHandle: text(fields, 3) });
const taskWriter = (writer, task) => writer.string(1, task.taskId).string(2, task.attemptId).string(3, task.taskHandle);
// InputDownloadRequest and ResultUploadRequest predate the other task control
// messages and place the authorization handle first on the canonical wire.
const handleFirstTaskWire = task => new Writer().string(1, task.taskHandle).string(2, task.taskId).string(3, task.attemptId).finish();
const decodeHandleFirstTask = fields => ({ taskHandle: text(fields, 1), taskId: text(fields, 2), attemptId: text(fields, 3) });
const encodeConnect = value => new Writer().tag(1, 0).uint(value.protocolVersion ?? 1).string(2, value.activeTaskHandle).string(3, value.authorization).finish();
const decodeConnect = fields => ({ protocolVersion: number(fields, 1), activeTaskHandle: text(fields, 2), authorization: text(fields, 3) });
const encodeRejected = value => taskWriter(new Writer(), value).string(4, value.reason).finish();
const decodeRejected = fields => ({ ...decodeTask(fields), reason: text(fields, 4) });
const encodeProgress = value => taskWriter(new Writer(), value).tag(4, 0).uint(value.sequenceNumber ?? 0).string(5, value.phase).double(6, value.percent).string(7, value.message).finish();
const decodeProgress = fields => ({ ...decodeTask(fields), sequenceNumber: number(fields, 4), phase: text(fields, 5), percent: number(fields, 6), message: text(fields, 7) });
const encodeFailed = value => taskWriter(new Writer(), value).string(4, value.step).string(5, value.reason).finish();
const decodeFailed = fields => ({ ...decodeTask(fields), step: text(fields, 4), reason: text(fields, 5) });

const encodeProvider = value => {
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

const decodeProvider = value => {
  const fields = new Reader(value).fields();
  if (fields.has(1)) return { connect: decodeConnect(nested(fields, 1)) };
  if (fields.has(2)) return { accepted: decodeTask(nested(fields, 2)) };
  if (fields.has(3)) return { rejected: decodeRejected(nested(fields, 3)) };
  if (fields.has(4)) return { progress: decodeProgress(nested(fields, 4)) };
  if (fields.has(5)) return { inputDownload: decodeHandleFirstTask(nested(fields, 5)) };
  if (fields.has(6)) return { resultUpload: decodeHandleFirstTask(nested(fields, 6)) };
  if (fields.has(7)) { const message = nested(fields, 7); return { completed: { ...decodeTask(message), receipt: text(message, 4) } }; }
  if (fields.has(8)) return { failed: decodeFailed(nested(fields, 8)) };
  throw new TypeError("ProviderMessage body is required");
};

const encodeInput = value => new Writer().string(1, value.url).string(2, value.contentType).tag(3, 0).uint(value.length ?? 0).string(4, value.sha256).finish();
const decodeInput = fields => ({ url: text(fields, 1), contentType: text(fields, 2), length: number(fields, 3), sha256: text(fields, 4) });
const encodeAssignment = value => {
  const writer = new Writer().string(1, value.taskId).string(2, value.attemptId).string(3, value.taskHandle);
  for (const [key, item] of Object.entries(value.scalars ?? {})) writer.message(4, new Writer().string(1, key).string(2, item).finish());
  if (value.input) writer.message(5, encodeInput(value.input));
  return writer.finish();
};
const decodeAssignment = fields => {
  const scalars = {};
  for (const value of fields.get(4) ?? []) { const entry = new Reader(value).fields(); scalars[text(entry, 1)] = text(entry, 2); }
  return { taskId: text(fields, 1), attemptId: text(fields, 2), taskHandle: text(fields, 3), scalars, ...(fields.has(5) ? { input: decodeInput(nested(fields, 5)) } : {}) };
};

const encodeServer = value => {
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

const decodeServer = value => {
  const fields = new Reader(value).fields();
  if (fields.has(1)) return { connected: { executionUnitId: text(nested(fields, 1), 1) } };
  if (fields.has(2)) return { assignment: decodeAssignment(nested(fields, 2)) };
  if (fields.has(3)) return { inputDownload: { url: text(nested(fields, 3), 1) } };
  if (fields.has(4)) return { resultUpload: { uploadToken: text(nested(fields, 4), 1) } };
  if (fields.has(5)) return { completion: { taskId: text(nested(fields, 5), 1) } };
  if (fields.has(6)) { const error = nested(fields, 6); return { error: { code: text(error, 1), message: text(error, 2) } }; }
  throw new TypeError("ServerMessage body is required");
};

export const MutualGpuProtocol = Object.freeze({
  encodeProvider,
  decodeProvider,
  encodeServer,
  decodeServer,
  encodeEnrollRequest: value => new Writer().bytes(1, value.definition).finish(),
  decodeEnrollRequest: value => ({ definition: new Reader(value).fields().get(1)?.at(-1) ?? new Uint8Array() }),
  encodeEnrollResponse: value => new Writer().string(1, value.executionUnitId).finish(),
  decodeEnrollResponse: value => ({ executionUnitId: text(new Reader(value).fields(), 1) })
});

export const protobuf = Object.freeze({ Writer, Reader });
