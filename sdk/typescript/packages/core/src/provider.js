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
  #progressIntervalMs;
  #progressTimer = null;
  #connected = false;
  #recoveryDeadlineMs;
  #recoveryError = null;
  #onDiagnostic;
  #sdkBuild;
  #connectionEpoch = 0;
  #reconnectDelay;
  #connectionOpenedAt = 0;
  #lifecycleTimer = null;
  #recycleWhenIdle = false;
  #idleRecycleAfterMs = 0;
  #maximumConnectionAgeMs = 0;
  #now;
  #scheduleTimeout;
  #cancelTimeout;

  constructor(transport, options = {}) {
    const {
      reconnectDelay = attempt => Math.min(1_000 * 2 ** (attempt - 1), 30_000),
      progressIntervalMs = 1_000,
      recoveryDeadlineMs = 155_000,
      sdkBuild = "cancellation-preview-2026-07-25",
      onDiagnostic = () => {},
      connectionLifecycle = transport.connectionLifecycle,
      // Browser timer methods are Web IDL operations. Keeping a detached method
      // reference makes some browsers throw "Illegal invocation" immediately
      // after the WSS handshake, when the lifecycle timer is scheduled.
      now = () => Date.now(),
      scheduleTimeout = (callback, delay) => globalThis.setTimeout(callback, delay),
      cancelTimeout = timer => globalThis.clearTimeout(timer)
    } = options;
    this.#transport = transport;
    this.#reconnectDelay = reconnectDelay;
    this.#progressIntervalMs = positiveDuration(progressIntervalMs) || 1_000;
    this.#recoveryDeadlineMs = positiveDuration(recoveryDeadlineMs) || 155_000;
    if (typeof onDiagnostic !== "function") throw new TypeError("onDiagnostic must be a function");
    this.#onDiagnostic = onDiagnostic;
    this.#sdkBuild = String(sdkBuild);
    this.#idleRecycleAfterMs = positiveDuration(connectionLifecycle?.idleRecycleAfterMs);
    this.#maximumConnectionAgeMs = positiveDuration(connectionLifecycle?.maximumConnectionAgeMs);
    if (this.#idleRecycleAfterMs && this.#maximumConnectionAgeMs && this.#idleRecycleAfterMs >= this.#maximumConnectionAgeMs) {
      throw new TypeError("maximumConnectionAgeMs must be greater than idleRecycleAfterMs");
    }
    this.#now = now;
    this.#scheduleTimeout = scheduleTimeout;
    this.#cancelTimeout = cancelTimeout;
  }

  async enroll(definition) { return this.#transport.enroll(normalizeEnrollment(definition)); }

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
    this.#connected = false;
    this.#discardPendingProgress(this.#active);
    this.#active?.abortController.abort();
    this.#active = null;
    this.#cancelLifecycleCheck();
    this.#connectionOpenedAt = 0;
    this.#transport.close?.();
  }

  async #openConnection() {
    if (this.#connection) return this.#connection;
    const activeTaskHandle = this.#active?.assignment.taskHandle ?? "";
    const opening = Promise.resolve(this.#transport.connect(
      async assignment => this.#receive(assignment),
      activeTaskHandle,
      error => this.#disconnected(error),
      cancellation => this.#cancel(cancellation)));
    this.#connection = opening;
    try {
      await opening;
      this.#connected = true;
      this.#recoveryError = null;
      this.#connectionEpoch += 1;
      this.#diagnostic("connection_rebound", this.#active, { disposition: "accepted" });
      this.#connectionOpenedAt = this.#now();
      this.#recycleWhenIdle = false;
      this.#scheduleLifecycleCheck();
    } finally {
      if (this.#connection === opening) this.#connection = null;
    }
  }

  #disconnected() {
    if (this.#closed || this.#reconnecting) return;
    this.#connected = false;
    this.#recoveryError = null;
    this.#diagnostic("disconnect_observed", this.#active, { disposition: "transient" });
    this.#cancelLifecycleCheck();
    this.#connectionOpenedAt = 0;
    this.#recycleWhenIdle = false;
    this.#reconnecting = this.#reconnectLoop()
      .catch(error => {
        this.#recoveryError = error;
        this.#expireRecovery(error);
      })
      .finally(() => { this.#reconnecting = null; });
  }

  async #reconnectLoop() {
    const startedAt = this.#now();
    for (let attempt = 1; !this.#closed; attempt += 1) {
      if (this.#now() - startedAt >= this.#recoveryDeadlineMs) {
        throw new ProviderClientError("recovery_expired", "The MutualGPU reconnect recovery deadline expired.");
      }
      this.#diagnostic("reconnect_attempt_started", this.#active, { reconnectAttempt: attempt });
      const delay = Number(this.#reconnectDelay(attempt));
      if (Number.isFinite(delay) && delay > 0) await new Promise(resolve => setTimeout(resolve, delay));
      if (this.#closed) return;
      try {
        await this.#openConnection();
        if (this.#active?.state === "accepted") void this.#flushProgress(this.#active);
        return;
      } catch {
        this.#diagnostic("reconnect_attempt_failed", this.#active, { reconnectAttempt: attempt });
        // The next bounded-backoff attempt owns any error mapping. The open stream
        // will also report a later close through the transport callback.
      }
    }
    throw new ProviderClientError("provider_closed", "The provider session was closed while reconnecting.");
  }

  async #receive(assignment) {
    if (this.#active) {
      const reason = this.#recycleWhenIdle
        ? "provider connection is draining for recycling"
        : "provider already owns an active task";
      await this.#transport.reject(assignment, reason);
      return;
    }
    if (this.#recycleWhenIdle || this.#maximumConnectionAgeExpired()) {
      this.#recycleWhenIdle = true;
      await this.#transport.reject(assignment, "provider connection is recycling");
      this.#transport.close?.();
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

    const active = { assignment, state: "pending", abortController: new AbortController(), pendingProgress: null };
    this.#active = active;
    this.#progressSequence = 0;
    this.#lastProgressAt = 0;
    const task = this.#taskFacade(active);

    try {
      await this.#handler(task);
      // Explicit close, cancellation, and recovery expiry detach the active
      // facade before aborting the handler. A handler observing that abort must
      // be allowed to return without being misreported as a provider failure.
      if (this.#active !== active) return;
      if (active.state === "pending") {
        await this.#reject(active, "handler returned without accepting the assignment");
      } else if (active.state === "accepted") {
        await this.#fail(active, "execution", "handler returned without completing the assignment");
      }
    } catch (error) {
      if (this.#active !== active) return;
      const reason = error instanceof Error ? error.message : "handler failed";
      if (active.state === "pending") await this.#reject(active, reason);
      else if (active.state === "accepted") await this.#fail(active, "execution", reason);
    } finally {
      if (this.#active === active) {
        this.#active = null;
        if (this.#recycleWhenIdle) this.#transport.close?.();
        else this.#scheduleLifecycleCheck();
      }
    }
  }

  #cancel(cancellation) {
    const active = this.#active;
    if (!active || active.state === "terminal") return;
    const { assignment } = active;
    if (assignment.taskId !== cancellation.taskId || assignment.attemptId !== cancellation.attemptId || assignment.taskHandle !== cancellation.taskHandle) return;
    active.state = "terminal";
    this.#discardPendingProgress(active);
    active.abortController.abort();
    if (this.#active === active) {
      this.#active = null;
      if (this.#recycleWhenIdle) this.#transport.close?.();
      else this.#scheduleLifecycleCheck();
    }
  }

  #scheduleLifecycleCheck() {
    this.#cancelLifecycleCheck();
    if (this.#closed || !this.#connectionOpenedAt || !this.#maximumConnectionAgeMs) return;
    const age = Math.max(0, this.#now() - this.#connectionOpenedAt);
    const threshold = this.#active ? this.#maximumConnectionAgeMs : this.#idleRecycleAfterMs;
    const delay = Math.max(0, threshold - age);
    this.#lifecycleTimer = this.#scheduleTimeout(() => this.#checkConnectionLifecycle(), delay);
    this.#lifecycleTimer?.unref?.();
  }

  #cancelLifecycleCheck() {
    if (this.#lifecycleTimer === null) return;
    this.#cancelTimeout(this.#lifecycleTimer);
    this.#lifecycleTimer = null;
  }

  #checkConnectionLifecycle() {
    this.#lifecycleTimer = null;
    if (this.#closed || !this.#connectionOpenedAt) return;
    const age = Math.max(0, this.#now() - this.#connectionOpenedAt);
    const maximumExpired = age >= this.#maximumConnectionAgeMs;
    const idleExpired = !this.#active && age >= this.#idleRecycleAfterMs;
    if (maximumExpired && this.#active) {
      this.#recycleWhenIdle = true;
      return;
    }
    if (maximumExpired || idleExpired) {
      this.#transport.close?.();
      return;
    }
    this.#scheduleLifecycleCheck();
  }

  #maximumConnectionAgeExpired() {
    return this.#connectionOpenedAt &&
      this.#maximumConnectionAgeMs &&
      this.#now() - this.#connectionOpenedAt >= this.#maximumConnectionAgeMs;
  }

  #taskFacade(active) {
    const { assignment } = active;
    return Object.freeze({
      ...assignment,
      acknowledgementDeadline: new Date(Date.now() + 30_000),
      signal: active.abortController.signal,
      accept: () => this.#accept(active),
      reject: reason => this.#reject(active, reason),
      reportProgress: async update => {
        this.#requireAccepted(active);
        active.pendingProgress = { ...update };
        return this.#flushProgress(active);
      },
      refreshInputDownload: async () => {
        this.#requireAccepted(active);
        await this.#waitForRebind(active);
        return this.#transport.refreshInputDownload(assignment);
      },
      requestResultUpload: async () => {
        this.#requireAccepted(active);
        await this.#waitForRebind(active);
        return this.#transport.requestResultUpload(assignment);
      },
      uploadResult: async result => {
        this.#requireAccepted(active);
        await this.#waitForRebind(active);
        const token = await this.#transport.requestResultUpload(assignment);
        const uploadEpoch = this.#connectionEpoch;
        try {
          return await this.#transport.uploadResult(assignment, token, result);
        } catch (error) {
          if (!this.#connected || this.#connectionEpoch !== uploadEpoch) throw new ProviderClientError("upload_outcome_unknown", "The result upload outcome is unknown; server reconciliation is required.");
          throw error;
        }
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
    await this.#waitForRebind(active);
    await this.#transport.complete(active.assignment, receipt);
    active.state = "terminal";
    this.#discardPendingProgress(active);
  }

  async #fail(active, step, reason) {
    this.#requireAccepted(active);
    await this.#waitForRebind(active);
    await this.#transport.fail(active.assignment, step, reason);
    active.state = "terminal";
    this.#discardPendingProgress(active);
  }

  #requireAccepted(active) { this.#requireState(active, "accepted"); }

  #requireState(active, expected) {
    if (this.#active !== active || active.state !== expected) {
      throw new ProviderClientError("invalid_task_state", `This task must be ${expected} before this operation.`);
    }
  }

  async #waitForRebind(active) {
    this.#requireAccepted(active);
    if (this.#connected) return;
    const reconnecting = this.#reconnecting ?? this.#connection;
    if (!reconnecting) throw new ProviderClientError("disconnected", "The MutualGPU provider session is disconnected.");
    await reconnecting;
    if (this.#recoveryError) throw this.#recoveryError;
    this.#requireAccepted(active);
    if (!this.#connected) throw new ProviderClientError("disconnected", "The MutualGPU provider session is disconnected.");
  }

  async #flushProgress(active) {
    this.#requireAccepted(active);
    if (!active.pendingProgress || !this.#connected) {
      this.#scheduleProgressFlush(active);
      this.#diagnostic("progress_coalesced", active, { disposition: "buffered", bufferOccupied: true });
      return false;
    }
    const delay = this.#progressIntervalMs - Math.max(0, this.#now() - this.#lastProgressAt);
    if (delay > 0) {
      this.#scheduleProgressFlush(active, delay);
      this.#diagnostic("progress_coalesced", active, { disposition: "buffered", bufferOccupied: true });
      return false;
    }
    const pending = active.pendingProgress;
    active.pendingProgress = null;
    this.#lastProgressAt = this.#now();
    const sequenceNumber = ++this.#progressSequence;
    try {
      await this.#transport.progress(active.assignment, { ...pending, sequenceNumber });
      this.#diagnostic("progress_sent", active, { sequence: sequenceNumber, disposition: "accepted", bufferOccupied: Boolean(active.pendingProgress) });
      return true;
    } catch {
      if (this.#active === active && active.state === "accepted" && active.pendingProgress === null) active.pendingProgress = pending;
      this.#disconnected();
      this.#scheduleProgressFlush(active);
      this.#diagnostic("progress_coalesced", active, { sequence: sequenceNumber, disposition: "transport_unavailable", bufferOccupied: true });
      return false;
    }
  }

  #scheduleProgressFlush(active, delay = this.#progressIntervalMs) {
    if (this.#progressTimer !== null || !active?.pendingProgress || this.#closed) return;
    this.#progressTimer = this.#scheduleTimeout(() => {
      this.#progressTimer = null;
      if (this.#active === active && active.state === "accepted") void this.#flushProgress(active);
    }, delay);
    this.#progressTimer?.unref?.();
  }

  #discardPendingProgress(active) {
    if (active) active.pendingProgress = null;
    if (this.#progressTimer !== null) {
      this.#cancelTimeout(this.#progressTimer);
      this.#progressTimer = null;
    }
  }

  #expireRecovery(error) {
    const active = this.#active;
    if (!active || active.state === "terminal") return;
    active.state = "terminal";
    this.#discardPendingProgress(active);
    active.abortController.abort(error);
    if (this.#active === active) this.#active = null;
    this.#diagnostic("recovery_expired", active, { disposition: error instanceof ProviderClientError ? error.code : "recovery_expired" });
  }

  #diagnostic(event, active, details = {}) {
    try {
      this.#onDiagnostic({
        event,
        sdkBuild: this.#sdkBuild,
        connectionEpoch: this.#connectionEpoch,
        taskId: active?.assignment?.taskId,
        attemptId: active?.assignment?.attemptId,
        ...details
      });
    } catch {
      // Diagnostics are advisory and cannot affect task ownership or transport recovery.
    }
  }
}

const positiveDuration = value => Number.isFinite(value) && value > 0 ? Number(value) : 0;

// Capability identity and continuity fields belong to the server protocol. Keep
// their placeholders inside the SDK so consumers only describe capabilities.
const normalizeEnrollment = definition => {
  if (!definition || typeof definition !== "object" || !definition.machine || !Array.isArray(definition.capabilities)) {
    throw new TypeError("An enrollment requires a machine profile and capabilities array.");
  }

  const tiers = new Set(["Small", "Medium", "Large", "ExtraLarge"]);
  const { tier, specifications } = definition.machine;
  if (!tiers.has(tier)) throw new TypeError("A machine must advertise a concrete T-shirt tier.");
  if (!specifications || !tiers.has(specifications.computeTier) || !Number.isInteger(specifications.memoryGiB) || specifications.memoryGiB <= 0) {
    throw new TypeError("Machine specifications require a concrete CPU/GPU tier and positive integer memoryGiB.");
  }

  return {
    ...definition,
    capabilities: definition.capabilities.map(capability => {
      if (!capability || typeof capability !== "object") throw new TypeError("Every capability must be an object.");
      return {
        ...capability,
        id: { value: "00000000-0000-0000-0000-000000000000" },
        contractHash: "server-computed"
      };
    })
  };
};
