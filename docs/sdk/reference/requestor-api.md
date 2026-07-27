# Requestor browser API

Use `@mutualgpu/requestor-web` from browser applications that discover capabilities, submit work, poll task state, or retrieve result descriptors.

```js
import { RequestorClient } from "@mutualgpu/requestor-web";

const requestor = new RequestorClient("https://mutualgpu.com/");
const capabilities = await requestor.listCapabilities();
```

Before the first API operation, the client performs one uncached credentialed `GET /` and waits for the anonymous requestor cookie. Concurrent initial operations share that bootstrap request. A failed bootstrap may be retried by the next operation. Every API request uses `credentials: "include"`; if the API still reports `requestor_identity_missing`, the client retries that operation once.

## Methods

| Method | Endpoint |
| --- | --- |
| `listCapabilities()` | `GET /api/capabilities/` |
| `listTasks()` | `GET /api/tasks/` |
| `getTask(taskId)` | `GET /api/tasks/{taskId}` |
| `submitTask(submission, image?)` | `POST /api/tasks/` |
| `cancelTask(taskId)` | `DELETE /api/tasks/{taskId}` |
| `reevaluateTask(taskId)` | `POST /api/tasks/{taskId}/reevaluate` |
| `getTaskResult(taskId)` | `GET /api/tasks/{taskId}/result` |
| `createWebGpuEnrollment()` | `POST /api/webgpu-enrollments` |

`submitTask` sends JSON when `image` is omitted and multipart form data when passed a `Blob` or `File`. Keep scalar values as strings and echo the current capability contract hash.

## Cancel a task

```js
try {
  await requestor.cancelTask(task.taskId);
  // The request has been durably marked Cancelled.
} catch (error) {
  if (error instanceof RequestorApiError && error.code === "task_not_cancellable") {
    // It was already Completed, Cancelled, or Failed. Refresh before updating UI.
    const current = await requestor.getTask(task.taskId);
    console.info("Task is already terminal:", current.status);
  } else {
    throw error;
  }
}
```

`cancelTask(taskId)` returns `Promise<void>` after the API returns `204 No Content`. It only acts on a task owned by the browser's anonymous requestor cookie; a task that is absent or belongs to another requestor returns `404`.

Cancellation is terminal and is committed before provider notification. A queued task is simply marked `Cancelled`. For an assigned or running task, the API invalidates the attempt handle, removes retained progress, and makes a best-effort cancellation control delivery to the provider. The provider SDK then aborts that exact assignment's `AbortSignal`. The client does not wait for handler cleanup or provider acknowledgement.

`409 task_not_cancellable` means the task was already terminal (`Completed`, `Cancelled`, or `Failed`). Do not treat it as confirmation that a new cancellation succeeded; call `getTask(taskId)` to display the actual state. If the browser loses the response to a cancellation request, the outcome is similarly ambiguous—refresh with `getTask(taskId)` instead of assuming the provider kept running.

Failures throw `RequestorApiError` with `status`, `code`, `problem`, and raw `body` properties. Result artifact download URLs are short-lived; request a fresh result descriptor after expiry.

Cross-origin applications must be exact entries in `MutualGPU:ProviderCorsOrigins`. The API cookie is `HttpOnly; Secure; SameSite=None; Partitioned`, so application JavaScript never reads or copies it and unrelated top-level sites do not share one requestor identity.
