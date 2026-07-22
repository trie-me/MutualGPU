# Provider SDK consumer API reference

The Node.js and Chrome packages share `ProviderClient` and its one-active-task lifecycle. Transport-specific classes adapt that lifecycle to the environment.

## Imports

```js
import {
  ProviderClient,
  ProviderClientError
} from "@mutualgpu/provider-core";

import { NodeGrpcTransport } from "@mutualgpu/provider-node";
import { BrowserWebSocketTransport } from "@mutualgpu/provider-web";
```

`ProviderUploadError` is exported from `@mutualgpu/provider-core/result-upload`. Direct use of `uploadProviderResult` and the `protocol` subpath is not required for ordinary provider applications.

## Transports

### `new NodeGrpcTransport(apiBaseUrl, presharedKey)`

- `apiBaseUrl`: an `https:` string or `URL` for the MutualGPU API.
- `presharedKey`: the provider key issued for one configured execution unit.

The transport uses native Node HTTP/2 for enrollment and the bidirectional provider session. Result upload uses the environment's Fetch, Blob, FormData, and Web Crypto implementations.

### `new BrowserWebSocketTransport(apiBaseUrl, presharedKey)`

- `apiBaseUrl`: an `https:` string or `URL`; the transport derives the `wss:` provider-session endpoint.
- `presharedKey`: the provider key issued for one configured execution unit.

The API operator must allow the provider application's origin. Use this transport only in a controlled Chrome environment where the key is not exposed through a generally accessible public bundle.

An explicit `wss:` session URL is supported by the implementation for adapter scenarios, but it also requires a separate HTTPS API base URL for enrollment and result upload. Prefer the HTTPS-base-URL constructor in consumer code.

## `ProviderClient`

### Constructor

```js
new ProviderClient(transport, {
  reconnectDelay: attempt => Math.min(1_000 * 2 ** (attempt - 1), 30_000)
})
```

`reconnectDelay` is optional. It returns the delay before each reconnect attempt. Reconnect continues until a connection opens or `close()` is called.

### Methods

| Method | Result | Contract |
| --- | --- | --- |
| `await enroll(definition)` | Enrollment response containing `executionUnitId` | Validates basic machine structure locally, then completely replaces the server enrollment |
| `await connect(handler)` | `undefined` after handshake | Opens the session and registers the asynchronous task handler; the session remains active afterward |
| `await reconnect()` | `undefined` after handshake | Manually reopens a previously configured session and supplies the active task handle when present |
| `close()` | `undefined` | Stops reconnect and closes the transport; does not cancel handler-owned workload code |

One client represents one execution unit and permits one active task. If another assignment arrives while one is active, the SDK rejects the newer assignment.

## Task handler object

The handler passed to `connect` receives a frozen task object.

### Fields

| Field | Type | Meaning |
| --- | --- | --- |
| `taskId` | string | Logical task identifier |
| `attemptId` | string | Current execution-attempt identifier |
| `taskHandle` | string | Opaque authorization handle; do not persist or log it |
| `scalars` | object of string values | Submitted scalar inputs keyed by enrollment input key |
| `input` | object or absent | `{ url, contentType, length, sha256 }` for the optional image input |
| `acknowledgementDeadline` | `Date` | Local 30-second acknowledgement deadline calculated when the assignment is received |

### State transitions

```text
pending --accept()--> accepted --complete(receipt)--> terminal
   |                       |
   +--reject(reason)------>+--fail(step, reason)----> terminal
```

If the handler returns while pending, the SDK rejects the assignment. If it returns while accepted, or throws while accepted, the SDK reports an `execution` failure. Calling a method after its legal state has ended raises `ProviderClientError` with code `invalid_task_state`.

### Methods

| Method | Legal state | Result |
| --- | --- | --- |
| `await accept()` | pending | Acknowledges the assignment and moves it to accepted |
| `await reject(reason)` | pending | Rejects the attempt and makes it terminal |
| `await reportProgress(update)` | accepted | `true` when sent; `false` when dropped by the one-update-per-second client limit |
| `await refreshInputDownload()` | accepted | A replacement short-lived HTTPS URL |
| `await uploadResult(result)` | accepted | `{ receipt, sha256 }` after authorization and successful multipart publication |
| `await complete(receipt)` | accepted | Waits for server completion confirmation and makes the task terminal |
| `await fail(step, reason)` | accepted | Reports failure and makes the task terminal |

`requestResultUpload()` is present on the current facade for transport-level integration, but ordinary consumers should use `uploadResult()` so the SDK owns token acquisition, multipart construction, checksum calculation, and receipt validation.

Progress updates accept `{ phase, percent, message }`. Calls inside the one-second window are dropped rather than queued; execution must never depend on delivery of every intermediate update.

## Result object

```js
await task.uploadResult({
  resultZip,
  metadata,
  thumbnail,
  preview,
  logs
});
```

`resultZip` is required; `zip` is accepted as an alias. A part can be a `Blob`, `ArrayBuffer`, typed array, string, or `{ data, contentType, fileName }`. The body property can alternatively be named `body` or `content`.

| Part | Limit | Validation |
| --- | --- | --- |
| `resultZip` | 50 MiB | Must be a readable ZIP |
| `metadata` | 64 KiB | Must be a JSON object |
| `thumbnail` | 5 MiB | PNG, JPEG, or WebP |
| `preview` | 5 MiB | Image MIME type declared by the capability |
| `logs` | 1 MiB | Strict UTF-8 `text/plain` |
| Complete request | 64 MiB | Includes multipart overhead and all parts |

## Errors and recovery

| Error | Stable fields | Typical handling |
| --- | --- | --- |
| `ProviderClientError` | `code` | `invalid_task_state` means a task method was called in the wrong state; `not_connected` means `reconnect()` was called before `connect(handler)` |
| `ProviderUploadError` | `status` | Inspect the status safely; rejected validation ends the attempt |
| `TypeError` | none | Correct local configuration or enrollment shape |
| Transport/protocol `Error` | none | Let automatic reconnect handle an unexpected session loss; correct authentication failures before retrying enrollment |

Do not put provider keys, task handles, input URLs, requestor data, or raw server bodies into end-user diagnostics. Upload authorization is single-use. A network failure during upload may leave its outcome ambiguous, so consumers must not blindly replay the same result publication.

## Consumer-owned responsibilities

- Parse and validate scalar values.
- Verify the input descriptor's MIME type, byte length, and SHA-256 digest.
- Apply workload-specific sandboxing, resource limits, timeout, and cancellation.
- Avoid duplicate execution while reconnect attempts to rebind an accepted task.
- Upload only declared result parts and use the returned receipt exactly once.
- Finish or fail accepted work before graceful shutdown when possible.
