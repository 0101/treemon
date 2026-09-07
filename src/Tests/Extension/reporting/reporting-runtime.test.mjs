import test from "node:test";
import assert from "node:assert/strict";
import {
  classifyPresenceResult,
  createReportingRuntime,
} from "../../../Extension/reporting/reporting-runtime.mjs";

const baseContext = {
  parentProcessId: 777,
  sessionId: "session-runtime",
  terminalSessionId: "0123456789abcdef0123456789abcdef",
  worktreePath: "Q:/code/runtime",
  provider: "copilot_cli",
};

const acknowledged = {
  ok: true,
  status: 200,
  body: { recorded: true, monitored: true, retryable: false },
};

const runtimeNow = Date.parse("2026-09-04T17:00:00.000Z");

function sdkEvent(id, timestamp, type, data = {}) {
  return { id, timestamp, type, data };
}

function deferred() {
  let resolve;
  const promise = new Promise((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function createFakeSession(historyProvider = async () => [], summaryProvider = async () => ({})) {
  const handlers = new Map();
  let getEventsCalls = 0;

  return {
    session: {
      on(type, handler) {
        const current = handlers.get(type) ?? new Set();
        current.add(handler);
        handlers.set(type, current);
        return () => current.delete(handler);
      },
      async getEvents() {
        getEventsCalls += 1;
        return historyProvider();
      },
      rpc: {
        metadata: {
          snapshot: summaryProvider,
        },
      },
    },
    emit(event) {
      for (const handler of [...(handlers.get(event.type) ?? [])]) handler(event);
    },
    getEventsCalls: () => getEventsCalls,
  };
}

function createManualScheduler() {
  const scheduled = [];

  return {
    schedule(callback, delayMs) {
      const item = { callback, delayMs, cancelled: false };
      scheduled.push(item);
      return item;
    },
    cancel(item) {
      item.cancelled = true;
    },
    pending() {
      return scheduled.filter((item) => !item.cancelled);
    },
    async runAll() {
      const ready = this.pending();
      ready.forEach((item) => {
        item.cancelled = true;
      });
      await Promise.all(ready.map((item) => item.callback()));
    },
  };
}

function createManualHeartbeat(order = [], onStart = () => {}) {
  const handles = [];
  const active = () => handles.filter((handle) => !handle.cancelled);

  return {
    setInterval(callback, intervalMs) {
      const handle = { callback, intervalMs, cancelled: false };
      handles.push(handle);
      order.push("heartbeat-start");
      onStart(handle);
      return handle;
    },
    clearInterval(handle) {
      if (!handle.cancelled) {
        handle.cancelled = true;
        order.push("heartbeat-stop");
      }
    },
    async tick() {
      await Promise.all(active().map((handle) => handle.callback()));
    },
  };
}

function runtimeOptions(fake, post, scheduler, heartbeat, activityUrls) {
  let generatedId = 0;
  return {
    session: fake.session,
    baseContext,
    activityUrls,
    post,
    randomId: () => `generated-${++generatedId}`,
    now: () => runtimeNow,
    nowIso: () => "2026-09-04T17:00:00.000Z",
    schedule: scheduler.schedule.bind(scheduler),
    cancel: scheduler.cancel.bind(scheduler),
    retryDelay: () => 1,
    setInterval: heartbeat.setInterval.bind(heartbeat),
    clearInterval: heartbeat.clearInterval.bind(heartbeat),
    log: () => {},
  };
}

test("presence retries only transient transport and explicit retry acknowledgements", () => {
  assert.deepEqual(classifyPresenceResult(acknowledged), { kind: "acknowledged" });
  assert.deepEqual(
    classifyPresenceResult({
      ok: true,
      status: 200,
      body: { recorded: false, monitored: true, retryable: true, reason: "store busy" },
    }),
    { kind: "retry", reason: "store busy" },
  );
  assert.deepEqual(
    classifyPresenceResult({
      ok: true,
      status: 200,
      body: { recorded: false, monitored: false, retryable: true },
    }),
    { kind: "terminal", reason: "worktree is not monitored by this endpoint" },
  );
  assert.deepEqual(
    classifyPresenceResult({
      ok: true,
      status: 200,
      body: { recorded: false, monitored: true },
    }),
    { kind: "terminal", reason: "presence was not recorded" },
  );
  assert.deepEqual(
    classifyPresenceResult({ ok: false, status: 400, statusText: "Bad Request" }),
    { kind: "terminal", reason: "HTTP 400 Bad Request" },
  );
  assert.deepEqual(
    classifyPresenceResult({ ok: false, status: 503, statusText: "Unavailable" }),
    { kind: "retry", reason: "HTTP 503 Unavailable" },
  );
});

test("fan-out presence, replay, and retry state are independent per endpoint", async () => {
  const history = [
    sdkEvent("turn-end", "2026-09-04T16:00:00.000Z", "assistant.turn_end"),
    sdkEvent("wait", "2026-09-04T16:00:01.000Z", "elicitation.requested", {
      message: "Choose a recovery",
    }),
    sdkEvent("background", "2026-09-04T16:00:02.000Z", "subagent.started", {
      toolCallId: "tool-background",
    }),
  ];
  const fake = createFakeSession(async () => history);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const urls = {
    monitored: "http://127.0.0.1:5101/api/session/activity",
    transport: "http://127.0.0.1:5102/api/session/activity",
    retryable: "http://127.0.0.1:5103/api/session/activity",
    unmonitored: "http://127.0.0.1:5104/api/session/activity",
    invalidProcess: "http://127.0.0.1:5105/api/session/activity",
    pidless: "http://127.0.0.1:5106/api/session/activity",
  };
  const presenceAttempts = new Map();
  const calls = [];

  const post = async (url, report) => {
    calls.push({ url, report });
    if (report.kind !== "session_present") return acknowledged;

    const attempt = (presenceAttempts.get(url) ?? 0) + 1;
    presenceAttempts.set(url, attempt);
    if (url === urls.transport && attempt === 1) throw new Error("connection refused");
    if (url === urls.retryable && attempt === 1) {
      return {
        ok: true,
        status: 200,
        body: { recorded: false, monitored: true, retryable: true, reason: "mailbox timeout" },
      };
    }
    if (url === urls.unmonitored) {
      return {
        ok: true,
        status: 200,
        body: { recorded: false, monitored: false, retryable: false },
      };
    }
    if (url === urls.invalidProcess) {
      return {
        ok: true,
        status: 200,
        body: {
          recorded: false,
          monitored: true,
          retryable: false,
          reason: "the parent Copilot process is not running",
        },
      };
    }
    if (url === urls.pidless) {
      return { ok: false, status: 400, statusText: "Bad Request" };
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    Object.values(urls),
  ));
  await runtime.start();
  await runtime.flush();

  assert.deepEqual(
    runtime.snapshot().endpoints.map(({ url, phase }) => [url, phase]),
    [
      [urls.monitored, "ready"],
      [urls.transport, "retry_wait"],
      [urls.retryable, "retry_wait"],
      [urls.unmonitored, "terminal"],
      [urls.invalidProcess, "terminal"],
      [urls.pidless, "terminal"],
    ],
  );
  assert.equal(scheduler.pending().length, 2);

  const liveIntent = sdkEvent(
    "live-intent",
    "2026-09-04T16:00:03.000Z",
    "assistant.intent",
    { intent: "Waiting on background verification" },
  );
  fake.emit(liveIntent);
  await runtime.flush();

  assert.equal(
    calls.some(({ url, report }) => url === urls.monitored && report.eventId === "live-intent"),
    true,
  );
  assert.equal(
    calls.some(({ url, report }) => url === urls.transport && report.eventId === "live-intent"),
    false,
  );

  await scheduler.runAll();
  await runtime.flush();

  for (const url of [urls.transport, urls.retryable]) {
    const reports = calls.filter((call) => call.url === url).map((call) => call.report);
    const presenceReports = reports.filter((report) => report.kind === "session_present");
    assert.equal(presenceReports.length, 2);
    assert.equal(new Set(presenceReports.map((report) => report.eventId)).size, 1);
    assert.deepEqual(
      reports
        .filter((report) => report.kind !== "session_present")
        .map((report) => report.kind),
      [
        "turn_ended",
        "awaiting_user_input",
        "background_agent_started",
        "intent_reported",
      ],
    );
  }

  assert.equal(fake.getEventsCalls(), 1);
  assert.equal(calls.every(({ report }) => report.parentProcessId === 777), true);
  assert.deepEqual(
    runtime.snapshot().endpoints.map(({ phase }) => phase),
    ["ready", "ready", "ready", "terminal", "terminal", "terminal"],
  );
  runtime.stop();
});

test("two endpoints and a reconnect reuse one bounded historical replay snapshot", async () => {
  const at = (offsetSeconds) => new Date(runtimeNow + (offsetSeconds * 1000)).toISOString();
  const assistantHistory = Array.from({ length: 60 }, (_, index) => sdkEvent(
    `assistant-${index}`,
    at(-150 + index),
    "assistant.message",
    { content: `Assistant message ${index}` },
  ));
  const history = [
    sdkEvent("old-background-start", at(-420), "subagent.started", {
      toolCallId: "old-background",
    }),
    sdkEvent("old-background-finish", at(-410), "subagent.completed", {
      toolCallId: "old-background",
    }),
    sdkEvent("active-background-finish", at(-390), "subagent.completed", {
      toolCallId: "active-background",
    }),
    sdkEvent("active-background-start", at(-380), "subagent.started", {
      toolCallId: "active-background",
    }),
    sdkEvent("start-only-background", at(-370), "subagent.started", {
      toolCallId: "start-only",
    }),
    sdkEvent("finish-only-background", at(-360), "subagent.failed", {
      toolCallId: "finish-only",
    }),
    sdkEvent("turn-old", at(-250), "assistant.turn_start"),
    sdkEvent("turn-current", at(-240), "assistant.turn_end"),
    sdkEvent("skill-old", at(-230), "skill.invoked", { name: "old-skill" }),
    sdkEvent("skill-current", at(-220), "skill.invoked", { name: "current-skill" }),
    sdkEvent("title-old", at(-210), "session.title_changed", { title: "Old title" }),
    sdkEvent("title-current", at(-200), "session.title_changed", { title: "Current title" }),
    sdkEvent("intent-old", at(-190), "assistant.intent", { intent: "Old intent" }),
    sdkEvent("intent-current", at(-180), "assistant.intent", { intent: "Current intent" }),
    sdkEvent("user-old", at(-170), "user.message", { content: "Old prompt" }),
    sdkEvent("user-current", at(-160), "user.message", { content: "Current prompt" }),
    ...assistantHistory,
    sdkEvent("ask-old", at(-80), "user_input.requested", { question: "Old question?" }),
    sdkEvent("ask-current", at(-70), "user_input.requested", {
      question: "Current question?",
    }),
    sdkEvent("answer-old", at(-60), "user_input.completed"),
    sdkEvent("answer-current", at(-50), "user_input.completed"),
    sdkEvent("usage-old", at(-40), "session.usage_info", {
      currentTokens: 20,
      tokenLimit: 100,
    }),
    sdkEvent("usage-current", at(-30), "session.usage_info", {
      currentTokens: 30,
      tokenLimit: 100,
    }),
    sdkEvent("recent-background-start", at(-20), "subagent.started", {
      toolCallId: "recent-background",
    }),
    sdkEvent("recent-background-finish", at(-10), "subagent.completed", {
      toolCallId: "recent-background",
    }),
    sdkEvent("historical-shutdown", at(-5), "session.shutdown"),
  ];
  const expectedHistoricalIds = [
    "active-background-finish",
    "active-background-start",
    "start-only-background",
    "finish-only-background",
    "turn-current",
    "skill-current",
    "title-current",
    "intent-current",
    "user-current",
    "assistant-59",
    "ask-current",
    "answer-current",
    "usage-current",
    "recent-background-start",
    "recent-background-finish",
  ];
  const fake = createFakeSession(async () => history);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const urls = [
    "http://127.0.0.1:5151/api/session/activity",
    "http://127.0.0.1:5152/api/session/activity",
  ];
  const calls = [];
  let rejectReconnectTrigger = true;

  const post = async (url, report) => {
    calls.push({ url, report });
    if (
      url === urls[0]
      && report.eventId === "live-reconnect"
      && rejectReconnectTrigger
    ) {
      rejectReconnectTrigger = false;
      throw new Error("endpoint restarted");
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    urls,
  ));
  await runtime.start();
  await runtime.flush();

  assert.ok(history.length > expectedHistoricalIds.length * 4);
  for (const url of urls) {
    assert.deepEqual(
      calls
        .filter((call) => call.url === url && call.report.kind !== "session_present")
        .map((call) => call.report.eventId),
      expectedHistoricalIds,
    );
  }
  assert.equal(fake.getEventsCalls(), 1);

  fake.emit(sdkEvent(
    "live-reconnect",
    at(1),
    "assistant.intent",
    { intent: "Reconnect now" },
  ));
  await runtime.flush();
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");

  const reconnectStart = calls.length;
  await scheduler.runAll();
  await runtime.flush();

  assert.deepEqual(
    calls
      .slice(reconnectStart)
      .filter((call) => call.url === urls[0] && call.report.kind !== "session_present")
      .map((call) => call.report.eventId),
    [...expectedHistoricalIds, "live-reconnect"],
  );
  assert.equal(fake.getEventsCalls(), 1);
  assert.equal(runtime.snapshot().endpoints[0].phase, "ready");
  runtime.stop();
});

test("a failed historical snapshot load is retried until one succeeds", async () => {
  let historyAttempt = 0;
  const fake = createFakeSession(async () => {
    historyAttempt += 1;
    if (historyAttempt === 1) throw new Error("history unavailable");
    return [
      sdkEvent("historical-turn", "2026-09-04T16:30:00.000Z", "assistant.turn_start"),
    ];
  });
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5171/api/session/activity";
  const calls = [];
  let rejectLiveTurn = true;

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.eventId === "live-after-history-failure" && rejectLiveTurn) {
      rejectLiveTurn = false;
      throw new Error("reconnect");
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();
  assert.equal(fake.getEventsCalls(), 1);

  fake.emit(sdkEvent(
    "live-after-history-failure",
    "2026-09-04T16:45:00.000Z",
    "assistant.turn_start",
  ));
  await runtime.flush();
  await scheduler.runAll();
  await runtime.flush();

  const secondPresence = calls
    .map(({ report }) => report.kind)
    .lastIndexOf("session_present");
  assert.deepEqual(
    calls.slice(secondPresence + 1).map(({ report }) => report.eventId),
    ["historical-turn", "live-after-history-failure"],
  );
  assert.equal(fake.getEventsCalls(), 2);
  runtime.stop();
});

test("an acknowledged endpoint heartbeats while a peer presence attempt is slow", async () => {
  const slowPresence = deferred();
  const historicalReplay = deferred();
  const heartbeatStarted = deferred();
  const fake = createFakeSession(() => historicalReplay.promise);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat([], () => heartbeatStarted.resolve());
  const urls = [
    "http://127.0.0.1:5181/api/session/activity",
    "http://127.0.0.1:5182/api/session/activity",
  ];
  const calls = [];

  const post = async (url, report) => {
    calls.push({ url, report });
    if (url === urls[1] && report.kind === "session_present") {
      return slowPresence.promise;
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    urls,
  ));
  const startTask = runtime.start();
  await heartbeatStarted.promise;

  assert.deepEqual(
    runtime.snapshot().endpoints.map(({ phase, heartbeatRunning }) => ({
      phase,
      heartbeatRunning,
    })),
    [
      { phase: "replaying", heartbeatRunning: true },
      { phase: "presence", heartbeatRunning: false },
    ],
  );

  await heartbeat.tick();
  assert.equal(
    calls.some(({ url, report }) => url === urls[0] && report.kind === "heartbeat"),
    true,
  );
  assert.equal(
    calls.some(({ url, report }) => url === urls[1] && report.kind === "heartbeat"),
    false,
  );

  historicalReplay.resolve([]);
  slowPresence.resolve(acknowledged);
  await startTask;
  await runtime.flush();
  runtime.stop();
});

test("heartbeat failure restarts presence without a slow reconnect replay winning the race", async () => {
  const slowReplay = deferred();
  const replayStarted = deferred();
  const fake = createFakeSession(async () => [
    sdkEvent("historical-working", "2026-09-04T16:30:00.000Z", "assistant.turn_start"),
  ]);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5191/api/session/activity";
  const calls = [];
  let presenceAttempts = 0;
  let rejectReconnectTrigger = true;
  let rejectReconnectHeartbeat = true;
  let slowReplayCallIndex = -1;

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.kind === "session_present") {
      presenceAttempts += 1;
      return acknowledged;
    }
    if (report.eventId === "trigger-reconnect" && rejectReconnectTrigger) {
      rejectReconnectTrigger = false;
      throw new Error("connection reset");
    }
    if (report.eventId === "historical-working" && presenceAttempts === 2) {
      slowReplayCallIndex = calls.length - 1;
      replayStarted.resolve();
      return slowReplay.promise;
    }
    if (
      report.kind === "heartbeat"
      && presenceAttempts === 2
      && rejectReconnectHeartbeat
    ) {
      rejectReconnectHeartbeat = false;
      throw new Error("heartbeat failed");
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();

  fake.emit(sdkEvent(
    "trigger-reconnect",
    "2026-09-04T16:45:00.000Z",
    "assistant.intent",
    { intent: "Reconnect" },
  ));
  await runtime.flush();
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");

  const reconnectTask = scheduler.runAll();
  await replayStarted.promise;
  assert.equal(runtime.snapshot().endpoints[0].phase, "replaying");
  assert.equal(runtime.snapshot().endpoints[0].heartbeatRunning, true);

  await heartbeat.tick();
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");
  assert.equal(runtime.snapshot().endpoints[0].heartbeatRunning, false);
  assert.equal(scheduler.pending().length, 1);

  const heartbeatCall = calls.findIndex(({ report }) => report.kind === "heartbeat");
  assert.ok(heartbeatCall > slowReplayCallIndex);

  slowReplay.resolve(acknowledged);
  await reconnectTask;
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");

  await scheduler.runAll();
  await runtime.flush();
  assert.equal(presenceAttempts, 3);
  assert.equal(fake.getEventsCalls(), 1);
  assert.equal(runtime.snapshot().endpoints[0].phase, "ready");
  assert.equal(runtime.snapshot().endpoints[0].heartbeatRunning, true);
  runtime.stop();
});

test("an active endpoint re-establishes presence and replays after transport loss", async () => {
  const history = [
    sdkEvent("working", "2026-09-04T16:30:00.000Z", "assistant.turn_start"),
  ];
  const fake = createFakeSession(async () => history);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5201/api/session/activity";
  const calls = [];
  let failHeartbeat = true;

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.kind === "heartbeat" && failHeartbeat) {
      failHeartbeat = false;
      throw new Error("server restarted");
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();

  await heartbeat.tick();
  await runtime.flush();
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");
  assert.equal(scheduler.pending().length, 1);

  fake.emit(sdkEvent(
    "waiting",
    "2026-09-04T16:30:01.000Z",
    "user_input.requested",
    { question: "Continue?" },
  ));
  fake.emit(sdkEvent(
    "background-live",
    "2026-09-04T16:30:02.000Z",
    "subagent.started",
    { toolCallId: "tool-reconnect" },
  ));
  await scheduler.runAll();
  await runtime.flush();

  const kinds = calls.map(({ report }) => report.kind);
  const secondPresence = kinds.lastIndexOf("session_present");
  assert.ok(secondPresence > 0);
  assert.equal(
    new Set(
      calls
        .filter(({ report }) => report.kind === "session_present")
        .map(({ report }) => report.eventId),
    ).size,
    1,
  );
  assert.deepEqual(
    kinds.slice(secondPresence + 1),
    ["turn_started", "awaiting_user_input", "background_agent_started"],
  );
  assert.equal(fake.getEventsCalls(), 1);
  assert.equal(runtime.snapshot().endpoints[0].phase, "ready");
  runtime.stop();
});

test("a retryable ordinary rejection re-establishes presence and replays the report", async () => {
  let history = [];
  const fake = createFakeSession(async () => history);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5251/api/session/activity";
  const calls = [];
  let rejectLiveTurn = true;

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.kind === "turn_started" && rejectLiveTurn) {
      rejectLiveTurn = false;
      return {
        ok: true,
        status: 200,
        body: {
          recorded: false,
          monitored: true,
          retryable: true,
          reason: "mailbox unavailable",
        },
      };
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();

  const liveTurn = sdkEvent(
    "live-turn",
    "2026-09-04T16:45:00.000Z",
    "assistant.turn_start",
  );
  history = [liveTurn];
  fake.emit(liveTurn);
  await runtime.flush();

  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");
  assert.equal(scheduler.pending().length, 1);

  await scheduler.runAll();
  await runtime.flush();

  const presenceReports = calls.filter(({ report }) => report.kind === "session_present");
  const turnReports = calls.filter(({ report }) => report.kind === "turn_started");
  assert.equal(presenceReports.length, 2);
  assert.equal(new Set(presenceReports.map(({ report }) => report.eventId)).size, 1);
  assert.deepEqual(turnReports.map(({ report }) => report.eventId), ["live-turn", "live-turn"]);
  assert.equal(fake.getEventsCalls(), 1);
  assert.equal(runtime.snapshot().endpoints[0].phase, "ready");
  runtime.stop();
});

test("ordinary ignored responses stay ready while unmonitored responses terminate", async () => {
  const fake = createFakeSession();
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5252/api/session/activity";
  const calls = [];

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.kind === "turn_started") {
      return {
        ok: true,
        status: 200,
        body: { recorded: false, monitored: true, retryable: false },
      };
    }
    if (report.kind === "turn_ended") {
      return {
        ok: true,
        status: 200,
        body: {
          recorded: false,
          monitored: false,
          retryable: false,
          reason: "worktree removed",
        },
      };
    }
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();

  fake.emit(sdkEvent(
    "ignored-turn",
    "2026-09-04T16:50:00.000Z",
    "assistant.turn_start",
  ));
  await runtime.flush();
  assert.equal(runtime.snapshot().endpoints[0].phase, "ready");

  fake.emit(sdkEvent(
    "unmonitored-turn",
    "2026-09-04T16:50:01.000Z",
    "assistant.turn_end",
  ));
  await runtime.flush();

  assert.equal(runtime.snapshot().endpoints[0].phase, "terminal");
  assert.equal(scheduler.pending().length, 0);
  const callCount = calls.length;

  fake.emit(sdkEvent(
    "after-terminal",
    "2026-09-04T16:50:02.000Z",
    "assistant.turn_start",
  ));
  await runtime.flush();
  assert.equal(calls.length, callCount);
  runtime.stop();
});

test("shutdown waits for an in-flight presence acknowledgement before exact closure", async () => {
  const pendingPresence = deferred();
  const fake = createFakeSession();
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5291/api/session/activity";
  const calls = [];

  const post = async (target, report) => {
    calls.push({ url: target, report });
    return report.kind === "session_present" ? pendingPresence.promise : acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  const startTask = runtime.start();

  assert.deepEqual(calls.map(({ report }) => report.kind), ["session_present"]);
  fake.emit(sdkEvent(
    "shutdown-during-presence",
    "2026-09-04T17:00:01.000Z",
    "session.shutdown",
  ));
  assert.equal(runtime.snapshot().endpoints[0].phase, "presence");

  pendingPresence.resolve(acknowledged);
  await startTask;
  await runtime.flush();

  assert.deepEqual(
    calls.map(({ report }) => report.kind),
    ["session_present", "session_closed"],
  );
  assert.equal(runtime.snapshot().closed, true);
  assert.equal(runtime.snapshot().endpoints[0].phase, "terminal");
});

test("shutdown retries ambiguous presence with the same event before exact closure", async () => {
  const pendingPresence = deferred();
  const fake = createFakeSession();
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5292/api/session/activity";
  const calls = [];
  let presenceAttempts = 0;

  const post = async (target, report) => {
    calls.push({ url: target, report });
    if (report.kind !== "session_present") return acknowledged;

    presenceAttempts += 1;
    return presenceAttempts === 1 ? pendingPresence.promise : acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  const startTask = runtime.start();

  fake.emit(sdkEvent(
    "shutdown-before-presence-retry",
    "2026-09-04T17:00:01.000Z",
    "session.shutdown",
  ));
  pendingPresence.resolve({
    ok: false,
    status: 503,
    statusText: "Unavailable",
  });
  await startTask;

  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");
  assert.equal(scheduler.pending().length, 1);
  assert.deepEqual(calls.map(({ report }) => report.kind), ["session_present"]);

  await scheduler.runAll();
  await runtime.flush();

  assert.deepEqual(
    calls.map(({ report }) => report.kind),
    ["session_present", "session_present", "session_closed"],
  );
  const presenceReports = calls.filter(({ report }) => report.kind === "session_present");
  assert.equal(new Set(presenceReports.map(({ report }) => report.eventId)).size, 1);
  assert.equal(runtime.snapshot().closed, true);
  assert.equal(runtime.snapshot().endpoints[0].phase, "terminal");
});

test("resumed startup reports the selected identity first and ignores historical shutdown", async () => {
  const selectedSessionId = "selected-durable-session";
  const fake = createFakeSession(async () => [
    sdkEvent("old-shutdown", "2026-09-04T15:59:00.000Z", "session.shutdown", {
      shutdownType: "routine",
    }),
    sdkEvent("resumed-idle", "2026-09-04T16:00:00.000Z", "session.idle"),
  ]);
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat();
  const url = "http://127.0.0.1:5301/api/session/activity";
  const calls = [];

  const post = async (target, report) => {
    calls.push({ url: target, report });
    return acknowledged;
  };

  const options = runtimeOptions(fake, post, scheduler, heartbeat, [url]);
  options.baseContext = { ...baseContext, sessionId: selectedSessionId };
  const runtime = createReportingRuntime(options);

  await runtime.start();
  await runtime.flush();

  assert.deepEqual(
    calls.map(({ report }) => report.kind),
    ["session_present", "went_idle"],
  );
  assert.equal(calls[0].report.sessionId, selectedSessionId);
  assert.ok(calls.every(({ report }) => report.sessionId === selectedSessionId));
  assert.equal(runtime.snapshot().closed, false);
  assert.equal(runtime.snapshot().heartbeatRunning, true);

  runtime.stop();
});

test("live shutdown stops heartbeats before exact closure", async () => {
  const order = [];
  const fake = createFakeSession();
  const scheduler = createManualScheduler();
  const heartbeat = createManualHeartbeat(order);
  const url = "http://127.0.0.1:5301/api/session/activity";
  const calls = [];

  const post = async (target, report) => {
    calls.push({ url: target, report });
    order.push(`post:${report.kind}`);
    return acknowledged;
  };

  const runtime = createReportingRuntime(runtimeOptions(
    fake,
    post,
    scheduler,
    heartbeat,
    [url],
  ));
  await runtime.start();
  await runtime.flush();

  order.length = 0;

  fake.emit(sdkEvent(
    "live-shutdown",
    "2026-09-04T17:01:00.000Z",
    "session.shutdown",
    { shutdownType: "routine" },
  ));
  runtime.stop();
  await runtime.flush();

  assert.ok(order.indexOf("heartbeat-stop") >= 0);
  assert.ok(order.indexOf("post:session_closed") > order.indexOf("heartbeat-stop"));
  assert.equal(
    calls.filter(({ report }) => report.kind === "session_closed").length,
    1,
  );
  assert.equal(runtime.snapshot().closed, true);
  assert.equal(runtime.snapshot().heartbeatRunning, false);
  assert.equal(runtime.snapshot().endpoints[0].phase, "terminal");

  heartbeat.tick();
  await runtime.flush();
  assert.equal(calls.some(({ report }) => report.kind === "heartbeat"), false);
});
