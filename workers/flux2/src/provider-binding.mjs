import { AssignmentValidationError, parseGenerationRequest } from "./config.mjs";

export function createTaskHandler({ runtime, logger = console, chooseSeed, activity = {}, progressRetryMs = 1_050 }) {
  if (!runtime || typeof runtime.generate !== "function") throw new TypeError("A FLUX.2 runtime is required.");
  return async task => {
    const progress = createProgressReporter({ task, logger, activity, retryMs: progressRetryMs });
    activity.state = "validating";
    activity.taskId = task.taskId;
    activity.lastEventAt = new Date();
    logger.info?.(`[task ${task.taskId}] assignment received; validating inputs.`);
    let request;
    try {
      request = parseGenerationRequest(task.scalars, chooseSeed);
    } catch (error) {
      if (!(error instanceof AssignmentValidationError)) throw error;
      await task.reject(`Invalid FLUX.2 inputs: ${error.message}.`);
      activity.state = "idle";
      activity.taskId = null;
      activity.lastEventAt = new Date();
      logger.warn?.(`[task ${task.taskId}] assignment rejected: invalid inputs.`);
      return;
    }

    await task.accept();
    activity.state = "accepted";
    activity.lastEventAt = new Date();
    logger.info?.(`[task ${task.taskId}] accepted; starting GPU inference.`);
    await progress.flush({ phase: "prepare", percent: 1, message: "Preparing the FLUX.2 pipeline." });
    let generated;
    try {
      activity.state = "inference";
      generated = await runtime.generate(request, { onProgress: update => progress.push(update) });
      await progress.flush({ phase: "inference", percent: 90, message: "FLUX.2 inference complete." });
    } catch (error) {
      await progress.close();
      const safetyRejected = error?.name === "SafetyCheckRejected";
      logger.error?.(`FLUX.2 task ${task.taskId} failed during inference: ${safeLocalError(error)}`);
      await task.fail(
        safetyRejected ? "content_safety" : "inference",
        safetyRejected ? "The generated image was blocked as inappropriate content." : "FLUX.2 inference failed on the provider."
      );
      activity.state = "idle";
      activity.taskId = null;
      activity.failed = (activity.failed ?? 0) + 1;
      activity.lastEventAt = new Date();
      return;
    }

    activity.state = "uploading";
    activity.lastEventAt = new Date();
    await progress.flush({ phase: "upload", percent: 95, message: "Publishing the generated image." });
    let published;
    try {
      published = await task.uploadResult({
        resultZip: generated.resultZip,
        preview: { data: generated.preview, contentType: "image/png", fileName: "preview.png" },
        thumbnail: { data: generated.thumbnail, contentType: "image/png", fileName: "thumbnail.png" },
        metadata: generated.metadata,
        logs: generated.logs
      });
      await task.complete(published.receipt);
    } catch (error) {
      await progress.close();
      logger.error?.(`FLUX.2 task ${task.taskId} failed during result publication: ${safeLocalError(error)}`);
      await task.fail("upload", "FLUX.2 result publication did not complete.");
      activity.state = "idle";
      activity.taskId = null;
      activity.failed = (activity.failed ?? 0) + 1;
      activity.lastEventAt = new Date();
      return;
    }
    await progress.close();

    if (published.ignoredParts?.length) {
      const names = published.ignoredParts.map(part => part.name).join(", ");
      logger.warn?.(`FLUX.2 task ${task.taskId} completed with ignored optional parts: ${names}.`);
    }
    activity.state = "idle";
    activity.taskId = null;
    activity.completed = (activity.completed ?? 0) + 1;
    activity.lastEventAt = new Date();
    logger.info?.(`Completed FLUX.2 task ${task.taskId}.`);
  };
}

function createProgressReporter({ task, logger, activity, retryMs }) {
  let latest = null;
  let inFlight = null;
  let timer = null;
  let closed = false;

  const logAndQueue = update => {
    if (closed) return;
    latest = update;
    activity.lastEventAt = new Date();
    logger.info?.(`[task ${task.taskId}] ${update.phase}: ${Number(update.percent).toFixed(1)}% — ${update.message}`);
  };

  const schedule = () => {
    if (closed || timer || !latest) return;
    timer = setTimeout(() => {
      timer = null;
      void sendLatest();
    }, retryMs);
    timer.unref?.();
  };

  const sendLatest = async () => {
    if (closed || !latest) return true;
    if (inFlight) return inFlight;
    const update = latest;
    inFlight = (async () => {
      try {
        const delivered = await task.reportProgress(update);
        if (delivered === false) {
          schedule();
          return false;
        }
        if (latest === update) latest = null;
        return true;
      } catch (error) {
        if (latest === update) latest = null;
        // Reconnect and active-task rebinding belong to the SDK. A transient
        // progress failure must not start duplicate inference or discard GPU work.
        logger.warn?.(`Progress update was not delivered: ${safeLocalError(error)}`);
        return false;
      } finally {
        inFlight = null;
        if (!closed && latest && latest !== update) void sendLatest();
      }
    })();
    return inFlight;
  };

  return {
    async push(update) {
      logAndQueue(update);
      await sendLatest();
    },
    async flush(update) {
      if (update) logAndQueue(update);
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
      while (!closed && latest) {
        const delivered = await sendLatest();
        if (!delivered && latest) await new Promise(resolve => setTimeout(resolve, retryMs));
      }
    },
    async close() {
      closed = true;
      latest = null;
      if (timer) clearTimeout(timer);
      timer = null;
      if (inFlight) await inFlight;
    }
  };
}

function safeLocalError(error) {
  if (!error || typeof error !== "object") return "unknown error";
  return typeof error.name === "string" ? error.name : "Error";
}
