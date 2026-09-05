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

function sdkEvent(id, timestamp, type, data = {}) {
  return { id, timestamp, type, data };
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

function createManualHeartbeat(order = []) {
  let callback = null;

  return {
    setInterval(next) {
      callback = next;
      order.push("heartbeat-start");
      return { kind: "heartbeat" };
    },
    clearInterval() {
      callback = null;
      order.push("heartbeat-stop");
    },
    tick() {
      callback?.();
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

  assert.equal(fake.getEventsCalls(), 3);
  assert.equal(calls.every(({ report }) => report.parentProcessId === 777), true);
  assert.deepEqual(
    runtime.snapshot().endpoints.map(({ phase }) => phase),
    ["ready", "ready", "ready", "terminal", "terminal", "terminal"],
  );
  runtime.stop();
});

test("an active endpoint re-establishes presence and replays after transport loss", async () => {
  let history = [
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

  heartbeat.tick();
  await runtime.flush();
  assert.equal(runtime.snapshot().endpoints[0].phase, "retry_wait");
  assert.equal(scheduler.pending().length, 1);

  history = [
    ...history,
    sdkEvent("waiting", "2026-09-04T16:30:01.000Z", "user_input.requested", {
      question: "Continue?",
    }),
    sdkEvent("background-live", "2026-09-04T16:30:02.000Z", "subagent.started", {
      toolCallId: "tool-reconnect",
    }),
  ];
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
  assert.equal(fake.getEventsCalls(), 2);
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

test("live shutdown stops heartbeats before exact closure and historical shutdown is ignored", async () => {
  const order = [];
  const fake = createFakeSession(async () => [
    sdkEvent("old-shutdown", "2026-09-04T15:59:00.000Z", "session.shutdown", {
      shutdownType: "routine",
    }),
    sdkEvent("resumed-working", "2026-09-04T16:00:00.000Z", "assistant.turn_start"),
  ]);
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

  assert.equal(calls.some(({ report }) => report.eventId === "old-shutdown"), false);
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
