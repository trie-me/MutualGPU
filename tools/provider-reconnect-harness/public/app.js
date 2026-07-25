import { BrowserWebSocketTransport, ProviderClient } from "/sdk/mutualgpu-provider-sdk.js";

const $ = selector => document.querySelector(selector);
const normalControls = ["#burst-progress", "#buffer-progress", "#refresh-input", "#request-upload"];
const scenarioControls = ["#acr-refresh", "#acr-upload", "#stale-progress", "#wrong-handle", "#malformed-progress", "#retry-completion"];
const fullPipelineControls = ["#run-full-certification", "#run-full-pipeline", "#run-provider-failure", "#run-requestor-cancellation"];
let client;
let transport;
let activeTask;
let expectedRebind;
let fullPipelineRun;
let rebindWaiter;
let testProviderKey;
let certificationRun;

const testCapabilityName = "mutualgpu-browser-pipeline-regression";
const testProviderDefinition = Object.freeze({
  machine: { tier: "Small", specifications: { computeTier: "Small", memoryGiB: 8 } },
  capabilities: [{
    name: testCapabilityName,
    description: "Disposable browser end-to-end regression provider.",
    inputs: [
      { key: "seed", type: "String", required: true, label: "Test seed", displayOrder: 0 },
      { key: "input", type: "Image", required: true, label: "Test input", contentTypes: ["image/png"], displayOrder: 1 }
    ],
    output: {
      hasThumbnail: true,
      hasPreview: true,
      hasMetadata: true,
      hasLogs: true,
      previewContentTypes: ["image/png"]
    }
  }]
});

const emptyZip = new Uint8Array([0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
const tinyPng = Uint8Array.from(atob("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL0pAAAAABJRU5ErkJggg=="), character => character.charCodeAt(0));

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function deferred() {
  let resolve;
  let reject;
  return { promise: new Promise((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise; }), resolve, reject };
}

function apiBaseUrl() {
  const value = $("#api-base-url").value.trim();
  const parsed = new URL(value);
  if (parsed.protocol !== "https:") throw new TypeError("The MutualGPU API URL must use HTTPS.");
  return parsed;
}

function apiEndpoint(path) {
  return new URL(path, apiBaseUrl());
}

async function apiFetch(path, options = {}) {
  return fetch(apiEndpoint(path), { credentials: "include", ...options });
}

async function apiJson(path, options = {}, action = "API request") {
  const response = await apiFetch(path, options);
  if (!response.ok) throw new Error(`${action} failed (${response.status}).`);
  return response.json();
}

async function retry(action, timeoutMs = 12_000, intervalMs = 250) {
  const deadline = performance.now() + timeoutMs;
  let lastError;
  while (performance.now() < deadline) {
    try { return await action(); }
    catch (error) { lastError = error; await delay(intervalMs); }
  }
  throw lastError ?? new Error("timed_out");
}

function log(event, level = "info") {
  const item = document.createElement("li");
  item.className = level === "info" ? "" : level;
  item.textContent = `${new Date().toISOString()}  ${event}`;
  $("#events").prepend(item);
}

function state(element, value, tone = "idle") {
  element.textContent = value;
  element.className = `state state-${tone}`;
}

function safeFailure(error, fallback) {
  if (typeof error?.code === "string" && error.code) return `${fallback} (${error.code})`;
  const message = typeof error?.message === "string" ? error.message
    .replace(/Bearer\s+[^\s]+/gi, "Bearer [redacted]")
    .replace(/https?:\/\/[^\s)]+/gi, "[URL redacted]")
    .slice(0, 180) : "";
  return message ? `${fallback}: ${message}` : fallback;
}

function requireTask() {
  if (!activeTask) throw new Error("An accepted task is required.");
  return activeTask;
}

function enableTaskControls(enabled) {
  [...normalControls, ...scenarioControls].forEach(selector => { $(selector).disabled = !enabled; });
}

function enableFullPipelineControls(enabled) {
  if (enabled && certificationRun) return;
  fullPipelineControls.forEach(selector => { $(selector).disabled = !enabled; });
}

function mark(name, outcome, detail) {
  const id = `result-${name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "")}`;
  let row = $(`#${id}`);
  if (!row) {
    row = document.createElement("div");
    row.id = id;
    row.innerHTML = `<dt></dt><dd></dd>`;
    $("#matrix-results").prepend(row);
  }
  row.querySelector("dt").textContent = name;
  const value = row.querySelector("dd");
  value.textContent = detail;
  value.className = `matrix-${outcome}`;
}

async function withTimeout(promise, milliseconds = 8_000) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("timed_out")), milliseconds); })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function disconnectForAcr() {
  transport.close();
  state($("#connection-state"), "Reconnecting", "alert");
  await delay(50);
}

async function finishTaskUi() {
  activeTask = null;
  enableTaskControls(false);
  state($("#task-state"), "Completed", "live");
  client?.close();
  state($("#connection-state"), "Closed", "idle");
}

function diagnostic(diagnosticEvent) {
  if (diagnosticEvent.event === "connection_rebound") {
    state($("#connection-state"), "Connected", "live");
    rebindWaiter?.resolve();
    rebindWaiter = undefined;
    if (expectedRebind) {
      mark(expectedRebind, "pass", "Rebound with the original accepted task.");
      expectedRebind = undefined;
    }
  }
  const suffix = `${diagnosticEvent.sequence ? ` seq=${diagnosticEvent.sequence}` : ""}${diagnosticEvent.disposition ? ` ${diagnosticEvent.disposition}` : ""}`;
  log(`sdk ${diagnosticEvent.event}${suffix}`, diagnosticEvent.disposition === "transport_unavailable" ? "warn" : "info");
}

async function providerTaskHandler(task) {
  const run = fullPipelineRun;
  if (testProviderKey) {
    if (!run) {
      await task.reject("The isolated browser test provider only accepts an active harness scenario.");
      log("Rejected an unexpected isolated-provider assignment.", "warn");
      return;
    }
    try { await withTimeout(run.taskIdReady.promise, 8_000); }
    catch {
      await task.reject("The harness did not finish creating its expected task.");
      log("Rejected an assignment whose harness task was not created.", "warn");
      return;
    }
    if (run.taskId !== task.taskId) {
      await task.reject("The assignment does not match the active isolated-provider test task.");
      log("Rejected an assignment that did not match the active harness task.", "warn");
      return;
    }
  }

  activeTask = task;
  $("#task-id").textContent = task.taskId;
  $("#attempt-id").textContent = task.attemptId;
  state($("#task-state"), "Accepting", "alert");
  await task.accept();
  state($("#task-state"), "Accepted", "live");
  enableTaskControls(true);
  log("Assignment accepted.");

  if (run) {
    enableTaskControls(false);
    if (run.mode === "provider_failure") await executeProviderFailureTask(task, run);
    else if (run.mode === "requestor_cancellation") await executeRequestorCancellationTask(task, run);
    else await executeFullPipelineTask(task, run);
    return;
  }

  log("The handler is held open so reconnect behavior can be exercised.");
  await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true }));
  if (activeTask === task) {
    activeTask = null;
    enableTaskControls(false);
    state($("#task-state"), "Waiting", "idle");
    log("Assignment cancelled, closed, or recovery-expired.", "warn");
  }
}

async function connectProvider(apiUrl, key, enrollmentDefinition) {
  if (client) throw new Error("A provider session is already active.");
  try {
    state($("#connection-state"), "Connecting", "alert");
    transport = new BrowserWebSocketTransport(apiUrl, key);
    client = new ProviderClient(transport, { progressIntervalMs: 1_000, onDiagnostic: diagnostic });
    if (enrollmentDefinition) await client.enroll(enrollmentDefinition);
    await client.connect(providerTaskHandler);
    state($("#connection-state"), "Connected", "live");
    $("#connect").disabled = true;
    $("#disconnect").disabled = false;
    log("Connected. Waiting for an assignment.");
  } catch (error) {
    state($("#connection-state"), "Failed", "alert");
    log(safeFailure(error, "Connection failed."), "error");
    client = undefined;
    transport = undefined;
    throw error;
  }
}

$("#connect").addEventListener("click", async () => {
  const key = $("#provider-key").value;
  if (!key) return log("An API URL and provider key are required.", "error");
  try { await connectProvider(apiBaseUrl(), key); }
  catch { /* connection state and safe failure were already logged */ }
});

$("#disconnect").addEventListener("click", async () => {
  if (!transport) return;
  expectedRebind = "Manual ACR";
  await disconnectForAcr();
  log("Socket dropped. The active task should rebind without a second handler invocation.", "warn");
});

async function provisionTestProvider() {
  if (client) throw new Error("Disconnect the current provider before provisioning the isolated test provider.");
  state($("#full-pipeline-state"), "Provisioning", "alert");
  const issued = await apiJson("/api/webgpu-enrollments", { method: "POST" }, "Test-provider provisioning");
  if (typeof issued?.providerKey !== "string" || !issued.providerKey) throw new Error("The API did not return a test-provider credential.");
  testProviderKey = issued.providerKey;
  await connectProvider(apiBaseUrl(), testProviderKey, testProviderDefinition);
  $("#provision-test-provider").disabled = true;
  enableFullPipelineControls(true);
  state($("#full-pipeline-state"), "Ready", "live");
  log("Isolated test provider enrolled. Ready to create one full-pipeline task.");
}

function testInputImage() {
  return new Blob([tinyPng], { type: "image/png" });
}

async function testCapability() {
  return retry(async () => {
    const capabilities = await apiJson("/api/capabilities/", {}, "Capability catalogue");
    const capability = capabilities.find(item => item.name === testCapabilityName && item.machineAvailability?.some(machine => machine.computeTier === "Small" && machine.memoryGiB === 8 && machine.connectedCount > 0));
    if (!capability) throw new Error("The isolated test capability is not connected yet.");
    return capability;
  });
}

async function submitFullPipelineTask(run) {
  const landing = await apiFetch("/", { cache: "no-store" });
  if (!landing.ok) throw new Error(`Requestor session setup failed (${landing.status}).`);
  const capability = await testCapability();
  const form = new FormData();
  form.append("submission", JSON.stringify({
    capabilityId: capability.capabilityId,
    contractHash: capability.contractHash,
    scalars: { seed: run.seed },
    resources: { computeTier: "Small", memoryGiB: 8 },
    idempotencyKey: run.idempotencyKey
  }));
  form.append("image", testInputImage(), "harness-input.png");
  const response = await apiFetch("/api/tasks/", { method: "POST", body: form });
  if (!response.ok) throw new Error(`Task submission failed (${response.status}).`);
  const task = await response.json();
  if (typeof task?.taskId !== "string") throw new Error("Task submission did not return a task ID.");
  return task;
}

async function readCompletedResult(taskId) {
  return retry(async () => {
    const response = await apiFetch(`/api/tasks/${encodeURIComponent(taskId)}/result`, { cache: "no-store" });
    if (response.status === 409) throw new Error("Result is not available yet.");
    if (!response.ok) throw new Error(`Result lookup failed (${response.status}).`);
    const result = await response.json();
    const expected = new Set(["result", "metadata", "thumbnail", "preview", "logs"]);
    const actual = new Set(result?.artifacts?.map(artifact => artifact.name));
    if (expected.size !== actual.size || [...expected].some(name => !actual.has(name))) throw new Error("The completed result omitted an expected artifact.");
    return result;
  });
}

async function readTask(taskId) {
  const response = await apiFetch(`/api/tasks/${encodeURIComponent(taskId)}`, { cache: "no-store" });
  if (!response.ok) throw new Error(`Task lookup failed (${response.status}).`);
  return response.json();
}

async function waitForTaskStatus(taskId, status) {
  return retry(async () => {
    const task = await readTask(taskId);
    if (task.status !== status) throw new Error(`Task has not reached ${status} yet.`);
    return task;
  });
}

async function verifyResultDownloads(result) {
  await Promise.all(result.artifacts.map(async artifact => {
    const response = await fetch(artifact.downloadUrl, { cache: "no-store" });
    if (!response.ok) throw new Error(`Browser result download failed for ${artifact.name} (${response.status}).`);
    if ((await response.arrayBuffer()).byteLength === 0) throw new Error(`Browser result download was empty for ${artifact.name}.`);
  }));
}

async function expectRejectedProgressFault(name, task, send) {
  const rebind = deferred();
  rebindWaiter = rebind;
  expectedRebind = name;
  send();
  await withTimeout(rebind.promise, 20_000);
  mark(name, "pass", "Server rejected the invalid progress frame and ACR restored the accepted task.");
}

function finishFullPipelineRun(run) {
  run.done?.resolve(run.outcome ?? { passed: false, detail: "Scenario ended without a result." });
  if (fullPipelineRun === run) fullPipelineRun = undefined;
  enableFullPipelineControls(true);
}

function recordScenarioOutcome(run, passed, detail) {
  run.outcome = { passed, detail };
}

async function executeFullPipelineTask(task, run) {
  let stage = "accepting the isolated assignment";
  try {
    if (task.taskId !== run.taskId) throw new Error("The isolated provider received an unexpected assignment.");
    state($("#full-pipeline-state"), "Running", "alert");
    stage = "sending initial progress";
    await task.reportProgress({ phase: "e2e", percent: 5, message: "accepted by browser test provider" });
    stage = "recovering the accepted task after the forced socket drop";
    const rebind = deferred();
    rebindWaiter = rebind;
    transport.close();
    await task.reportProgress({ phase: "e2e", percent: 25, message: "newest progress retained through reconnect" });
    await withTimeout(rebind.promise, 20_000);
    stage = "sending progress after accepted-task recovery";
    await task.reportProgress({ phase: "e2e", percent: 50, message: "rebound with active task handle" });

    stage = "verifying duplicate progress is non-fatal";
    const raw = { taskId: task.taskId, attemptId: task.attemptId, taskHandle: task.taskHandle };
    transport.progress(raw, { sequenceNumber: 9_001, phase: "e2e-stale", percent: 60, message: "first high sequence" });
    transport.progress(raw, { sequenceNumber: 9_001, phase: "e2e-stale", percent: 61, message: "duplicate must be advisory" });
    await delay(150);
    await withTimeout(task.requestResultUpload(), 20_000);
    mark("Full pipeline duplicate progress", "pass", "Duplicate high sequence was non-fatal; an upload control still succeeded.");

    stage = "recovering from the deliberate wrong-handle fault";
    await expectRejectedProgressFault("Full pipeline wrong-handle fault", task, () => transport.progress(
      { ...raw, taskHandle: "intentionally-invalid-handle" },
      { sequenceNumber: 9_002, phase: "e2e-fault", percent: 62, message: "wrong handle" }
    ));
    stage = "recovering from the deliberate malformed-progress fault";
    await expectRejectedProgressFault("Full pipeline malformed-progress fault", task, () => transport.progress(
      raw,
      { sequenceNumber: 9_003, phase: "e2e-fault", percent: Number.NaN, message: "non-finite percent" }
    ));

    stage = "refreshing the browser input download";
    const inputUrl = await withTimeout(task.refreshInputDownload(), 20_000);
    const inputResponse = await fetch(inputUrl, { cache: "no-store" });
    if (!inputResponse.ok || (await inputResponse.arrayBuffer()).byteLength !== tinyPng.byteLength) throw new Error("Browser input download did not return the submitted image.");

    stage = "authorizing and uploading the result artifacts";
    const published = await withTimeout(task.uploadResult({
      resultZip: emptyZip,
      metadata: { harness: "provider-reconnect", seed: run.seed },
      thumbnail: testInputImage(),
      preview: testInputImage(),
      logs: "MutualGPU browser full-pipeline regression result."
    }), 30_000);
    stage = "completing the staged result";
    await withTimeout(task.complete(published.receipt), 20_000);
    activeTask = null;
    enableTaskControls(false);
    state($("#task-state"), "Completed", "live");

    stage = "retrieving the completed result";
    const result = await readCompletedResult(run.taskId);
    await verifyResultDownloads(result);
    mark("Full browser pipeline", "pass", "Task, ACR, input, upload, completion, and browser result retrieval succeeded.");
    recordScenarioOutcome(run, true, "Task, ACR, data paths, and result retrieval passed.");
    state($("#full-pipeline-state"), "Passed", "live");
    log("Full pipeline passed: requestor task through result retrieval.");
  } catch (error) {
    const detail = safeFailure(error, `Failed during ${stage}`);
    mark("Full browser pipeline", "fail", detail);
    recordScenarioOutcome(run, false, detail);
    state($("#full-pipeline-state"), "Failed", "alert");
    log(safeFailure(error, "Full pipeline failed."), "error");
    throw error;
  } finally {
    finishFullPipelineRun(run);
  }
}

async function executeProviderFailureTask(task, run) {
  try {
    await task.reportProgress({ phase: "terminal-failure", percent: 10, message: "intentional terminal failure probe" });
    await task.fail("content_safety", "Intentional browser-harness terminal failure.");
    activeTask = null;
    enableTaskControls(false);
    const completed = await waitForTaskStatus(run.taskId, "Failed");
    if (completed.failureStep !== "content_safety") throw new Error("The failed task did not preserve the expected failure step.");
    mark("Provider terminal failure", "pass", "Provider failure reached the expected terminal task state.");
    recordScenarioOutcome(run, true, "Terminal provider failure reached the expected task state.");
    state($("#full-pipeline-state"), "Failure verified", "live");
    log("Provider terminal-failure scenario passed.");
  } catch (error) {
    const detail = safeFailure(error, "Failed");
    mark("Provider terminal failure", "fail", detail);
    recordScenarioOutcome(run, false, detail);
    state($("#full-pipeline-state"), "Failed", "alert");
    log(safeFailure(error, "Provider terminal-failure scenario failed."), "error");
    throw error;
  } finally {
    finishFullPipelineRun(run);
  }
}

async function executeRequestorCancellationTask(task, run) {
  try {
    const cancellation = await apiFetch(`/api/tasks/${encodeURIComponent(run.taskId)}`, { method: "DELETE" });
    if (cancellation.status !== 204) throw new Error(`Requestor cancellation failed (${cancellation.status}).`);
    if (!task.signal.aborted) await withTimeout(new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })), 12_000);
    activeTask = null;
    enableTaskControls(false);
    const completed = await waitForTaskStatus(run.taskId, "Cancelled");
    if (completed.failureStep !== "requestor_cancelled") throw new Error("The cancelled task did not preserve the expected cancellation reason.");
    mark("Requestor cancellation", "pass", "Matching cancellation aborted the SDK task and reached the expected terminal state.");
    recordScenarioOutcome(run, true, "Matching cancellation aborted the SDK task and reached the expected task state.");
    state($("#full-pipeline-state"), "Cancellation verified", "live");
    log("Requestor-cancellation scenario passed.");
  } catch (error) {
    const detail = safeFailure(error, "Failed");
    mark("Requestor cancellation", "fail", detail);
    recordScenarioOutcome(run, false, detail);
    state($("#full-pipeline-state"), "Failed", "alert");
    log(safeFailure(error, "Requestor-cancellation scenario failed."), "error");
    throw error;
  } finally {
    finishFullPipelineRun(run);
  }
}

$("#provision-test-provider").addEventListener("click", async () => {
  $("#provision-test-provider").disabled = true;
  try { await provisionTestProvider(); }
  catch (error) {
    state($("#full-pipeline-state"), "Failed", "alert");
    log(safeFailure(error, "Test-provider provisioning failed."), "error");
    $("#provision-test-provider").disabled = false;
  }
});

async function startIsolatedScenario(mode, label) {
  if (!client || !testProviderKey) return log("Provision the isolated test provider first.", "warn");
  if (fullPipelineRun) return log("A full-pipeline run is already in progress.", "warn");
  enableFullPipelineControls(false);
  const run = fullPipelineRun = {
    mode,
    idempotencyKey: `browser-pipeline-${crypto.randomUUID()}`,
    seed: crypto.randomUUID(),
    taskIdReady: deferred(),
    done: deferred()
  };
  try {
    const task = await submitFullPipelineTask(run);
    run.taskId = task.taskId;
    run.taskIdReady.resolve();
    log(`${label} task submitted. Waiting for the isolated provider assignment.`);
  } catch (error) {
    run.taskIdReady.reject(error);
    recordScenarioOutcome(run, false, safeFailure(error, "Task submission failed"));
    run.done.resolve(run.outcome);
    fullPipelineRun = undefined;
    enableFullPipelineControls(true);
    state($("#full-pipeline-state"), "Failed", "alert");
    mark(label, "fail", safeFailure(error, "Task submission failed"));
    log(safeFailure(error, `${label} setup failed.`), "error");
  }
  return run.done.promise;
}

function releaseIsolatedTestProvider() {
  client?.close();
  client = undefined;
  transport = undefined;
  activeTask = undefined;
  testProviderKey = undefined;
  $("#connect").disabled = false;
  $("#disconnect").disabled = true;
}

async function runFullCertification() {
  if (certificationRun) return log("A full certification is already in progress.", "warn");
  certificationRun = {};
  enableFullPipelineControls(false);
  state($("#full-pipeline-state"), "Certifying", "alert");
  const outcomes = [];
  try {
    if (client) releaseIsolatedTestProvider();
    const sdkResults = await runSdkContractChecks();
    outcomes.push({ name: "SDK contract suite", passed: sdkResults.every(result => result.passed), detail: `${sdkResults.filter(result => result.passed).length}/${sdkResults.length} passed` });

    for (const [mode, label] of [
      ["full", "Full browser pipeline"],
      ["provider_failure", "Provider terminal failure"],
      ["requestor_cancellation", "Requestor cancellation"]
    ]) {
      if (!client) await provisionTestProvider();
      let outcome;
      try { outcome = await withTimeout(startIsolatedScenario(mode, label), 70_000); }
      catch { outcome = { passed: false, detail: "Scenario timed out waiting for its expected event." }; }
      outcomes.push({ name: label, ...outcome });
      if (!outcome.passed) releaseIsolatedTestProvider();
    }

    const failed = outcomes.filter(outcome => !outcome.passed);
    mark("Full certification", failed.length ? "fail" : "pass", failed.length
      ? `${failed.map(outcome => `${outcome.name}: ${outcome.detail}`).join(" | ")}`
      : "SDK, success, failure, and cancellation scenarios all met their expected outcomes.");
    state($("#full-pipeline-state"), failed.length ? "Failed" : "Passed", failed.length ? "alert" : "live");
    log(`Full certification completed: ${outcomes.length - failed.length}/${outcomes.length} scenarios passed.`, failed.length ? "error" : "info");
  } catch (error) {
    mark("Full certification", "fail", safeFailure(error, "Certification setup failed"));
    state($("#full-pipeline-state"), "Failed", "alert");
    log(safeFailure(error, "Full certification failed."), "error");
  } finally {
    certificationRun = undefined;
    enableFullPipelineControls(true);
  }
}

$("#run-full-certification").addEventListener("click", () => { void runFullCertification(); });

$("#run-full-pipeline").addEventListener("click", () => {
  void startIsolatedScenario("full", "Full browser pipeline");
});

$("#run-provider-failure").addEventListener("click", () => {
  void startIsolatedScenario("provider_failure", "Provider terminal failure");
});

$("#run-requestor-cancellation").addEventListener("click", () => {
  void startIsolatedScenario("requestor_cancellation", "Requestor cancellation");
});

$("#burst-progress").addEventListener("click", async () => {
  try {
    const task = requireTask();
    const sent = [];
    for (let percent = 10; percent <= 100; percent += 10) sent.push(await task.reportProgress({ phase: "burst-test", percent, message: `burst value ${percent}` }));
    const immediate = sent.filter(Boolean).length;
    mark("Progress burst", "pass", `${immediate} immediate send; newest value retained.`);
    log(`Progress burst completed: ${immediate} immediate send(s), latest value coalesced.`);
  } catch (error) { mark("Progress burst", "fail", safeFailure(error, "Failed")); log(safeFailure(error, "Progress burst failed."), "error"); }
});

$("#buffer-progress").addEventListener("click", async () => {
  try {
    const task = requireTask();
    await task.reportProgress({ phase: "reconnect-test", percent: 41, message: "before forced disconnect" });
    await task.reportProgress({ phase: "reconnect-test", percent: 42, message: "newest buffered value" });
    expectedRebind = "Buffered progress ACR";
    await disconnectForAcr();
    mark("Buffered progress ACR", "warn", "Waiting for reconnect and progress_sent diagnostics.");
    log("Progress 42 is buffered. Confirm a later progress_sent seq event after rebind.", "warn");
  } catch (error) { mark("Buffered progress ACR", "fail", safeFailure(error, "Failed")); log(safeFailure(error, "Reconnect test failed."), "error"); }
});

$("#refresh-input").addEventListener("click", async () => {
  try { const url = await requireTask().refreshInputDownload(); log(`Input refresh succeeded (${new URL(url).host}).`); }
  catch (error) { log(safeFailure(error, "Input refresh failed."), "error"); }
});

$("#request-upload").addEventListener("click", async () => {
  try { await requireTask().requestResultUpload(); log("Result-upload authorization succeeded. Token redacted."); }
  catch (error) { log(safeFailure(error, "Upload authorization failed."), "error"); }
});

async function runAcrControl(name, operation) {
  try {
    expectedRebind = name;
    await disconnectForAcr();
    const started = performance.now();
    await withTimeout(operation());
    const elapsed = Math.round(performance.now() - started);
    mark(name, "pass", `Control resumed after rebind (${elapsed} ms).`);
    log(`${name} passed: the control waited for reconnection instead of failing.`);
  } catch (error) {
    mark(name, "fail", safeFailure(error, "Failed"));
    log(safeFailure(error, `${name} failed.`), "error");
  }
}

$("#acr-refresh").addEventListener("click", () => runAcrControl("ACR input barrier", () => requireTask().refreshInputDownload()));
$("#acr-upload").addEventListener("click", () => runAcrControl("ACR upload barrier", () => requireTask().requestResultUpload()));

$("#stale-progress").addEventListener("click", async () => {
  try {
    const task = requireTask();
    const raw = { taskId: task.taskId, attemptId: task.attemptId, taskHandle: task.taskHandle };
    transport.progress(raw, { sequenceNumber: 9_001, phase: "raw-stale-probe", percent: 50, message: "first high sequence" });
    transport.progress(raw, { sequenceNumber: 9_001, phase: "raw-stale-probe", percent: 51, message: "duplicate sequence" });
    await delay(150);
    await withTimeout(task.requestResultUpload());
    mark("Duplicate/stale progress", "pass", "Duplicate was non-fatal; another control succeeded.");
    log("Duplicate/stale progress probe passed. Start a new task before using SDK progress again.");
  } catch (error) {
    mark("Duplicate/stale progress", "fail", safeFailure(error, "Failed"));
    log(safeFailure(error, "Duplicate/stale progress probe failed."), "error");
  }
});

async function destructiveProgressProbe(name, update) {
  try {
    const task = requireTask();
    expectedRebind = name;
    transport.progress({ taskId: task.taskId, attemptId: task.attemptId, taskHandle: update.handle ?? task.taskHandle }, update.value);
    mark(name, "warn", "Invalid frame sent; waiting for expected close and rebind.");
    log(`${name}: invalid raw frame sent. The current connection may close before it rebinds.`, "warn");
  } catch (error) {
    mark(name, "fail", safeFailure(error, "Failed"));
    log(safeFailure(error, `${name} failed.`), "error");
  }
}

$("#wrong-handle").addEventListener("click", () => destructiveProgressProbe("Wrong-handle rejection", {
  handle: "intentionally-invalid-handle",
  value: { sequenceNumber: 1, phase: "negative-probe", percent: 1, message: "wrong handle" }
}));

$("#malformed-progress").addEventListener("click", () => destructiveProgressProbe("Malformed progress rejection", {
  value: { sequenceNumber: 1, phase: "negative-probe", percent: Number.NaN, message: "non-finite percent" }
}));

$("#retry-completion").addEventListener("click", async () => {
  const receipt = $("#completion-receipt").value;
  if (!receipt) return log("Enter the existing upload receipt from the acknowledgement-loss test.", "warn");
  try {
    await withTimeout(requireTask().complete(receipt));
    mark("Completion retry", "pass", "Same receipt was accepted after the lost acknowledgement.");
    log("Completion retry succeeded with the existing receipt.");
    await finishTaskUi();
  } catch (error) {
    mark("Completion retry", "fail", safeFailure(error, "Failed"));
    log(safeFailure(error, "Completion retry failed."), "error");
  }
});

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function runSdkContractChecks() {
  const results = [];
  const report = (name, passed, detail) => { results.push({ name, passed, detail }); mark(`SDK: ${name}`, passed ? "pass" : "fail", detail); };
  try {
    const frames = [];
    let receive;
    const fake = {
      connect: async callback => { receive = callback; }, accept: async () => {}, reject: async () => {}, fail: async () => {}, complete: async () => {},
      progress: async (_task, update) => { frames.push(update); }, refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
    };
    const probe = new ProviderClient(fake, { progressIntervalMs: 1 });
    await probe.connect(async task => { await task.accept(); await task.reportProgress({ percent: 1 }); await task.reportProgress({ percent: 2 }); await task.reportProgress({ percent: 3 }); await delay(5); await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })); });
    const handling = receive({ taskId: "sdk", attemptId: "burst", taskHandle: "opaque" });
    await delay(12);
    assert(frames.at(-1)?.percent === 3 && frames.length <= 2, "latest-value coalescing did not retain only the newest progress");
    probe.close(); await handling;
    report("latest-value progress", true, "Newest buffered value was the only delayed frame.");
  } catch (error) { report("latest-value progress", false, safeFailure(error, "Failed")); }

  try {
    const handles = []; let receive; let disconnect; let calls = 0;
    const fake = {
      connect: async (callback, handle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(handle); }, accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {}, complete: async () => {}, refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
    };
    const probe = new ProviderClient(fake, { reconnectDelay: () => 0 });
    await probe.connect(async task => { calls += 1; await task.accept(); await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })); });
    const handling = receive({ taskId: "sdk", attemptId: "acr", taskHandle: "active" }); await delay(1); disconnect(); await delay(12);
    assert(handles.join(",") === ",active" && calls === 1, "ACR did not preserve the handle and one handler invocation");
    probe.close(); await handling;
    report("accepted-task recovery", true, "Rebind used the active handle and did not restart the handler.");
  } catch (error) { report("accepted-task recovery", false, safeFailure(error, "Failed")); }

  try {
    const handles = []; const progress = []; let receive; let disconnect; let finish;
    const fake = {
      connect: async (callback, handle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(handle); }, accept: async () => {}, reject: async () => {}, fail: async () => {}, complete: async () => {},
      progress: async (_task, update) => { progress.push(update); }, refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
    };
    const probe = new ProviderClient(fake, { reconnectDelay: () => 0, progressIntervalMs: 1 });
    await probe.connect(async task => {
      await task.accept();
      disconnect(new Error("network lost"));
      assert(await task.reportProgress({ percent: 55 }) === false, "progress was not buffered during reconnect");
      assert(await task.refreshInputDownload() === "https://objects.example/input", "input control did not wait for rebind");
      await new Promise(resolve => { finish = resolve; });
      await task.complete("receipt");
    });
    const handling = receive({ taskId: "sdk", attemptId: "progress-reconnect", taskHandle: "active" });
    await delay(12);
    assert(handles.join(",") === ",active" && progress[0]?.percent === 55 && typeof finish === "function", "progress reconnect lost the active task or newest value");
    finish(); await handling; probe.close();
    report("reconnect-time progress", true, "Progress buffered, rebind retained ownership, and the control resumed.");
  } catch (error) { report("reconnect-time progress", false, safeFailure(error, "Failed")); }

  try {
    const handles = []; let receive; let disconnect; let completionCalls = 0;
    const fake = {
      connect: async (callback, handle, onDisconnect) => { receive = callback; disconnect = onDisconnect; handles.push(handle); }, accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {},
      refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token",
      complete: async () => { completionCalls += 1; if (completionCalls === 1) { disconnect(new Error("completion acknowledgement lost")); throw new Error("completion acknowledgement lost"); } }, close: () => {}
    };
    const probe = new ProviderClient(fake, { reconnectDelay: () => 0 });
    await probe.connect(async task => {
      await task.accept();
      try { await task.complete("same-receipt"); } catch { /* acknowledgement loss is the expected first outcome */ }
      await task.complete("same-receipt");
    });
    await receive({ taskId: "sdk", attemptId: "completion-replay", taskHandle: "active" });
    assert(handles.join(",") === ",active" && completionCalls === 2, "completion was not retried with the original receipt after acknowledgement loss");
    probe.close();
    report("completion acknowledgement replay", true, "The same receipt remained retryable after a disconnected acknowledgement.");
  } catch (error) { report("completion acknowledgement replay", false, safeFailure(error, "Failed")); }

  try {
    let receive; let disconnect; let clock = 0; let terminalSend = false;
    const fake = {
      connect: async (callback, _handle, onDisconnect) => {
        if (!receive) { receive = callback; disconnect = onDisconnect; return; }
        throw new Error("still unavailable");
      },
      accept: async () => {}, reject: async () => { terminalSend = true; }, progress: async () => {}, fail: async () => { terminalSend = true; }, complete: async () => {},
      refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
    };
    const probe = new ProviderClient(fake, { reconnectDelay: () => 0, recoveryDeadlineMs: 3, now: () => clock++ });
    await probe.connect(async task => { await task.accept(); await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })); });
    const handling = receive({ taskId: "sdk", attemptId: "recovery-expiry", taskHandle: "active" });
    await delay(1); disconnect(new Error("network lost")); await handling;
    assert(!terminalSend, "recovery expiry reported a second terminal handler error");
    probe.close();
    report("recovery deadline expiry", true, "The active task aborted cleanly when reconnect could not recover it.");
  } catch (error) { report("recovery deadline expiry", false, safeFailure(error, "Failed")); }

  try {
    let receive; let cancel; let signal;
    const fake = {
      connect: async (callback, _handle, _disconnect, onCancellation) => { receive = callback; cancel = onCancellation; }, accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {}, complete: async () => {}, refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", close: () => {}
    };
    const probe = new ProviderClient(fake);
    await probe.connect(async task => { await task.accept(); signal = task.signal; await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })); });
    const handling = receive({ taskId: "sdk", attemptId: "cancel", taskHandle: "active" }); await delay(1);
    cancel({ taskId: "other", attemptId: "cancel", taskHandle: "active" }); assert(!signal.aborted, "a non-matching cancellation aborted the active task");
    cancel({ taskId: "sdk", attemptId: "cancel", taskHandle: "active" }); await handling; assert(signal.aborted, "matching cancellation did not abort the active task");
    report("matching cancellation", true, "Only the exact task identity cancelled the active handler.");
  } catch (error) { report("matching cancellation", false, safeFailure(error, "Failed")); }

  try {
    let receive; let disconnect; let disposition;
    const fake = {
      connect: async (callback, _handle, onDisconnect) => { receive = callback; disconnect = onDisconnect; }, accept: async () => {}, reject: async () => {}, progress: async () => {}, fail: async () => {}, complete: async () => {}, refreshInputDownload: async () => "https://objects.example/input", requestResultUpload: async () => "token", uploadResult: async () => { disconnect(); throw new Error("transport interrupted"); }, close: () => {}
    };
    const probe = new ProviderClient(fake, { reconnectDelay: () => 0 });
    await probe.connect(async task => { await task.accept(); try { await task.uploadResult({ resultZip: new Uint8Array([1]) }); } catch (error) { disposition = error.code; } await new Promise(resolve => task.signal.addEventListener("abort", resolve, { once: true })); });
    const handling = receive({ taskId: "sdk", attemptId: "upload", taskHandle: "active" }); await delay(12);
    assert(disposition === "upload_outcome_unknown", "ambiguous upload did not report its stable disposition");
    probe.close(); await handling;
    report("ambiguous upload", true, "Interrupted upload produced upload_outcome_unknown, not an automatic retry.");
  } catch (error) { report("ambiguous upload", false, safeFailure(error, "Failed")); }

  const originalWebSocket = globalThis.WebSocket;
  try {
    const sockets = [];
    class FakeSocket {
      constructor() { this.sent = 0; sockets.push(this); queueMicrotask(() => this.onopen?.()); }
      send() { this.sent += 1; queueMicrotask(() => this.onmessage?.({ data: new Uint8Array([1]).buffer })); }
      close() { this.onclose?.(); }
    }
    globalThis.WebSocket = FakeSocket;
    const codec = { encodeProvider: () => new Uint8Array([1]), decodeServer: () => ({ connected: { executionUnitId: "unit" } }) };
    const probe = new BrowserWebSocketTransport("wss://sdk.example/provider/connect", "key", codec, "https://sdk.example/");
    await probe.connect(async () => {}); const first = sockets[0]; first.onclose(); await probe.connect(async () => {}); const replacement = sockets[1]; first.onclose(); probe.accept({ taskId: "task", attemptId: "attempt", taskHandle: "handle" });
    assert(replacement.sent >= 2, "a late close callback displaced the replacement socket");
    report("stale socket callback", true, "Late old-socket cleanup left the replacement session usable.");
  } catch (error) { report("stale socket callback", false, safeFailure(error, "Failed")); }
  finally { globalThis.WebSocket = originalWebSocket; }
  log(`SDK contract suite: ${results.filter(result => result.passed).length}/${results.length} passed.`, results.every(result => result.passed) ? "info" : "error");
  return results;
}

$("#run-sdk-checks").addEventListener("click", async () => {
  if (client) return log("Disconnect the live provider before running isolated SDK contract checks.", "warn");
  $("#run-sdk-checks").disabled = true;
  try { await runSdkContractChecks(); } finally { $("#run-sdk-checks").disabled = false; }
});

$("#clear-log").addEventListener("click", () => { $("#events").replaceChildren(); });
