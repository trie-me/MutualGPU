# ACR 0001: Provider session recovery and progress sequencing

- Status: Proposed
- Date: 2026-07-25
- Decision owners: MutualGPU API and SDK maintainers
- Incident: Browser provider reconnect loop during an accepted remote task
- Remediation plan: [Provider reconnect, progress, and browser recovery remediation plan](../provider-reconnect-progress-remediation-plan.md)
- SDK evidence: `mutualgpu-browser-sdk-cancellation-preview-2026-07-23.tgz`
- SDK artifact SHA-256: `41156658c891b0e0af179c1c1c07f4745ec188f9a247f3e9d5b9e3de7b267698`

## Context

An accepted browser-provider attempt entered a repeated WSS reconnect loop. The
server closed each affected connection with policy code `1008` and the reason
`Invalid task handle`, while the provider reported successful active-task
rebinds between closes.

The investigation established that `Invalid task handle` is not a reliable
diagnosis. The server currently maps several unrelated outcomes to the same
failure:

- unknown assignment;
- wrong execution unit;
- wrong handle;
- invalid attempt state;
- duplicate or stale progress sequence;
- progress arriving less than one server-observed second after the previous
  accepted update;
- invalid task-scoped input or result authorization requests.

For WSS, any of these results can close the connection. For gRPC, they become
the same failed-precondition error.

The shipped SDK already suppresses progress calls made within one
client-observed second, but it discards the newer value rather than retaining
the latest value for later delivery. Client timing cannot guarantee server
arrival timing because network latency can compress the interval between
frames.

The sequence audit also found that server progress is retained by logical task
ID rather than attempt ID and is not removed on every requeue path. A
replacement attempt begins at sequence `1` and can therefore be classified as
stale relative to an earlier attempt.

During reconnect, the browser transport rejects task operations immediately
with a disconnected-session error. If that error escapes the task handler, the
SDK can attempt to report failure over the same unavailable transport and then
clear the active task. A subsequent reconnect can omit the active handle.

The current browser provider stores its provider startup state in
`sessionStorage`. Active task ownership, progress sequence, and recovery state
do not survive a tab restart. A tab restart also destroys WebGPU execution, so
browser recovery can preserve ownership and restart information but cannot
preserve GPU memory without workload-specific checkpoints.

## Decision

MutualGPU will separate advisory progress flow control from task
authorization, make task control reconnect-aware, and add an opt-in
browser-local recovery journal.

### Progress handling

1. Progress outcomes will use an explicit result type rather than a Boolean.
2. Duplicate, stale, superseded, or burst progress will be non-fatal.
3. The server will remove its per-task one-second arrival-time check.
4. Sequence validation will remain monotonic and will be scoped to
   `(TaskId, AttemptId)`.
5. Progress state will be cleared on every terminal or requeue transition.
6. The SDK will use a bounded, one-slot, latest-value-wins buffer.
7. Buffered progress will flush only while connected and successfully rebound.
8. Progress sequence allocation will remain monotonic across reconnect and
   browser restoration.

The server continues to enforce bounded frames and bounded field sizes. If
connection-level abuse protection is needed, it will drop progress under a
bounded token bucket and record telemetry; it will not report an authorization
failure or close a healthy provider task.

### Reconnect-aware task control

The SDK will represent connection and task state explicitly. While an accepted
task is reconnecting:

- progress is coalesced in the one-slot buffer;
- input refresh waits for rebind;
- result-upload authorization waits for rebind;
- completion waits for rebind;
- failure waits for rebind.

Transient transport errors do not clear active task state. Active state is
cleared only after confirmed terminal state, matching requestor cancellation,
definitive rebind rejection, recovery expiry, or explicit provider shutdown.

Uploads are not blindly replayed. If upload transmission began and the response
was lost, the SDK records an `upload_outcome_unknown` state and requires server
reconciliation before another publication.

Completion will become idempotent for the same attempt and receipt so a lost
completion acknowledgement can be retried safely.

### Browser-local recovery

Trusted provider devices may opt into a recovery journal stored in IndexedDB.
`localStorage` may contain a non-secret discovery marker, but it is not the
authoritative journal.

The journal includes:

- schema and SDK build versions;
- provider-identity fingerprint;
- task and attempt IDs;
- an active recovery capability;
- accepted lifecycle state;
- the minimum assignment state required to restart;
- next progress sequence;
- newest pending progress value;
- upload and completion recovery state;
- connection epoch and expiry timestamps.

It does not store upload tokens or presigned URLs.

Task handles and scalar inputs are sensitive. The initial implementation is
restricted to an explicitly trusted provider origin with a strict Content
Security Policy, no untrusted third-party scripts, short expiry, and visible
forget/revoke controls. A follow-on protocol change will replace durable raw
task-handle storage with a short-lived provider-bound recovery credential and a
server-supplied rebind snapshot.

The Web Locks API ensures one browser tab owns execution for a provider
identity. BroadcastChannel coordinates status and tab takeover.

On restoration, the SDK:

1. acquires the provider Web Lock;
2. validates the journal and its expiry;
3. authenticates the provider;
4. reconnects with the recovery capability;
5. waits for explicit rebind acceptance;
6. refreshes the input URL;
7. restores the accepted task facade and progress sequence;
8. resumes a workload checkpoint or restarts the workload for the same attempt;
9. purges the journal if the attempt was cancelled, revoked, completed, or
   expired.

### Diagnostics

Server and SDK diagnostics will distinguish:

- progress accepted;
- progress coalesced;
- stale or duplicate progress dropped;
- malformed progress rejected;
- unknown assignment;
- wrong execution unit;
- wrong handle;
- invalid attempt state;
- rebind accepted;
- rebind rejected;
- recovery deadline refreshed;
- recovery expired;
- upload outcome unknown;
- completion replay accepted or rejected;
- server-initiated and client-initiated closes.

Structured server logs include transport, session ID, connection epoch, task
ID, attempt ID, execution unit ID, operation, disposition, classified reason,
sequence metadata, attempt state, reconnect attempt, close code, and safe close
reason.

Metrics use only low-cardinality tags such as transport, operation, and reason.
Task, attempt, session, and execution-unit IDs are correlation fields in
structured logs, not metric tags.

SDKs expose an optional structured diagnostic callback containing safe
lifecycle and disposition fields. The browser transport will retain and safely
classify the WebSocket close code rather than replacing every close with one
generic error.

Provider keys, task handles, recovery credentials, upload tokens, receipts,
presigned URLs, requestor identities, scalar inputs, and raw exception bodies
must never appear in logs, metrics, close reasons, or user-facing diagnostics.

## Evidence

The decision is based on:

- the manually distributed cancellation-preview SDK artifact;
- byte-for-byte comparison of its provider-core and provider-web sources with
  the declared source commit;
- passing packaged SDK core and browser-transport tests;
- a controlled reproduction showing that an operation during reconnect can
  clear active state and cause the next connection to omit the active handle;
- authoritative task attempt state from current MutualGPU AWS S3 storage;
- current WSS, gRPC, provider-session, progress-registry, upload, completion,
  reconnect-recovery, and browser-hosting implementations;
- the absence of classified rejected-message telemetry in the incident window.

The evidence proves that too-fast or stale progress can close a connection. It
does not prove that every close in the incident had that cause because current
logs collapse all rejection predicates into one close reason.

## Alternatives considered

### Keep server arrival throttling and increase the SDK interval

Rejected. A larger client interval reduces probability but cannot guarantee a
minimum server arrival interval. It also leaves derivative SDKs exposed to a
fatal advisory-data rule.

### Trust only the official SDK and remove all server progress validation

Rejected. MutualGPU may trust supported senders, but the server must still
enforce bounded frames, bounded values, monotonic attempt-scoped progress, and
connection-level resource protection.

### Queue every progress update

Rejected. Progress is advisory, and an unbounded queue creates memory and stale
status problems. One latest-value slot is sufficient.

### Store recovery only in memory

Rejected. It cannot survive tab refresh or browser restart.

### Store the full recovery record in `localStorage`

Rejected. `localStorage` is synchronous, lacks transactional structured
updates, and is unsuitable for coordinating lifecycle, sequence, upload, and
expiry state. IndexedDB is the browser-local persistence mechanism.

### Automatically replay result uploads after reconnect

Rejected. Upload authorization is single-use, and a lost response makes the
server outcome ambiguous. Blind replay can duplicate publication or conflict
with an already staged result.

### Treat every invalid task-scoped operation as a fatal close

Rejected. It prevents recovery, obscures the cause, and lets advisory progress
terminate valid computation. Only authentication, protocol negotiation, and
genuine authorization violations justify a policy close.

## Consequences

### Positive

- Progress timing cannot terminate valid computation.
- Replacement attempts do not inherit stale sequence state.
- Reconnect preserves active ownership and waits task-control operations.
- Browser providers can recover ownership after a tab restart.
- Upload ambiguity is explicit and cannot trigger blind replay.
- Completion acknowledgement loss is recoverable.
- Operators can identify the exact rejection or drop reason without accessing
  secret values.
- WSS and gRPC behavior becomes consistent and testable.

### Costs and risks

- SDK connection state becomes more complex.
- IndexedDB recovery introduces schema migration, expiry, single-tab
  coordination, and trusted-device UX.
- Browser-local recovery capabilities increase the impact of same-origin script
  compromise; CSP and origin isolation become release requirements.
- Tab restart may restart expensive computation unless the workload provides a
  checkpoint.
- Protocol correlation and rebind snapshots require compatible server and SDK
  rollout.
- Durable upload reconciliation and active-assignment restoration require
  additional AWS S3 state design.

## Implementation sequence

1. Deploy server-side classified progress outcomes.
2. Make stale, duplicate, and burst progress non-fatal.
3. Scope retained progress to task and attempt and clear it on all exits.
4. Add structured rejection, close, rebind, and recovery logging and metrics.
5. Ship a replacement manual SDK preview with latest-value coalescing,
   reconnect barriers, and active-state preservation.
6. Make completion replay idempotent.
7. Add explicit upload ambiguity and reconciliation behavior.
8. Introduce the opt-in IndexedDB recovery journal and single-tab ownership.
9. Add a provider-bound recovery credential and rebind snapshot.
10. Address follow-on sequence and durability work before scaling the API
    service beyond one concurrent writer.

## Verification

Release requires:

- SDK fake-clock tests for latest-value coalescing;
- SDK reconnect tests with progress, refresh, upload authorization, completion,
  and failure invoked during backoff;
- server tests proving stale and burst progress remain connected;
- a regression proving attempt B sequence `1` is accepted after attempt A;
- real WSS and gRPC accepted-task reconnect tests;
- idempotent lost-acknowledgement completion tests;
- Playwright refresh recovery with IndexedDB;
- two-tab Web Lock exclusion;
- cancellation while the provider tab is closed;
- upload-shutdown tests proving no blind replay;
- structured-log redaction tests;
- matching WSS and gRPC disposition tests.

## Follow-on records

Separate ACRs are required before:

- introducing a durable provider recovery credential;
- making staged uploads and completion state restart-durable in AWS S3;
- restoring active assignments across API deployments;
- increasing ECS desired count above one;
- changing the public progress disposition return type;
- adding workload-specific WebGPU checkpoint formats.

## Review trigger

Review this ACR after the first replacement SDK preview has completed:

- a forced mid-inference disconnect;
- successful active-task rebind;
- buffered progress flush;
- result upload;
- completion;
- tab-refresh recovery;
- requestor cancellation during offline recovery.

Promote the status from `Proposed` only after the release-gate matrix passes and
the incident diagnostics show classified outcomes without secret leakage.
