import {
  buildNonBlankMessageReport,
  buildReport,
  createReplayAccumulator,
  isRecord,
  mergeReplayReports,
  reportForReplaySdkEvent,
  reportForSdkEvent,
} from "./reporting-core.mjs";

const HEARTBEAT_INTERVAL_MS = 60000;
const MAX_PRESENCE_RETRY_MS = 120000;

const SUBSCRIBED_TYPES = [
  "assistant.turn_start",
  "assistant.turn_end",
  "assistant.intent",
  "session.title_changed",
  "session.idle",
  "session.shutdown",
  "skill.invoked",
  "assistant.message",
  "user.message",
  "session.usage_info",
  "elicitation.requested",
  "elicitation.completed",
  "user_input.requested",
  "user_input.completed",
  "subagent.started",
  "subagent.completed",
  "subagent.failed",
];

/** @typedef {Record<string, unknown>} ActivityReport */
/** @typedef {{ ok: boolean, status: number, statusText?: string, body?: unknown }} PostResult */
/** @typedef {"connecting" | "replaying" | "ready" | "terminal"} RuntimeState */
/** @typedef {{ kind: "ok" } | { kind: "retry" | "terminal", reason: string }} Outcome */

/** @param {unknown} value */
function errorText(value) {
  return value instanceof Error ? value.message : String(value);
}

/** @param {unknown} value @param {string} fallback */
function reasonText(value, fallback) {
  return typeof value === "string" && value.trim() ? value : fallback;
}

/** @param {number} status */
function isTransientHttpStatus(status) {
  return status === 408 || status === 429 || status >= 500;
}

/** @param {PostResult} result */
function responseDescription(result) {
  return `HTTP ${result.status}${result.statusText ? ` ${result.statusText}` : ""}`;
}

/**
 * Presence is acknowledged only by `recorded: true` and retries only when the response is a
 * transient transport failure or explicitly retryable. Ordinary delivery is best-effort, so a
 * permanent negative counts as sent and preserves intentionally ignored reports.
 *
 * @param {PostResult} result
 * @param {"presence" | "ordinary"} lane
 * @returns {Outcome}
 */
function classifyResult(result, lane) {
  const presence = lane === "presence";
  if (!result.ok) {
    const description = responseDescription(result);
    if (isTransientHttpStatus(result.status)) return { kind: "retry", reason: description };
    return presence ? { kind: "terminal", reason: description } : { kind: "ok" };
  }

  const body = isRecord(result.body) ? result.body : null;
  const notRecorded = presence ? "presence was not recorded" : "report was not recorded";
  if (body?.monitored === false) {
    return {
      kind: "terminal",
      reason: reasonText(body.reason, "worktree is not monitored by this endpoint"),
    };
  }
  if (body?.recorded === true) return { kind: "ok" };
  if (body?.retryable === true) {
    return { kind: "retry", reason: reasonText(body.reason, notRecorded) };
  }

  return presence ? { kind: "terminal", reason: reasonText(body?.reason, notRecorded) } : { kind: "ok" };
}

/** @param {PostResult} result @returns {Outcome} */
export function classifyPresenceResult(result) {
  return classifyResult(result, "presence");
}

/** @param {number} attempt */
export function presenceRetryDelay(attempt) {
  return Math.min(1000 * (2 ** Math.min(Math.max(0, attempt), 7)), MAX_PRESENCE_RETRY_MS);
}

/**
 * Reports this Copilot session to the one Treemon activity endpoint: acknowledged presence, one
 * merged replay snapshot, heartbeat cadence, reconnect after transport loss, and exact closure.
 *
 * @param {{
 *   session: {
 *     on: (type: string, handler: (event: unknown) => void) => () => void,
 *     getEvents: () => Promise<unknown[]>,
 *     rpc: { metadata: { snapshot: () => Promise<{ summary?: unknown } | null | undefined> } },
 *   },
 *   baseContext: import("./reporting-core.mjs").ReportBaseContext,
 *   activityUrl: string,
 *   post: (url: string, report: ActivityReport) => Promise<PostResult>,
 *   randomId: () => string,
 *   now?: () => number,
 *   nowIso?: () => string,
 *   schedule?: (callback: () => void | Promise<void>, delayMs: number) => unknown,
 *   cancel?: (handle: unknown) => void,
 *   retryDelay?: (attempt: number) => number,
 *   setInterval?: (callback: () => void | Promise<void>, intervalMs: number) => unknown,
 *   clearInterval?: (handle: unknown) => void,
 *   heartbeatIntervalMs?: number,
 *   log?: (message: string) => void,
 * }} options
 */
export function createReportingRuntime(options) {
  const { session, baseContext, activityUrl } = options;
  const now = options.now ?? Date.now;
  const nowIso = options.nowIso ?? (() => new Date(now()).toISOString());
  const log = options.log ?? (() => {});
  const schedule = options.schedule ?? ((callback, delayMs) => setTimeout(callback, delayMs));
  const cancel = options.cancel ?? ((handle) => clearTimeout(
    /** @type {ReturnType<typeof setTimeout>} */ (handle),
  ));
  const retryDelay = options.retryDelay ?? presenceRetryDelay;
  const startInterval = options.setInterval
    ?? ((callback, intervalMs) => setInterval(callback, intervalMs));
  const stopInterval = options.clearInterval ?? ((handle) => clearInterval(
    /** @type {ReturnType<typeof setInterval>} */ (handle),
  ));
  const heartbeatIntervalMs = options.heartbeatIntervalMs ?? HEARTBEAT_INTERVAL_MS;

  const currentProcess = createReplayAccumulator(now);
  /** @type {RuntimeState} */
  let state = "connecting";
  // A monotonic generation invalidates every continuation left behind by an abandoned connection.
  let generation = 0;
  let retryAttempt = 0;
  let snapshotVersion = 0;
  /** @type {unknown} */
  let retryHandle;
  /** @type {unknown} */
  let heartbeatHandle;
  /** @type {Promise<void>} */
  let work = Promise.resolve();
  /** @type {Promise<void>} */
  let heartbeatTask = Promise.resolve();
  /** @type {Promise<void>} */
  let metadataTask = Promise.resolve();
  /** @type {ActivityReport | undefined} */
  let pendingClose;
  /** @type {ActivityReport[] | undefined} */
  let historicalReports;
  /** @type {(() => void)[]} */
  let unsubscribes = [];
  let liveTitleSeen = false;
  let started = false;
  let stopped = false;
  let closed = false;

  /** @param {string} kind */
  const localReport = (kind) => buildReport(
    { ...baseContext, eventId: options.randomId(), occurredAt: nowIso() },
    kind,
  );

  const presenceReport = localReport("session_present");

  /**
   * @param {ActivityReport} report
   * @param {"presence" | "ordinary"} lane
   * @returns {Promise<Outcome>}
   */
  async function send(report, lane) {
    try {
      const result = await options.post(activityUrl, report);
      const outcome = classifyResult(result, lane);
      if (!result.ok && outcome.kind === "ok") {
        log(`POST ${activityUrl} -> ${responseDescription(result)}`);
      }
      return outcome;
    } catch (error) {
      return { kind: "retry", reason: errorText(error) };
    }
  }

  function clearRetry() {
    if (retryHandle !== undefined) cancel(retryHandle);
    retryHandle = undefined;
  }

  function stopHeartbeat() {
    if (heartbeatHandle !== undefined) stopInterval(heartbeatHandle);
    heartbeatHandle = undefined;
  }

  /** @param {string} reason */
  function terminate(reason) {
    generation += 1;
    clearRetry();
    stopHeartbeat();
    state = "terminal";
    log(`Reporting ${activityUrl} stopped: ${reason}`);
  }

  /** Re-sends the same idempotent presence report after bounded backoff. @param {string} reason */
  function reconnect(reason) {
    generation += 1;
    clearRetry();
    stopHeartbeat();
    if (stopped || (closed && pendingClose === undefined)) {
      state = "terminal";
      return;
    }

    const delayMs = retryDelay(retryAttempt);
    retryAttempt += 1;
    state = "connecting";
    log(`Presence ${activityUrl} will retry in ${delayMs}ms: ${reason}`);
    retryHandle = schedule(() => {
      retryHandle = undefined;
      return connect();
    }, delayMs);
  }

  /** @param {Outcome} outcome */
  function applyOutcome(outcome) {
    if (outcome.kind === "terminal") terminate(outcome.reason);
    else if (outcome.kind === "retry") reconnect(outcome.reason);
  }

  /** @param {ActivityReport} report @param {number} sendGeneration */
  async function sendOrdinary(report, sendGeneration) {
    if (stopped || closed || generation !== sendGeneration) return;
    const outcome = await send(report, "ordinary");
    if (stopped || closed || generation !== sendGeneration) return;
    applyOutcome(outcome);
  }

  function heartbeatTick() {
    const tickGeneration = generation;
    heartbeatTask = heartbeatTask.then(() => (heartbeatHandle === undefined
      ? undefined
      : sendOrdinary(localReport("heartbeat"), tickGeneration)));
    return heartbeatTask;
  }

  function startHeartbeat() {
    if (heartbeatHandle === undefined && !stopped && !closed) {
      heartbeatHandle = startInterval(heartbeatTick, heartbeatIntervalMs);
    }
  }

  /**
   * Maps the first successful `getEvents()` read into one compact snapshot cached for the process
   * lifetime. A failed read is logged and left uncached so the next reconnect can retry it.
   */
  async function loadHistorical() {
    if (historicalReports !== undefined) return historicalReports;

    try {
      const events = await session.getEvents();
      if (!Array.isArray(events)) throw new TypeError("getEvents returned a non-array result");

      const accumulator = createReplayAccumulator(now);
      events.forEach((event) => {
        const report = reportForReplaySdkEvent(baseContext, event);
        if (report) accumulator.observe(report);
      });
      historicalReports = accumulator.snapshot();
      return historicalReports;
    } catch (error) {
      log(`getEvents replay failed: ${errorText(error)}`);
      return [];
    }
  }

  /**
   * Sends the historical snapshot merged with current-process state. Live events observed while
   * that snapshot is in flight bump `snapshotVersion`, and a following pass sends what the earlier
   * passes did not already carry, so nothing is lost or sent twice.
   *
   * @param {number} replayGeneration
   */
  async function replay(replayGeneration) {
    state = "replaying";
    startHeartbeat();
    const historical = await loadHistorical();
    if (stopped || generation !== replayGeneration) return;

    /** @type {Set<unknown>} */
    const sentEventIds = new Set();
    let sentVersion = -1;
    while (sentVersion !== snapshotVersion) {
      sentVersion = snapshotVersion;
      const reports = mergeReplayReports(historical, currentProcess.snapshot())
        .filter((report) => !sentEventIds.has(report.eventId));

      for (const report of reports) {
        sentEventIds.add(report.eventId);
        const outcome = await send(report, "ordinary");
        if (stopped || generation !== replayGeneration) return;
        if (outcome.kind !== "ok") {
          applyOutcome(outcome);
          return;
        }
      }
    }

    state = closed ? "terminal" : "ready";
  }

  function connect() {
    const connectGeneration = (generation += 1);
    state = "connecting";
    work = work.then(async () => {
      if (stopped || generation !== connectGeneration) return;
      const outcome = await send(presenceReport, "presence");
      if (stopped || generation !== connectGeneration) return;
      if (outcome.kind !== "ok") {
        applyOutcome(outcome);
        return;
      }

      retryAttempt = 0;
      if (pendingClose !== undefined) await deliverClose(pendingClose);
      else await replay(connectGeneration);
    });
    return work;
  }

  /** @param {ActivityReport} report */
  async function deliverClose(report) {
    pendingClose = undefined;
    stopHeartbeat();
    await send(report, "ordinary");
    generation += 1;
    state = "terminal";
  }

  /**
   * The live shutdown is the last thing this process reports: it stops every other lane and
   * delivers exactly one closure, waiting behind an unacknowledged presence when one is in flight.
   *
   * @param {ActivityReport} report
   */
  function close(report) {
    closed = true;
    unsubscribeAll();
    stopHeartbeat();
    if (state === "terminal") return;
    pendingClose = report;
    if (state === "connecting") return;

    const closeGeneration = generation;
    work = work.then(() => (stopped || generation !== closeGeneration
      ? undefined
      : deliverClose(report)));
  }

  /**
   * Records one live report and sends it once the endpoint is ready. While connecting or replaying
   * the accumulator carries it into the next acknowledged snapshot instead.
   *
   * @param {ActivityReport} report
   */
  function publish(report) {
    currentProcess.observe(report);
    snapshotVersion += 1;
    if (state !== "ready") return work;

    const publishGeneration = generation;
    work = work.then(() => sendOrdinary(report, publishGeneration));
    return work;
  }

  function unsubscribeAll() {
    const active = unsubscribes;
    unsubscribes = [];
    // Teardown at the SDK boundary is best-effort: one failure cannot strand the others.
    active.forEach((unsubscribe) => { try { unsubscribe(); } catch { /* already detached */ } });
  }

  /** @param {unknown} event */
  function handleLiveEvent(event) {
    if (stopped || closed) return;
    const report = reportForSdkEvent(baseContext, event);
    if (!report) return;

    if (report.kind === "session_closed") {
      close(report);
      return;
    }

    if (report.kind === "title_reported") liveTitleSeen = true;
    void publish(report);
  }

  async function bootstrapTitle() {
    try {
      const snapshot = await session.rpc.metadata.snapshot();
      if (stopped || closed || liveTitleSeen) return;

      const report = buildNonBlankMessageReport(
        { ...baseContext, eventId: options.randomId(), occurredAt: nowIso() },
        "title_bootstrap",
        snapshot?.summary,
      );
      if (report) await publish(report);
    } catch (error) {
      log(`metadata title bootstrap failed: ${errorText(error)}`);
    }
  }

  async function start() {
    if (started || stopped) return;
    started = true;
    unsubscribes = SUBSCRIBED_TYPES.map((type) => session.on(type, handleLiveEvent));

    await connect();
    if (stopped || closed) return;
    metadataTask = bootstrapTitle();
  }

  /** Cancels every lane. A live closure already in flight still delivers its final report. */
  function stop() {
    if (stopped || closed) return;
    stopped = true;
    generation += 1;
    unsubscribeAll();
    clearRetry();
    stopHeartbeat();
    state = "terminal";
  }

  async function flush() {
    await metadataTask;
    await Promise.all([work, heartbeatTask]);
  }

  return {
    start,
    stop,
    flush,
    snapshot: () => ({
      state,
      closed,
      retryAttempt,
      heartbeatRunning: heartbeatHandle !== undefined,
    }),
  };
}
