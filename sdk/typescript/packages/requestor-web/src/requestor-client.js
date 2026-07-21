export class RequestorApiError extends Error {
  constructor(status, code, problem, body) {
    super(problem?.detail || problem?.title || code || `MutualGPU request failed (${status}).`);
    this.name = "RequestorApiError";
    this.status = status;
    this.code = code;
    this.problem = problem;
    this.body = body;
  }
}

/** Cookie-backed browser client for the finite requestor HTTP API. */
export class RequestorClient {
  #fetch;
  #session = null;

  constructor(apiBaseUrl, { fetchImpl = globalThis.fetch } = {}) {
    const base = new URL(apiBaseUrl);
    if (base.protocol !== "https:") throw new TypeError("MutualGPU requestors require an https API URL.");
    if (typeof fetchImpl !== "function") throw new TypeError("A fetch implementation is required.");
    this.apiBaseUrl = base.href;
    this.#fetch = fetchImpl;
  }

  async listCapabilities() {
    return (await this.#send("/api/capabilities/")).data;
  }

  async listTasks() {
    return (await this.#send("/api/tasks/")).data;
  }

  async getTask(taskId) {
    return (await this.#send(`/api/tasks/${segment(taskId)}`)).data;
  }

  async submitTask(submission, image = null) {
    if (!submission || typeof submission !== "object") throw new TypeError("A task submission is required.");
    if (image == null) {
      return (await this.#send("/api/tasks/", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(submission)
      })).data;
    }
    if (!(image instanceof Blob)) throw new TypeError("A requestor image must be a Blob or File.");
    const form = new FormData();
    form.append("submission", JSON.stringify(submission));
    form.append("image", image, typeof image.name === "string" && image.name ? image.name : "input-image");
    return (await this.#send("/api/tasks/", { method: "POST", body: form })).data;
  }

  async reevaluateTask(taskId) {
    const result = await this.#send(`/api/tasks/${segment(taskId)}/reevaluate`, { method: "POST" });
    return { location: result.response.headers.get("location"), task: result.data };
  }

  async getTaskResult(taskId) {
    return (await this.#send(`/api/tasks/${segment(taskId)}/result`)).data;
  }

  async createWebGpuEnrollment() {
    return (await this.#send("/api/webgpu-enrollments", { method: "POST" })).data;
  }

  async #send(path, init = {}, retryIdentity = true) {
    await this.#ensureSession();
    const response = await this.#fetch.call(globalThis, new URL(path, this.apiBaseUrl), {
      ...init,
      credentials: "include"
    });
    const parsed = await parse(response);
    if (response.ok) return { response, data: parsed.data };
    const code = parsed.data?.code;
    if (retryIdentity && response.status === 400 && code === "requestor_identity_missing") {
      return this.#send(path, init, false);
    }
    throw new RequestorApiError(response.status, code, parsed.data, parsed.body);
  }

  #ensureSession() {
    if (!this.#session) {
      const opening = this.#fetch.call(globalThis, new URL("/", this.apiBaseUrl), {
        cache: "no-store",
        credentials: "include"
      }).then(async response => {
        if (response.ok) return;
        const parsed = await parse(response);
        throw new RequestorApiError(response.status, parsed.data?.code, parsed.data, parsed.body);
      });
      this.#session = opening;
      opening.catch(() => {
        if (this.#session === opening) this.#session = null;
      });
    }
    return this.#session;
  }
}

async function parse(response) {
  const body = await response.text();
  if (!body) return { body, data: null };
  if (/^application\/(?:problem\+)?json\b/i.test(response.headers.get("content-type") ?? "")) {
    try { return { body, data: JSON.parse(body) }; } catch { /* return the raw body below */ }
  }
  return { body, data: body };
}

function segment(value) {
  if (typeof value !== "string" || !value) throw new TypeError("A task ID is required.");
  return encodeURIComponent(value);
}
