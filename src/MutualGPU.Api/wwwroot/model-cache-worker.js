import { StreamingSha256 } from "/js/streaming-sha256.js?v=20260726-klein-fp16-cache-3";

const MODEL_CACHE_NAME = "mutualgpu-flux2-klein-4b-2cd85d938ff8afb954661262c12c6d10676a20e1-v1";
const HUGGING_FACE_ARTIFACT_PREFIX =
  "/KatzenStuff/flux-2-klein-4b-webgpu/resolve/2cd85d938ff8afb954661262c12c6d10676a20e1/models/klein-4b/";
const SHA256_PATTERN = /^[0-9a-f]{64}$/;

self.addEventListener("install", event => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", event => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener("fetch", event => {
  const url = new URL(event.request.url);
  if (event.request.method !== "GET" || !isModelArtifact(url)) return;
  event.respondWith(handleModelArtifact(event.request, url, event));
});

function isModelArtifact(url) {
  const expectedBytes = Number(url.searchParams.get("mutualgpu_bytes"));
  const knownSource =
    (url.origin === "https://huggingface.co" && url.pathname.startsWith(HUGGING_FACE_ARTIFACT_PREFIX)) ||
    (url.origin === self.location.origin && url.pathname.startsWith("/models/klein-4b/"));
  return knownSource &&
    Number.isSafeInteger(expectedBytes) &&
    expectedBytes > 0 &&
    SHA256_PATTERN.test(url.searchParams.get("sha256") || "");
}

async function handleModelArtifact(request, url, event) {
  const warmOnly = url.searchParams.get("mutualgpu_cache_only") === "1";
  const cacheUrl = new URL(url);
  cacheUrl.searchParams.delete("mutualgpu_cache_only");
  const cacheKey = new Request(cacheUrl, {
    method: "GET",
    mode: "cors",
    credentials: "omit"
  });
  const expectedBytes = Number(cacheUrl.searchParams.get("mutualgpu_bytes"));
  const expectedSha256 = cacheUrl.searchParams.get("sha256");
  const cache = await caches.open(MODEL_CACHE_NAME);
  const cached = await cache.match(cacheKey);
  if (
    cached &&
    responseLength(cached) === expectedBytes &&
    cached.headers.get("x-mutualgpu-sha256") === expectedSha256
  ) {
    return warmOnly ? cacheResult("hit") : cached;
  }
  if (cached) await cache.delete(cacheKey);

  const upstreamUrl = new URL(cacheUrl);
  upstreamUrl.searchParams.delete("mutualgpu_bytes");
  let response;
  try {
    response = await fetch(new Request(upstreamUrl, {
      method: "GET",
      mode: "cors",
      credentials: "omit",
      cache: "no-store",
      redirect: "follow"
    }));
    if (!response.ok) {
      return warmOnly ? cacheFailure(response.status, `Model artifact download failed (${response.status}).`) : response;
    }
    const advertisedBytes = responseLength(response);
    if (advertisedBytes !== expectedBytes) {
      return cacheFailure(502, `Model artifact length mismatch: expected ${expectedBytes}, received ${advertisedBytes || "no content-length"}.`);
    }
    const verified = integrityCheckedResponse(response, expectedBytes, expectedSha256);
    if (warmOnly) {
      await cache.put(cacheKey, verified);
      return cacheResult("stored");
    }
    const cacheWrite = cache.put(cacheKey, verified.clone());
    event.waitUntil(cacheWrite);
    return verified;
  } catch (error) {
    return cacheFailure(507, `Persistent model cache failed: ${String(error?.message || error)}`);
  }
}

function responseLength(response) {
  const value = Number(response.headers.get("content-length"));
  return Number.isSafeInteger(value) && value > 0 ? value : 0;
}

function integrityCheckedResponse(response, expectedBytes, expectedSha256) {
  if (!response.body) throw new Error("Model artifact response has no readable body.");
  let receivedBytes = 0;
  const digest = new StreamingSha256();
  const body = response.body.pipeThrough(new TransformStream({
    transform(chunk, controller) {
      receivedBytes += chunk.byteLength;
      if (receivedBytes > expectedBytes) {
        throw new Error(`Model artifact exceeded its declared length of ${expectedBytes} bytes.`);
      }
      digest.update(chunk);
      controller.enqueue(chunk);
    },
    flush() {
      if (receivedBytes !== expectedBytes) {
        throw new Error(`Model artifact length mismatch: expected ${expectedBytes}, received ${receivedBytes}.`);
      }
      const actualSha256 = digest.digestHex();
      if (actualSha256 !== expectedSha256) {
        throw new Error(`Model artifact SHA-256 mismatch: expected ${expectedSha256}, received ${actualSha256}.`);
      }
    }
  }));
  const headers = new Headers(response.headers);
  headers.set("content-length", String(expectedBytes));
  headers.set("x-mutualgpu-sha256", expectedSha256);
  return new Response(body, {
    status: response.status,
    statusText: response.statusText,
    headers
  });
}

function cacheResult(state) {
  return new Response(null, {
    status: 204,
    headers: { "x-mutualgpu-cache": state }
  });
}

function cacheFailure(status, message) {
  return new Response(message, {
    status,
    headers: { "content-type": "text/plain; charset=utf-8" }
  });
}
