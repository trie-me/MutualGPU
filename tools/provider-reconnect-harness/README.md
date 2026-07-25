# MutualGPU provider reconnect probe

Static, no-cost Vercel harness for manually exercising the provider reconnect
remediations against a staging MutualGPU API.

It deliberately does not persist the provider key, task handle, input URL, or
upload token. You can either use a dedicated staging provider key and an
enrolled browser execution unit, or provision an isolated browser test provider
from the harness. The latter creates test records in the selected API only when
the operator presses the button.

## Deploy to Vercel

1. Keep the manual preview SDK at
   `artifacts/mutualgpu-browser-sdk-cancellation-preview-2026-07-25/`.
2. In this directory, run `npm run build`; this stages the local SDK into
   `public/sdk/` for the deployment.
3. Create a Vercel project with this directory as its root, using the supplied
   `vercel.json`. Deploy from your local checkout so the manually distributed
   SDK artifact is available to the build.

The app needs only static hosting and works on Vercel's Hobby plan. Its API URL
must be HTTPS. Configure the deployed Vercel origin as an approved browser
origin in MutualGPU before connecting.

## Full browser pipeline

**Run full certification** is the one-press runner. It executes the isolated
SDK contract suite, then the successful, terminal-failure, and
requestor-cancellation scenarios. A failed scenario is isolated from the next
one with a fresh test provider so a stuck accepted task cannot hide another
result. **Provision isolated test provider** remains available for manually
running an individual case. It calls the browser-enrollment endpoint, enrolls
a fixed capability with an image input and all result artifact types, then
connects the packaged browser SDK. **Run full pipeline** creates a
requestor session and a task, and verifies all of these real paths:

1. requestor cookie and cross-origin task submission;
2. task scheduling, browser WSS assignment, and acceptance;
3. progress buffering over a forced socket drop and accepted-task recovery;
4. duplicate/stale progress is non-fatal;
5. wrong-handle and malformed-progress frames are rejected, followed by a
   rebind of the original accepted task;
6. browser input download from object storage;
7. result-upload authorization, multipart result upload, and completion; and
8. requestor result lookup plus direct browser download of ZIP, metadata,
   thumbnail, preview, and logs.

The test reports predictable fault outcomes as passes only when the connection
recovers with the original task. It intentionally leaves the resulting test
objects in the selected API for diagnostic inspection.

Two separate terminal-task controls complete the event-stream coverage:

- **Run terminal failure** sends the explicit terminal `content_safety` failure
  and verifies the requestor task is `Failed` with that recorded step.
- **Run requestor cancellation** deletes the accepted requestor task, verifies
  the matching SDK task receives its abort signal, and verifies the requestor
  task is `Cancelled` with `requestor_cancelled` recorded.

An isolated provider rejects any assignment that is not the currently selected
harness scenario, preventing an old queued task from silently turning the run
into a manual held-open handler.

The run does not simulate a lost completion acknowledgement or an ambiguous
HTTP upload in the live API: both require a server/proxy fault injector at the
exact acknowledgement boundary. The isolated SDK contract suite covers the
stable `upload_outcome_unknown` disposition; use the Completion retry control
only after configuring that staging fault.

## Regression matrix

Run **SDK contract checks** with no live session to cover deterministic SDK
regressions: latest-value progress coalescing, accepted-task connection recovery
(ACR), one handler invocation after rebind, reconnect-time progress buffering,
matching-only cancellation, recovery-deadline expiry, ambiguous-upload handling,
completion acknowledgement replay with the same receipt, and stale
browser-socket callbacks.

With an accepted staging task, run these live probes:

| Probe | Expected outcome |
| --- | --- |
| Progress burst | The connection remains open; only one immediate progress send and the newest buffered value is flushed. |
| Buffer then reconnect | ACR succeeds with the same task and attempt; diagnostics show the buffered progress send after rebind. |
| ACR input/upload barrier | The control waits through reconnect, then succeeds after rebind rather than returning a disconnected-session error. |
| Duplicate/stale progress | The duplicate raw frame is non-fatal; upload authorization still succeeds. Start a fresh task after this raw sequence probe. |
| Wrong handle / malformed progress | The current socket is rejected, then the valid active assignment reconnects and rebinds. These are deliberately separate from non-fatal stale/duplicate progress. |
| Completion retry | First arrange a staging fault that drops the completion acknowledgement after server acceptance. Enter the existing receipt and retry; the original success must be returned. |

Repository application tests additionally assert the complete server-side
classification family that had previously been collapsed into `Invalid task
handle`: unknown assignment, wrong execution unit, wrong handle, invalid
attempt state, and malformed progress. The Vercel harness exercises the two
safe destructive live variants (wrong handle and malformed progress); injecting
the other three into a shared live service would require a privileged test hook
and is intentionally not exposed by this static public app.

The harness also covers cancellation by keeping the accepted handler open until
the server sends cancellation. The task status changes to waiting and all task
controls are disabled. Upload-outcome-unknown still requires an HTTP fault
injector between the browser and API: do not retry upload bytes; record the
operator-visible ambiguity and use server reconciliation.

Do not use this harness for production provider work or result publication.
