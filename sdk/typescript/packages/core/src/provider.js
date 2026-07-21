export class ProviderClientError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "ProviderClientError";
    this.code = code;
  }
}

/**
 * Transport-neutral one-active-task lifecycle used by Node and browser providers.
 * A handler must explicitly accept or reject its task before doing work. This keeps
 * the acknowledgement boundary visible and lets an integrator decline a task
 * without first making the attempt running.
 */
export class ProviderClient {
  #transport;
  #handler;
  #active = null;
  #connection = null;
  #reconnecting = null;
  #closed = false;
  #progressSequence = 0;
  #lastProgressAt = 0;
  #reconnectDelay;

  constructor(transport, { reconnectDelay = attempt => Math.min(1_000 * 2 ** (attempt - 1), 30_000) } = {}) {
    this.#transport = transport;
    this.#reconnectDelay = reconnectDelay;
  }

  async enroll(definition) { return this.#transport.enroll(definition); }

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
      async assignment => this.#receive(assignment),
      activeTaskHandle,
      error => this.#disconnected(error)));
    this.#connection = opening;
    try {
      await opening;
    } finally {
      if (this.#connection === opening) this.#connection = null;
    }
  }

  #disconnected() {
    if (this.#closed || this.#reconnecting) return;
    this.#reconnecting = this.#reconnectLoop().finally(() => { this.#reconnecting = null; });
  }

  async #reconnectLoop() {
    for (let attempt = 1; !this.#closed; attempt += 1) {
      const delay = Number(this.#reconnectDelay(attempt));
      if (Number.isFinite(delay) && delay > 0) await new Promise(resolve => setTimeout(resolve, delay));
      if (this.#closed) return;
      try {
        await this.#openConnection();
        return;
      } catch {
        // The next bounded-backoff attempt owns any error mapping. The open stream
        // will also report a later close through the transport callback.
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
      acknowledgementDeadline: new Date(Date.now() + 30_000),
      accept: () => this.#accept(active),
      reject: reason => this.#reject(active, reason),
      reportProgress: async update => {
        this.#requireAccepted(active);
        const now = Date.now();
        if (now - this.#lastProgressAt < 1000) return false;
        this.#lastProgressAt = now;
        await this.#transport.progress(assignment, { ...update, sequenceNumber: ++this.#progressSequence });
        return true;
      },
      refreshInputDownload: () => { this.#requireAccepted(active); return this.#transport.refreshInputDownload(assignment); },
      requestResultUpload: () => { this.#requireAccepted(active); return this.#transport.requestResultUpload(assignment); },
      uploadResult: async result => {
        this.#requireAccepted(active);
        const token = await this.#transport.requestResultUpload(assignment);
        return this.#transport.uploadResult(assignment, token, result);
      },
      complete: receipt => this.#complete(active, receipt),
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

  #requireAccepted(active) { this.#requireState(active, "accepted"); }

  #requireState(active, expected) {
    if (this.#active !== active || active.state !== expected) {
      throw new ProviderClientError("invalid_task_state", `This task must be ${expected} before this operation.`);
    }
  }
}
