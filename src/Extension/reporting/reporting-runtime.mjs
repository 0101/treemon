import {
  buildNonBlankMessageReport,
  buildReport,
  createCurrentProcessState,
  isRecord,
  mergeReplayReports,
  reportForReplaySdkEvent,
  reportForSdkEvent,
} from "./reporting-core.mjs";

export const HEARTBEAT_INTERVAL_MS = 60000;
export const MAX_PRESENCE_RETRY_MS = 120000;

export const SUBSCRIBED_TYPES = [
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
/** @typedef {"idle" | "presence" | "retry_wait" | "replaying" | "ready" | "closing" | "terminal"} EndpointPhase */
/** @typedef {{ kind: "acknowledged" } | { kind: "retry" | "terminal", reason: string }} PresenceOutcome */
/** @typedef {{ kind: "sent" } | { kind: "transport" | "terminal", reason: string }} OrdinaryOutcome */

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
 * @param {Record<string, unknown> | null} body
 * @returns {{ kind: "terminal", reason: string } | null}
 */
function unmonitoredTerminalOutcome(body) {
  return body?.monitored === false
    ? {
      kind: "terminal",
      reason: reasonText(body.reason, "worktree is not monitored by this endpoint"),
    }
    : null;
}

/**
 * @param {PostResult} result
 * @returns {OrdinaryOutcome}
 */
function classifyOrdinaryResult(result) {
  if (!result.ok) {
    return isTransientHttpStatus(result.status)
      ? { kind: "transport", reason: responseDescription(result) }
      : { kind: "sent" };
  }

  const body = isRecord(result.body) ? result.body : null;
  const unmonitored = unmonitoredTerminalOutcome(body);
  if (unmonitored) return unmonitored;
  if (body?.recorded === false && body.retryable === true) {
    return {
      kind: "transport",
      reason: reasonText(body.reason, "report was not recorded"),
    };
  }

  return { kind: "sent" };
}

/**
 * Presence is acknowledged only by `recorded: true`. Logical negative responses retry only when
 * the presence acknowledgement explicitly says so; unmonitored and other permanent negatives stop.
 *
 * @param {PostResult} result
 * @returns {PresenceOutcome}
 */
export function classifyPresenceResult(result) {
  if (!result.ok) {
    return isTransientHttpStatus(result.status)
      ? { kind: "retry", reason: responseDescription(result) }
      : { kind: "terminal", reason: responseDescription(result) };
  }

  const body = isRecord(result.body) ? result.body : null;
  const unmonitored = unmonitoredTerminalOutcome(body);
  if (unmonitored) return unmonitored;
  if (body?.recorded === true) return { kind: "acknowledged" };
  if (body?.retryable === true) {
    return {
      kind: "retry",
      reason: reasonText(body.reason, "presence was not recorded"),
    };
  }

  return {
    kind: "terminal",
    reason: reasonText(body?.reason, "presence was not recorded"),
  };
}

/** @param {number} attempt */
export function presenceRetryDelay(attempt) {
  return Math.min(1000 * (2 ** Math.min(Math.max(0, attempt), 7)), MAX_PRESENCE_RETRY_MS);
}

/**
 * @param {{
 *   urls: string[],
 *   presenceReport: ActivityReport,
 *   loadReplayReports: () => Promise<ActivityReport[]>,
 *   post: (url: string, report: ActivityReport) => Promise<PostResult>,
 *   schedule?: (callback: () => void | Promise<void>, delayMs: number) => unknown,
 *   cancel?: (handle: unknown) => void,
 *   retryDelay?: (attempt: number) => number,
 *   log?: (message: string) => void,
 * }} options
 */
function createReportingFanout(options) {
  const schedule = options.schedule ?? ((callback, delayMs) => setTimeout(callback, delayMs));
  const cancel = options.cancel ?? ((handle) => clearTimeout(
    /** @type {ReturnType<typeof setTimeout>} */ (handle),
  ));
  const retryDelay = options.retryDelay ?? presenceRetryDelay;
  const log = options.log ?? (() => {});

  /** @param {string} url */
  function createEndpoint(url) {
    /** @type {EndpointPhase} */
    let phase = "idle";
    let retryAttempt = 0;
    /** @type {unknown | undefined} */
    let retryHandle;
    /** @type {ActivityReport[]} */
    let replayBuffer = [];
    let stopped = false;
    let closing = false;
    let everAcknowledged = false;
    let attemptVersion = 0;
    /** @type {Promise<void>} */
    let activeTask = Promise.resolve();
    /** @type {Promise<void>} */
    let sendChain = Promise.resolve();

    function clearRetry() {
      if (retryHandle !== undefined) {
        cancel(retryHandle);
        retryHandle = undefined;
      }
    }

    /** @param {string} reason */
    function schedulePresenceRetry(reason) {
      clearRetry();
      if (stopped || closing) {
        phase = "terminal";
        return;
      }

      const delayMs = retryDelay(retryAttempt);
      retryAttempt += 1;
      phase = "retry_wait";
      log(`Presence ${url} will retry in ${delayMs}ms: ${reason}`);
      retryHandle = schedule(() => {
        retryHandle = undefined;
        return beginPresence();
      }, delayMs);
    }

    /** @param {ActivityReport} report @returns {Promise<OrdinaryOutcome>} */
    async function sendOrdinary(report) {
      try {
        const result = await options.post(url, report);
        const outcome = classifyOrdinaryResult(result);
        if (!result.ok && outcome.kind === "sent") {
          log(`POST ${url} -> ${responseDescription(result)}`);
        }
        return outcome;
      } catch (error) {
        return { kind: "transport", reason: errorText(error) };
      }
    }

    /**
     * @param {ActivityReport[]} reports
     * @param {number} version
     */
    async function sendReplay(reports, version) {
      for (const report of reports) {
        if (stopped || version !== attemptVersion) return "cancelled";
        const outcome = await sendOrdinary(report);
        if (outcome.kind === "transport") {
          schedulePresenceRetry(outcome.reason);
          return "stopped";
        }
        if (outcome.kind === "terminal") {
          phase = "terminal";
          log(`Reporting ${url} stopped: ${outcome.reason}`);
          return "stopped";
        }
      }
      return "sent";
    }

    /** @param {number} version */
    async function replayAfterPresence(version) {
      phase = "replaying";
      replayBuffer = [];

      /** @type {ActivityReport[]} */
      let reports = [];
      try {
        const loaded = await options.loadReplayReports();
        reports = Array.isArray(loaded) ? loaded : [];
      } catch (error) {
        log(`Replay snapshot failed: ${errorText(error)}`);
      }

      if (stopped || version !== attemptVersion) return;
      if (await sendReplay(reports, version) !== "sent") return;

      while (replayBuffer.length > 0) {
        const pending = replayBuffer;
        replayBuffer = [];
        if (await sendReplay(pending, version) !== "sent") return;
      }

      if (stopped || version !== attemptVersion) return;
      phase = closing ? "terminal" : "ready";
    }

    async function attemptPresence() {
      if (
        stopped
        || closing
        || phase === "presence"
        || phase === "replaying"
        || phase === "ready"
        || phase === "terminal"
      ) {
        return;
      }

      clearRetry();
      const version = ++attemptVersion;
      phase = "presence";

      /** @type {PostResult} */
      let result;
      try {
        result = await options.post(url, options.presenceReport);
      } catch (error) {
        if (!stopped && version === attemptVersion) {
          schedulePresenceRetry(errorText(error));
        }
        return;
      }

      if (stopped || closing || version !== attemptVersion) return;

      const outcome = classifyPresenceResult(result);
      if (outcome.kind === "acknowledged") {
        everAcknowledged = true;
        retryAttempt = 0;
        await replayAfterPresence(version);
      } else if (outcome.kind === "retry") {
        schedulePresenceRetry(outcome.reason);
      } else {
        phase = "terminal";
        log(`Reporting ${url} stopped: ${outcome.reason}`);
      }
    }

    function beginPresence() {
      activeTask = attemptPresence();
      return activeTask;
    }

    /**
     * @param {ActivityReport} report
     * @param {boolean} final
     */
    function enqueueReady(report, final) {
      sendChain = sendChain.then(async () => {
        if (stopped || (!final && phase !== "ready")) return;
        if (final && !everAcknowledged) {
          phase = "terminal";
          return;
        }

        const outcome = await sendOrdinary(report);
        if (final) {
          phase = "terminal";
        } else if (outcome.kind === "transport") {
          schedulePresenceRetry(outcome.reason);
        } else if (outcome.kind === "terminal") {
          phase = "terminal";
          log(`Reporting ${url} stopped: ${outcome.reason}`);
        }
      }).catch((error) => {
        if (final || stopped || closing) {
          phase = "terminal";
        } else {
          schedulePresenceRetry(errorText(error));
        }
      });
      return sendChain;
    }

    /** @param {ActivityReport} report */
    function publish(report) {
      if (stopped || closing || phase === "terminal") return Promise.resolve();
      if (phase === "replaying") {
        replayBuffer.push(report);
        return activeTask;
      }
      if (phase === "ready") return enqueueReady(report, false);
      // While presence is pending, a fresh getEvents() plus the current-process snapshot replays it.
      return Promise.resolve();
    }

    /** @param {ActivityReport} report */
    function close(report) {
      if (stopped || closing || phase === "terminal") return activeTask;
      closing = true;
      clearRetry();

      if (phase === "replaying") {
        replayBuffer.push(report);
        return activeTask;
      }
      if (phase === "ready") return enqueueReady(report, true);

      attemptVersion += 1;
      if (!everAcknowledged) {
        phase = "terminal";
        return Promise.resolve();
      }

      phase = "closing";
      activeTask = (async () => {
        await sendOrdinary(report);
        phase = "terminal";
      })();
      return activeTask;
    }

    function stop() {
      stopped = true;
      closing = true;
      attemptVersion += 1;
      clearRetry();
      replayBuffer = [];
      phase = "terminal";
    }

    function snapshot() {
      return {
        url,
        phase,
        everAcknowledged,
        retryAttempt,
      };
    }

    function pendingTasks() {
      return [activeTask, sendChain];
    }

    return {
      start: beginPresence,
      publish,
      close,
      stop,
      snapshot,
      pendingTasks,
    };
  }

  const endpoints = [...new Set(options.urls)].map(createEndpoint);

  async function flush() {
    for (let pass = 0; pass < 20; pass += 1) {
      const before = endpoints.flatMap((endpoint) => endpoint.pendingTasks());
      await Promise.all(before);
      const after = endpoints.flatMap((endpoint) => endpoint.pendingTasks());
      if (before.every((task, index) => task === after[index])) return;
    }
    throw new Error("reporting fanout did not settle");
  }

  return {
    start: async () => {
      await Promise.all(endpoints.map((endpoint) => endpoint.start()));
    },
    /** @param {ActivityReport} report */
    publish: async (report) => {
      await Promise.all(endpoints.map((endpoint) => endpoint.publish(report)));
    },
    /** @param {ActivityReport} report */
    close: async (report) => {
      await Promise.all(endpoints.map((endpoint) => endpoint.close(report)));
    },
    stop: () => endpoints.forEach((endpoint) => endpoint.stop()),
    flush,
    snapshot: () => endpoints.map((endpoint) => endpoint.snapshot()),
  };
}

/**
 * @param {{
 *   session: {
 *     on: (type: string, handler: (event: unknown) => void) => () => void,
 *     getEvents: () => Promise<unknown[]>,
 *     rpc: { metadata: { snapshot: () => Promise<{ summary?: unknown } | null | undefined> } },
 *   },
 *   baseContext: import("./reporting-core.mjs").ReportBaseContext,
 *   activityUrls: string[],
 *   post: (url: string, report: ActivityReport) => Promise<PostResult>,
 *   randomId: () => string,
 *   nowIso?: () => string,
 *   schedule?: (callback: () => void | Promise<void>, delayMs: number) => unknown,
 *   cancel?: (handle: unknown) => void,
 *   retryDelay?: (attempt: number) => number,
 *   setInterval?: (callback: () => void, intervalMs: number) => unknown,
 *   clearInterval?: (handle: unknown) => void,
 *   heartbeatIntervalMs?: number,
 *   log?: (message: string) => void,
 * }} options
 */
export function createReportingRuntime(options) {
  const nowIso = options.nowIso ?? (() => new Date().toISOString());
  const setIntervalFn = options.setInterval
    ?? ((callback, intervalMs) => setInterval(callback, intervalMs));
  const clearIntervalFn = options.clearInterval ?? ((handle) => clearInterval(
    /** @type {ReturnType<typeof setInterval>} */ (handle),
  ));
  const heartbeatIntervalMs = options.heartbeatIntervalMs ?? HEARTBEAT_INTERVAL_MS;
  const log = options.log ?? (() => {});
  const currentProcessState = createCurrentProcessState();
  let liveTitleSeen = false;
  let started = false;
  let stopped = false;
  let closed = false;
  /** @type {unknown | undefined} */
  let heartbeatHandle;
  /** @type {(() => void)[]} */
  const unsubscribes = [];
  /** @type {Promise<void>} */
  let metadataTask = Promise.resolve();

  async function loadReplayReports() {
    /** @type {ActivityReport[]} */
    let historical = [];
    try {
      const events = await options.session.getEvents();
      historical = events
        .map((event) => reportForReplaySdkEvent(options.baseContext, event))
        .filter((report) => report !== null);
    } catch (error) {
      log(`getEvents replay failed: ${errorText(error)}`);
    }

    return mergeReplayReports(historical, currentProcessState.snapshot());
  }

  const presenceReport = buildReport(
    {
      ...options.baseContext,
      eventId: options.randomId(),
      occurredAt: nowIso(),
    },
    "session_present",
  );

  const fanout = createReportingFanout({
    urls: options.activityUrls,
    presenceReport,
    loadReplayReports,
    post: options.post,
    schedule: options.schedule,
    cancel: options.cancel,
    retryDelay: options.retryDelay,
    log,
  });

  function stopHeartbeat() {
    if (heartbeatHandle !== undefined) {
      clearIntervalFn(heartbeatHandle);
      heartbeatHandle = undefined;
    }
  }

  function unsubscribeAll() {
    while (unsubscribes.length > 0) {
      const unsubscribe = unsubscribes.pop();
      try {
        unsubscribe?.();
      } catch {
        // Best-effort teardown at the SDK boundary.
      }
    }
  }

  /** @param {unknown} event */
  function handleLiveEvent(event) {
    if (stopped || closed) return;
    const report = reportForSdkEvent(options.baseContext, event);
    if (!report) return;

    if (report.kind === "session_closed") {
      closed = true;
      stopHeartbeat();
      unsubscribeAll();
      void fanout.close(report);
      return;
    }

    if (report.kind === "title_reported") liveTitleSeen = true;
    currentProcessState.observe(report);
    void fanout.publish(report);
  }

  function heartbeatTick() {
    if (stopped || closed) return;
    void fanout.publish(buildReport(
      {
        ...options.baseContext,
        eventId: options.randomId(),
        occurredAt: nowIso(),
      },
      "heartbeat",
    ));
  }

  async function bootstrapTitle() {
    try {
      const snapshot = await options.session.rpc.metadata.snapshot();
      if (stopped || closed || liveTitleSeen) return;

      const report = buildNonBlankMessageReport(
        {
          ...options.baseContext,
          eventId: options.randomId(),
          occurredAt: nowIso(),
        },
        "title_bootstrap",
        snapshot?.summary,
      );
      if (report) {
        currentProcessState.observe(report);
        await fanout.publish(report);
      }
    } catch (error) {
      log(`metadata title bootstrap failed: ${errorText(error)}`);
    }
  }

  async function start() {
    if (started || stopped) return;
    started = true;

    for (const type of SUBSCRIBED_TYPES) {
      unsubscribes.push(options.session.on(type, handleLiveEvent));
    }

    await fanout.start();
    if (stopped || closed) return;

    heartbeatHandle = setIntervalFn(heartbeatTick, heartbeatIntervalMs);
    metadataTask = bootstrapTitle();
    void metadataTask;
  }

  function stop() {
    if (stopped) return;
    stopped = true;
    stopHeartbeat();
    unsubscribeAll();
    if (!closed) fanout.stop();
  }

  async function flush() {
    await metadataTask;
    await fanout.flush();
  }

  return {
    start,
    stop,
    flush,
    snapshot: () => ({
      closed,
      stopped,
      heartbeatRunning: heartbeatHandle !== undefined,
      endpoints: fanout.snapshot(),
    }),
  };
}
