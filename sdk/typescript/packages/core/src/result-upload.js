/** Error returned when the provider result endpoint rejects a multipart publication. */
export class ProviderUploadError extends Error {
  constructor(status, body) {
    super(`MutualGPU result upload failed (${status}): ${body || "no response body"}`);
    this.name = "ProviderUploadError";
    this.status = status;
  }
}

/**
 * Shared Node/browser data-plane operation. Transports obtain a single-use control-plane
 * token; this function constructs the authenticated multipart request and checksum.
 */
export async function uploadProviderResult({ apiBaseUrl, presharedKey, task, token, result, fetchImpl = globalThis.fetch }) {
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
  // Keep native browser fetch bound to Window when the caller uses the default.
  // Custom test and application implementations remain ordinary callables.
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

const partBody = value => isPlainObject(value) && (value.data ?? value.body ?? value.content) !== undefined
  ? value.data ?? value.body ?? value.content
  : value;

const partType = (value, defaultType) => isPlainObject(value) && typeof value.contentType === "string" ? value.contentType : defaultType;

const fileName = (value, fallback) => isPlainObject(value) && typeof value.fileName === "string" ? value.fileName : fallback;

const isPlainObject = value => value !== null && typeof value === "object" && !(value instanceof Blob) && !ArrayBuffer.isView(value) && !(value instanceof ArrayBuffer);

async function digest(blob) {
  if (!globalThis.crypto?.subtle) throw new Error("Web Crypto is required to calculate the result SHA-256 checksum");
  const hash = await globalThis.crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
  return [...new Uint8Array(hash)].map(byte => byte.toString(16).padStart(2, "0")).join("");
}
