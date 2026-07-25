# Provider reconnect, progress, and browser recovery remediation plan

Status: proposed  
Incident reviewed: 2026-07-25  
SDK artifact reviewed: `mutualgpu-browser-sdk-cancellation-preview-2026-07-23.tgz`  
Artifact SHA-256: `41156658c891b0e0af179c1c1c07f4745ec188f9a247f3e9d5b9e3de7b267698`

## Executive decisions

1. Progress that is stale, duplicated, or too frequent is advisory data and must
   never terminate a provider session.
2. The browser SDK will use a bounded, one-slot, latest-value-wins progress
   buffer. It will not build an unbounded progress queue.
3. MutualGPU currently trusts the supported SDK implementations. The server will
   remove its per-task one-second arrival-time check. Frame-size, text-size, and
   connection-level abuse controls remain server responsibilities.
4. Task control operations will wait for successful rebind instead of failing
   immediately while the transport is reconnecting.
5. A transient transport failure must not clear the active assignment or its
   handle.
6. Browser recovery state will be stored in an opt-in, origin-scoped IndexedDB
   journal so a trusted provider browser can recover after a tab refresh or
   restart. `localStorage` is not suitable for the journal because it is
   synchronous, unstructured, and difficult to update atomically.
7. Invalid ownership, invalid state, stale sequence, rate limiting, malformed
   data, and internal errors will have distinct result codes, logs, metrics, and
   transport behavior.

## What the investigation established

### Can progress currently close the session for being too fast?

Yes. The server's progress registry returns `false` when either:

- the sequence is not greater than the last retained sequence; or
- two updates arrive less than one server-observed second apart.

Both WSS and gRPC map that `false` result to an invalid-task-handle failure. WSS
closes with policy code `1008`. Therefore a too-fast update can close the
session today.

This is a credible explanation for an observed close, but existing telemetry
cannot prove which predicate caused a particular close because all predicates
share one result and one close reason.

### What the current SDK does

The SDK drops a call made less than one client-observed second after the
previous send. It does not retain the newest dropped value, despite the design
documentation describing latest-value-wins coalescing.

Client-side spacing does not guarantee server-side spacing. For example, two
sends exactly 1,000 ms apart can arrive 980 ms apart when the first frame has
more network latency than the second.

### Additional sequence and recovery findings

1. Retained progress is keyed by logical task ID, not attempt ID. Progress is
   removed on completion and requestor cancellation, but not on every rejection,
   failure, revocation, acknowledgement timeout, or delivery-requeue path. A new
   attempt starts its sequence at `1` and can therefore be rejected as stale
   relative to an earlier attempt.
2. WSS and gRPC collapse progress drops and authorization/state failures into
   the same task-handle error.
3. Accepted progress is not included in `ProviderMessageLogging`, and rejected
   messages are not logged with a classified reason.
4. SDK control waiters are FIFO and protocol responses do not consistently
   contain request correlation IDs. Concurrent derivative implementations could
   associate a response with the wrong call.
5. Completion consumes an in-memory staged receipt. If completion succeeds but
   its acknowledgement is lost, an identical retry is reported as invalid.
6. Upload tokens and staged results are process-local. A service restart makes
   an in-progress or ambiguously completed upload unrecoverable.
7. Browser protobuf `uint64` values are converted to JavaScript `number`.
   Sequences above `Number.MAX_SAFE_INTEGER` lose precision.
8. Attempt event filenames use `count + 1`. The current single-task ECS service
   and process-local locks reduce exposure, but the algorithm is unsafe if the
   service scales to concurrent writers.
9. Rebind does not explicitly move attempt diagnostics to the replacement
   session, which can make the admin timeline show the original disconnect while
   omitting later rebound activity.
10. Reconnect recovery is scoped per execution unit and checks on a fixed delay
    sequence. During repeated flapping, an older recovery sequence can continue;
    the implementation does not explicitly grant a fresh recovery window from
    the latest successful rebind and subsequent disconnect.
11. `RevokeExpired` currently returns `false` even when it changed tasks, which
    weakens callers' ability to report or react to acknowledgement revocations.
12. The real-browser SDK integration test covers enrollment and the initial WSS
    handshake, but not accepted-task reconnect, progress, upload, completion, or
    tab restoration.

## Delivery plan

### Phase 0: stop false provider disconnects

Priority: P0  
Owners: API, application state, SDK  
Deployment order: server first, SDK artifact second

#### 0.1 Classify progress outcomes

Replace the Boolean progress result with a closed result type:

- `Accepted`
- `DroppedStaleSequence`
- `DroppedSuperseded`
- `RejectedUnknownAssignment`
- `RejectedWrongExecutionUnit`
- `RejectedWrongHandle`
- `RejectedAttemptState`
- `RejectedMalformed`

Do not infer authorization failure from a generic `false`.

Acceptance criteria:

- Every caller handles all result variants exhaustively.
- WSS and gRPC apply identical mappings.
- No handle, token, URL, provider key, or scalar value appears in a result,
  log, metric, or close reason.

#### 0.2 Make advisory progress non-fatal

- Remove the server's per-task one-second arrival-time predicate.
- Retain monotonic sequence validation.
- Silently ignore stale or duplicate progress and keep the connection open.
- Keep the 64 KiB WSS frame bound.
- Add explicit bounds for phase and message text and require finite progress
  percentages.
- If connection-level abuse protection is later required, use a bounded token
  bucket that drops progress and records a metric; do not represent it as an
  authorization failure.

Acceptance criteria:

- A duplicate, stale, or burst progress update never closes WSS or gRPC.
- Completion, failure, cancellation, refresh, and upload authorization remain
  responsive during a progress burst.
- A genuinely wrong handle remains rejected.

#### 0.3 Scope progress to an attempt

- Key retained progress by `(TaskId, AttemptId)`.
- Include attempt ID when reading progress for the requestor projection, or
  return only the active attempt's progress.
- Clear retained progress on every terminal or requeue transition:
  completion, cancellation, provider rejection, provider failure, disconnect
  recovery revocation, acknowledgement timeout, delivery failure, startup
  recovery, and attempt replacement.

Acceptance criteria:

- Attempt B sequence `1` is accepted after attempt A ended at any sequence.
- Progress from an old attempt can never appear on the current attempt.
- Unit tests cover every transition that removes an assignment.

#### 0.4 Implement true latest-value-wins SDK coalescing

Use one pending progress slot per active assignment:

1. `reportProgress(update)` replaces the pending value.
2. A flusher sends only when the task is accepted and the transport is rebound.
3. At most one progress frame is sent per configured client interval.
4. A new call replaces, rather than appends behind, the pending value.
5. Progress pending during a disconnect remains pending.
6. After rebind, the newest pending value is flushed.
7. Terminal state or cancellation discards the pending value.

Sequence allocation:

- Persist the next sequence before each send attempt.
- Never decrement or reset it for the same attempt.
- If send outcome is ambiguous, a retry may use a new, higher sequence; the
  server will harmlessly ignore any older duplicate.
- A restored tab resumes above the journaled sequence.

API compatibility:

- For the preview SDK, retain the Boolean return temporarily.
- `true` means sent immediately.
- `false` means coalesced for possible later delivery, not necessarily lost.
- Document that callers must not depend on delivery of an individual progress
  call.
- Plan a later structured disposition result if consumers need more detail.

Acceptance criteria:

- Ten calls in one second produce at most one frame containing the newest value.
- A disconnect during the interval produces no task failure.
- Rebind flushes only the newest buffered value.
- The buffer remains constant-size.

#### 0.5 Phase 0 diagnostics

Server structured logs:

- `event`: `provider_progress_disposition`
- `transport`: `wss` or `grpc`
- `session_id`
- `connection_epoch`
- `task_id`
- `attempt_id`
- `execution_unit_id`
- `disposition`
- `reason_code`
- `received_sequence`
- `previous_sequence`, when present
- `arrival_delta_ms`, when relevant during rollout
- `attempt_state`

SDK diagnostic callback:

- `sdk_build`
- `event`
- `connection_epoch`
- `task_id`
- `attempt_id`
- `sequence`
- `disposition`
- `reconnect_attempt`
- `buffer_occupied`

The SDK callback must redact the task handle, provider key, upload token,
presigned URL, scalar inputs, and exception bodies that may contain them.

Metrics:

- `mutualgpu.provider.progress.received`
- `mutualgpu.provider.progress.accepted`
- `mutualgpu.provider.progress.dropped`, tagged only by reason and transport
- `mutualgpu.provider.session.closed`, tagged only by classified reason and
  transport

Do not use task, attempt, session, or execution-unit IDs as metric tags.

### Phase 1: reconnect-safe task control

Priority: P0 for active-handle preservation; P1 for the complete control surface  
Owners: SDK core, browser transport, Node transport

#### 1.1 Add a reconnect barrier

Represent connection state explicitly:

- `connecting`
- `connected_idle`
- `connected_active`
- `disconnected_active`
- `reconnecting_active`
- `terminal`
- `closed`

While `reconnecting_active`:

- progress remains in its one-slot buffer;
- input refresh waits for rebind;
- result-upload authorization waits for rebind;
- completion waits for rebind;
- failure waits for rebind;
- cancellation from the server remains terminal whenever received.

Each wait must be bounded by:

- provider shutdown;
- requestor cancellation;
- server rejection of the rebind;
- the configured recovery deadline.

Acceptance criteria:

- Control calls made during backoff do not throw a generic disconnected-session
  error.
- Waiting calls resume after a successful rebind.
- Rebind rejection returns a stable terminal SDK error.

#### 1.2 Preserve active state across transport errors

- Never clear `#active` because a control send failed transiently.
- Do not attempt to report handler failure until the transport is rebound.
- Clear active state only after:
  - completion acknowledgement;
  - confirmed failure send;
  - confirmed rejection;
  - matching requestor cancellation;
  - definitive rebind rejection or recovery expiry;
  - explicit provider shutdown policy.
- Ensure only one reconnect loop and one connection epoch can mutate active
  state.

Acceptance criteria:

- A transport error followed by reconnect always presents the original active
  handle.
- A stale socket callback cannot clear or disconnect its replacement socket.
- Existing handler code is not invoked twice within one live tab.

#### 1.3 Do not blindly replay ambiguous uploads

Buffering is safe for progress, but not for a single-use upload:

- Before upload authorization, wait for rebind normally.
- If the multipart request never began, request a fresh token after rebind.
- If transmission began but no response arrived, mark `upload_outcome_unknown`.
- Do not automatically upload the bytes again.
- Add a server reconciliation/status operation before enabling automatic
  recovery of an ambiguous upload.

Acceptance criteria:

- No upload token is replayed.
- The SDK exposes a stable `upload_outcome_unknown` error.
- Partner UI tells the operator that server reconciliation is required instead
  of reporting a generic inference failure.

#### 1.4 Make completion idempotent

- Retain the accepted receipt/result identity long enough to answer an identical
  completion retry.
- Completing an already completed attempt with the same receipt returns the
  original success.
- A different receipt remains rejected.

Acceptance criteria:

- Losing the completion acknowledgement and retrying after reconnect succeeds.
- A conflicting completion cannot replace the stored result.

### Phase 2: browser-local recovery journal

Priority: P1  
Owners: SDK core, browser SDK, provider UI, API protocol

#### 2.1 Storage and security model

Use IndexedDB as browser-local storage with one atomic record per execution
unit. Add a small `localStorage` marker only if startup discovery requires it;
the authoritative journal remains IndexedDB.

This is opt-in for a trusted provider device because the provider identity and
active task handle are authorization capabilities. Browser storage does not
protect against same-origin script compromise.

Required controls:

- strict Content Security Policy with no untrusted third-party script;
- origin isolation for the provider application;
- no handle, key, token, URL, prompt, or scalar in logs;
- short journal expiry aligned with server recovery policy;
- purge on terminal state, explicit disconnect, invalid rebind, or expiry;
- a visible “remember this provider on this device” choice;
- a visible “forget provider and recovery data” action;
- Web Locks API ownership so only one tab executes for an execution unit;
- BroadcastChannel coordination for tab takeover and status display.

Do not store upload tokens or presigned URLs.

#### 2.2 Journal schema

Version the record and include:

- schema version;
- SDK build identifier;
- provider-identity fingerprint;
- task ID;
- attempt ID;
- active task handle or a future scoped recovery credential;
- accepted lifecycle state;
- sanitized assignment data needed to restart work;
- input artifact metadata without its presigned URL;
- next progress sequence;
- newest pending progress value;
- upload state: `not_started`, `authorization_pending`,
  `upload_outcome_unknown`, `receipt_obtained`, or `completion_pending`;
- receipt only when an upload was confirmed;
- accepted-at, last-updated-at, and expires-at timestamps;
- connection epoch.

Scalar inputs may contain private requestor data. Prefer a protocol-level
rebind snapshot so the server can resend the current assignment after
authentication. Until that exists, persisting scalar inputs requires explicit
trusted-device consent and the same purge policy as the handle.

#### 2.3 Atomic lifecycle

Before sending `TaskAccepted`:

1. write the assignment and intended accepted state to the journal;
2. send acceptance;
3. mark acceptance confirmed when subsequent server activity proves continuity.

For every sequence allocation:

1. increment and commit the journaled next sequence;
2. attempt the send;
3. retain only the newest pending progress value.

On terminal confirmation:

1. mark terminal;
2. stop the workload;
3. delete the journal record transactionally.

#### 2.4 Tab restoration

On startup:

1. acquire the execution-unit Web Lock;
2. load and validate the journal;
3. discard expired or incompatible records;
4. authenticate the provider;
5. connect with the stored recovery handle;
6. wait for explicit rebind success;
7. refresh the input URL if needed;
8. reconstruct an accepted task facade;
9. resume from an application checkpoint when available, otherwise restart the
   deterministic workload for the same attempt;
10. flush the newest buffered progress above the persisted sequence.

A browser restart destroys active WebGPU execution. The journal preserves task
ownership and restart information; it does not preserve GPU memory. Workloads
that need true continuation must implement their own checkpoint format.

If rebind is rejected because the requestor cancelled, recovery expired, or the
service revoked the attempt, purge the journal and do not restart computation.

Acceptance criteria:

- Refreshing the tab during an accepted task reconnects with the same attempt.
- Sequence numbers do not reset.
- Only one tab resumes the workload.
- Cancellation while the tab is closed prevents restoration.
- Expired recovery data is removed automatically.
- No upload is duplicated after an ambiguous browser shutdown.

#### 2.5 Consider a scoped recovery credential

Follow-on protocol design should replace durable storage of the raw task handle
with a short-lived, provider-bound recovery credential. The server exchanges it
for current assignment state only after provider authentication.

This reduces the authority stored in the browser and allows the server to:

- revoke browser recovery independently;
- return an explicit rebind result;
- resend sanitized assignment state;
- communicate the recovery deadline;
- distinguish cancelled, expired, superseded, and completed attempts.

### Phase 3: logging and operator transparency

Priority: P0 for rejection classification; P1 for UI and retention  
Owners: API, SDK, telemetry, admin UI

#### 3.1 Connection lifecycle logs

Emit structured events for:

- connection authenticated;
- active-handle rebind requested;
- rebind accepted;
- rebind rejected with classified reason;
- reconnect attempt started;
- reconnect attempt succeeded;
- reconnect attempt failed;
- stale connection callback ignored;
- disconnect observed;
- recovery deadline started or refreshed;
- recovery expired;
- server-initiated close, including operation and classified reason.

Required correlation fields:

- transport;
- session ID;
- connection epoch;
- execution unit ID;
- task ID and attempt ID when applicable;
- reconnect attempt number;
- SDK build identifier when supplied;
- close code;
- safe reason code;
- elapsed reconnect time.

Forbidden fields:

- provider keys;
- task handles;
- upload tokens;
- receipts;
- presigned URLs;
- requestor identity;
- scalar parameters;
- raw provider exception text.

#### 3.2 Operation disposition logs

For acceptance, rejection, progress, input refresh, result authorization,
completion, failure, and cancellation, record:

- operation;
- disposition;
- classified reason;
- task and attempt IDs;
- attempt state;
- session ID and connection epoch;
- sequence metadata only for progress.

Sample routine accepted progress logs or aggregate them. Never sample rejection,
rebind, disconnect, recovery-expiry, upload ambiguity, or terminal-operation
logs.

#### 3.3 SDK diagnostics

Expose an optional structured callback rather than requiring applications to
parse strings:

```text
onDiagnostic({
  event,
  sdkBuild,
  connectionEpoch,
  reconnectAttempt,
  taskId,
  attemptId,
  disposition,
  reasonCode,
  sequence
})
```

The SDK should expose safe WebSocket close code and reason classification. It
must not forward arbitrary server bodies or secret-bearing exceptions.

#### 3.4 Admin timeline

- Show every connection epoch.
- Associate rebind activity with the active attempt.
- Show `rebind accepted` separately from `connected`.
- Show non-fatal progress drops with their classified reason.
- Show the recovery deadline and whether it was refreshed.
- Display timestamps consistently with an explicit timezone.
- Do not claim “invalid handle” for rate or sequence drops.

#### 3.5 Alerts

Alert on:

- repeated reconnects for one execution unit;
- any server close caused by progress;
- stale-sequence drops immediately after a new attempt;
- recovery expiry shortly after a successful rebind;
- upload-outcome-unknown;
- completion retry conflicts;
- journal restoration failure rate;
- WSS and gRPC disposition mismatches.

## Follow-on tasks from the invalid-sequence audit

### P0

1. Separate progress disposition from task authorization.
2. Key progress by task and attempt.
3. Clear progress on all attempt exits.
4. Add real WSS tests for stale, duplicate, and burst progress remaining
   connected.
5. Add a new-attempt regression test where the prior attempt ended with a high
   sequence.
6. Preserve the active SDK task through transport errors.
7. Reset or recompute reconnect recovery from the latest disconnect epoch so a
   flapping older recovery loop cannot revoke a freshly rebound attempt.

### P1

1. Add request IDs to input-refresh, upload-authorization, and completion
   request/response messages, or explicitly reject concurrent calls.
2. Make completion retries idempotent.
3. Add upload reconciliation before retrying ambiguous publication.
4. Move attempt diagnostics to the current session on rebind.
5. Implement the browser recovery journal and single-tab ownership.
6. Add an explicit rebind response containing task, attempt, disposition, and
   recovery deadline without exposing handles.
7. Cover accepted-task reconnect, progress, upload, completion, cancellation,
   and browser refresh in a Playwright end-to-end test.
8. Correct `RevokeExpired` to return whether it changed state and test the
   caller-visible result.

### P2

1. Replace JavaScript `uint64` conversion with safe `bigint` handling or enforce
   and validate a protocol maximum at `Number.MAX_SAFE_INTEGER`.
2. Replace attempt event `count + 1` allocation before increasing ECS desired
   count. Prefer immutable UUIDv7 event keys plus an explicit ordinal in the
   payload, or a durable conditional sequence allocator.
3. Make upload authorizations and staged completion state durable enough to
   survive an API service restart.
4. Define active-assignment restoration across API deployments; current startup
   recovery revokes in-flight attempts.
5. Add protocol conformance cases for zero, duplicate, maximum-safe, and
   overflowing sequence numbers.
6. Add bounded validation for progress phase, message, percentage, and any
   future metadata.

## Test matrix

The release gate must include:

| Layer | Scenario | Required outcome |
| --- | --- | --- |
| SDK unit | Ten rapid progress calls | One latest-value frame |
| SDK unit | Progress during reconnect | Buffered, handler remains active |
| SDK unit | Upload authorization during reconnect | Waits for rebind |
| SDK unit | Transport failure while reporting failure | Active handle retained |
| SDK unit | Stale socket closes after replacement | Replacement remains active |
| API unit | Duplicate progress | Dropped, connection remains open |
| API unit | Too-fast progress | Accepted or non-fatally dropped |
| API unit | Wrong handle | Classified authorization rejection |
| API unit | New attempt after high prior sequence | Sequence `1` accepted |
| WSS integration | Disconnect, progress, rebind | Same attempt continues |
| gRPC integration | Disconnect, progress, rebind | Same attempt continues |
| WSS integration | Lost completion acknowledgement | Same receipt retry succeeds |
| Browser E2E | Refresh accepted provider tab | Journal restores same attempt |
| Browser E2E | Two tabs race to restore | One Web Lock owner executes |
| Browser E2E | Cancellation while tab closed | Restore rejected and journal purged |
| Browser E2E | Shutdown during upload | No blind upload replay |
| Deployment | API restart during active attempt | Documented, tested recovery result |

## Rollout

1. Deploy server progress classification, attempt-scoped tracking, non-fatal
   drops, and diagnostic logging.
2. Observe metrics while existing SDKs continue operating.
3. Ship a new manually distributed SDK preview with coalescing, reconnect
   barriers, active-state preservation, safe diagnostics, and regression tests.
4. Have the partner publish the deployed bundle checksum and SDK build ID in
   provider diagnostics.
5. Enable browser journaling behind an explicit trusted-device flag.
6. Exercise tab refresh, cancellation, disconnect, and upload ambiguity in a
   staging task before enabling journaling by default.
7. Complete protocol correlation and durable upload/completion work before
   advertising restart-safe result publication.

## Definition of done

- No progress timing or sequence condition can close a provider connection.
- A new attempt is independent of every previous attempt's progress sequence.
- Transient disconnects preserve active ownership and task-control calls wait
  for rebind.
- A trusted browser can refresh and restore the same accepted attempt without
  duplicate execution by another tab.
- Ambiguous uploads are never blindly replayed.
- Operators can determine exactly why a message was accepted, dropped,
  rejected, or caused a close without accessing secret data.
- The full SDK, WSS, gRPC, browser-refresh, cancellation, and result-publication
  recovery matrix passes before release.
