import test from "node:test";
import assert from "node:assert/strict";
import {
  classifyPresenceResult,
  createReportingRuntime,
  presenceRetryDelay,
} from "../../../Extension/reporting/reporting-runtime.mjs";

const baseContext = {
  parentProcessId: 777,
  sessionId: "session-runtime",
  terminalSessionId: "0123456789abcdef0123456789abcdef",
  worktreePath: "Q:/code/runtime",
  provider: "copilot_cli",
};

const activityUrl = "http://127.0.0.1:5199/api/session/activity";
const runtimeNow = Date.parse("2026-09-04T17:00:00.000Z");
const runtimeNowIso = "2026-09-04T17:00:00.000Z";

const answer = (body) => ({ ok: true, status: 200, body });
const acknowledged = answer({ recorded: true, monitored: true, retryable: false });

/** Scripted transport replies, addressed by the short tokens used in the scenario table. */
const replies = {
  ack: acknowledged,
  retryable: answer({ recorded: false, monitored: true, retryable: true, reason: "mailbox busy" }),
  ignored: answer({ recorded: false, monitored: true }),
  unmonitored: answer({ recorded: false, monitored: false, retryable: false }),
  deadParent: answer({
    recorded: false, monitored: true, reason: "the parent Copilot process is not running",
  }),
  unavailable: { ok: false, status: 503, statusText: "Unavailable" },
};

const at = (offsetSeconds) => new Date(runtimeNow + (offsetSeconds * 1000)).toISOString();
const ev = (id, offsetSeconds, type, data = {}) => ({ id, timestamp: at(offsetSeconds), type, data });

function deferred() {
  let resolve;
  const promise = new Promise((resolvePromise) => { resolve = resolvePromise; });
  return { promise, resolve };
}

/**
 * One manual timer lane holding the single live handle the runtime keeps for it: presence retries
 * fire once (`oneShot`), heartbeats until cleared. Registration and cancellation are appended to
 * the shared `order` log, and `started` resolves on the first registration.
 */
function createManualTimers(label, order, oneShot = false) {
  let current = null;
  const started = deferred();

  return {
    started: started.promise,
    pending: () => (current ? [current] : []),
    add: (callback) => {
      current = { callback };
      order.push(`${label}-start`);
      started.resolve();
      return current;
    },
    cancel: (handle) => {
      if (current !== handle) return;
      current = null;
      order.push(`${label}-stop`);
    },
    run: async () => {
      const ready = current;
      if (oneShot) current = null;
      if (ready) await ready.callback();
    },
  };
}

/**
 * One runtime wired to a fake SDK session, transport, scheduler and heartbeat lane. `order`
 * interleaves timer changes with posts; `sent` records every report the runtime posted.
 */
function createFixture({
  history = async () => [], post = async () => acknowledged, metadata = async () => ({}),
  baseContext: contextOverride,
} = {}) {
  const order = [];
  const sent = [];
  const handlers = [];
  const scheduler = createManualTimers("retry", order, true);
  const heartbeat = createManualTimers("heartbeat", order);
  let historyReads = 0;
  let generatedId = 0;

  const runtime = createReportingRuntime({
    session: {
      on: (type, handler) => {
        const entry = { type, handler, cancelled: false };
        handlers.push(entry);
        return () => { entry.cancelled = true; };
      },
      getEvents: () => { historyReads += 1; return history(); },
      rpc: { metadata: { snapshot: metadata } },
    },
    baseContext: { ...baseContext, ...contextOverride },
    activityUrl,
    post: async (url, report) => {
      assert.equal(url, activityUrl);
      sent.push(report);
      order.push(`post:${report.kind}`);
      return post(report);
    },
    randomId: () => `generated-${++generatedId}`,
    now: () => runtimeNow, nowIso: () => runtimeNowIso, retryDelay: () => 1,
    schedule: scheduler.add, cancel: scheduler.cancel,
    setInterval: heartbeat.add, clearInterval: heartbeat.cancel,
    log: () => {},
  });

  return {
    runtime, scheduler, heartbeat, sent, order,
    emit: (event) => handlers.filter((entry) => !entry.cancelled && entry.type === event.type)
      .forEach((entry) => entry.handler(event)),
    historyReads: () => historyReads,
  };
}

const identityOf = (report) => `${report.parentProcessId}|${report.sessionId}`
  + `|${report.terminalSessionId}|${report.worktreePath}|${report.provider}`;
const presenceSent = "session_present:generated-1";
const presenceOnly = [presenceSent];

/**
 * One normalized observation of the runtime. Only the keys named by `expected` are compared, so
 * checkpoints stay narrow.
 */
function assertObservation(fixture, expected) {
  const { state, closed, retryAttempt, heartbeatRunning } = fixture.runtime.snapshot();
  const observation = {
    state, retries: retryAttempt, heartbeat: heartbeatRunning, closed, reports: fixture.sent,
    sent: fixture.sent.map((report) => `${report.kind}:${report.eventId}`),
    historyReads: fixture.historyReads(), pending: fixture.scheduler.pending().length,
    identities: [...new Set(fixture.sent.map(identityOf))],
  };

  assert.deepEqual(
    Object.fromEntries(Object.keys(expected).map((key) => [key, observation[key]])),
    expected,
  );
}

const expect = (observation) => ({ expect: observation });

/**
 * Replies to each report from the scenario's script: `presence` for presence attempts, `ordinary`
 * keyed by event id or report kind. A script is one token or a per-attempt token list whose last
 * token repeats; `throw` raises a transport failure.
 */
function scriptedPost({ presence = "ack", ordinary = {} }) {
  // Attempt counters per script key: a scripted transport is stateful by nature.
  const attempts = new Map();

  return async (report) => {
    const [key, script] = report.kind === "session_present"
      ? ["presence", presence]
      : [report.eventId, ordinary[report.eventId] ?? ordinary[report.kind] ?? "ack"];
    const tokens = Array.isArray(script) ? script : [script];
    const attempt = attempts.get(key) ?? 0;
    attempts.set(key, attempt + 1);
    const token = tokens[Math.min(attempt, tokens.length - 1)];
    if (token === "throw") throw new Error("connection refused");
    return replies[token];
  };
}

/**
 * Drives one runtime through scripted history reads, endpoint responses, live events and
 * scheduler/heartbeat ticks. A step is `"retry"`, `"heartbeat"`, an SDK event to emit, or an
 * `expect(...)` checkpoint; `expected` is asserted once every step has settled.
 */
async function runScenario(scenario) {
  const reads = scenario.history ?? [[]];
  // Cursor over the scripted history reads: one read is consumed per getEvents() call.
  let readIndex = 0;

  const fixture = createFixture({
    baseContext: scenario.baseContext,
    metadata: scenario.metadata,
    history: async () => {
      const read = reads[Math.min(readIndex++, reads.length - 1)];
      if (read === "fail") throw new Error("history unavailable");
      return read;
    },
    post: scriptedPost(scenario),
  });
  const { runtime } = fixture;

  await runtime.start();
  await runtime.flush();

  for (const step of scenario.steps ?? []) {
    if (step.expect) {
      assertObservation(fixture, step.expect);
      continue;
    }
    if (step === "retry") await fixture.scheduler.run();
    else if (step === "heartbeat") await fixture.heartbeat.run();
    else fixture.emit(step);
    await runtime.flush();
  }

  assertObservation(fixture, {
    state: "ready", retries: 0, heartbeat: true, historyReads: 1, pending: 0, closed: false,
    identities: [identityOf(baseContext)],
    ...scenario.expected,
  });
  runtime.stop();
}

const outageReplay = [
  "turn_ended:turn-end", "awaiting_user_input:wait",
  "background_agent_started:background", "intent_reported:live-intent",
];

// History collapses into the newest report of each kind plus unresolved or recently finished
// background-agent pairs; the pair that finished outside the five-minute window is dropped.
const boundedHistory = [
  ["old-background-start", -420, "subagent.started", { toolCallId: "old" }],
  ["old-background-finish", -410, "subagent.completed", { toolCallId: "old" }],
  ["active-background-finish", -390, "subagent.completed", { toolCallId: "active" }],
  ["active-background-start", -380, "subagent.started", { toolCallId: "active" }],
  ["turn-old", -250, "assistant.turn_start"], ["turn-current", -240, "assistant.turn_end"],
  ["title-old", -210, "session.title_changed", { title: "Old" }],
  ["title-current", -200, "session.title_changed", { title: "Current" }],
  ["recent-background-start", -20, "subagent.started", { toolCallId: "recent" }],
  ["recent-background-finish", -10, "subagent.completed", { toolCallId: "recent" }],
].map((event) => ev(...event));
const boundedReplay = [
  "background_agent_finished:active-background-finish",
  "background_agent_started:active-background-start", "turn_ended:turn-current",
  "title_reported:title-current", "background_agent_started:recent-background-start",
  "background_agent_finished:recent-background-finish",
];
const boundedPass = [presenceSent, ...boundedReplay, "intent_reported:live-reconnect"];
const recoveredReplay = [
  presenceSent, "turn_started:working",
  "awaiting_user_input:waiting", "background_agent_started:background-live",
];
const resumedShape = (eventId, occurredAt, kind) =>
  ({ ...baseContext, sessionId: "selected-durable-session", eventId, occurredAt, kind });

const scenarios = [
  {
    name: "presence retries through a transport outage, then replays what it could not send",
    presence: ["throw", "ack"],
    history: [[
      ev("turn-end", -90, "assistant.turn_end"),
      ev("wait", -89, "elicitation.requested", { message: "Choose a recovery" }),
      ev("background", -88, "subagent.started", { toolCallId: "tool-background" }),
    ]],
    steps: [
      expect({ state: "connecting", retries: 1, heartbeat: false, pending: 1, historyReads: 0 }),
      ev("live-intent", -87, "assistant.intent", { intent: "Waiting on background verification" }),
      expect({ sent: presenceOnly }),
      "retry",
    ],
    expected: { sent: [presenceSent, presenceSent, ...outageReplay] },
  },
  ...[
    ["unmonitored", "an unmonitored endpoint acknowledgement"],
    ["deadParent", "a permanently rejected presence"],
  ].map(([presence, label]) => ({
    name: `${label} stops reporting without a retry or a replay`,
    presence,
    steps: [
      expect({ state: "terminal", heartbeat: false, pending: 0 }),
      ev("after-terminal", -600, "assistant.turn_start"),
    ],
    expected: { state: "terminal", heartbeat: false, sent: presenceOnly, historyReads: 0 },
  })),
  {
    name: "a reconnect reuses the one bounded historical replay snapshot",
    ordinary: { "live-reconnect": ["throw", "ack"] },
    history: [boundedHistory],
    steps: [
      expect({ sent: [presenceSent, ...boundedReplay], historyReads: 1 }),
      ev("live-reconnect", 1, "assistant.intent", { intent: "Reconnect now" }),
      expect({ state: "connecting", retries: 1, pending: 1 }),
      "retry",
    ],
    expected: { sent: [...boundedPass, ...boundedPass] },
  },
  {
    name: "a failed historical snapshot load is retried until one succeeds",
    ordinary: { "live-after-failure": ["throw", "ack"] },
    history: ["fail", [ev("historical-turn", -1800, "assistant.turn_start")]],
    steps: [
      ev("live-after-failure", -900, "assistant.turn_start"),
      expect({ state: "connecting", pending: 1 }),
      "retry",
    ],
    expected: {
      historyReads: 2,
      sent: [
        presenceSent, "turn_started:live-after-failure",
        presenceSent, "turn_started:historical-turn", "turn_started:live-after-failure",
      ],
    },
  },
  {
    name: "presence is re-established and replayed after heartbeat and ordinary transport loss",
    ordinary: { heartbeat: ["throw", "ack"], "live-turn": ["retryable", "ack"] },
    history: [[ev("working", -1800, "assistant.turn_start")]],
    steps: [
      "heartbeat",
      expect({ state: "connecting", retries: 1, heartbeat: false, pending: 1 }),
      ev("waiting", -1799, "user_input.requested", { question: "Continue?" }),
      ev("background-live", -1798, "subagent.started", { toolCallId: "tool-reconnect" }),
      "retry",
      expect({ state: "ready", retries: 0, heartbeat: true }),
      ev("live-turn", -1797, "assistant.turn_start"),
      expect({ state: "connecting", pending: 1 }),
      "retry",
    ],
    expected: {
      sent: [
        presenceSent, "turn_started:working", "heartbeat:generated-3",
        ...recoveredReplay, "turn_started:live-turn", ...recoveredReplay, "turn_started:live-turn",
      ],
    },
  },
  {
    name: "ordinary ignored responses stay ready while unmonitored responses terminate",
    ordinary: { "ignored-turn": "ignored", "unmonitored-turn": "unmonitored" },
    steps: [
      ev("ignored-turn", -600, "assistant.turn_start"),
      expect({ state: "ready", heartbeat: true }),
      ev("unmonitored-turn", -599, "assistant.turn_end"),
      expect({ state: "terminal", pending: 0 }),
      ev("after-terminal", -598, "assistant.turn_start"),
    ],
    expected: {
      state: "terminal", heartbeat: false,
      sent: [presenceSent, "turn_started:ignored-turn", "turn_ended:unmonitored-turn"],
    },
  },
  {
    name: "the metadata title bootstrap reports the session summary after presence",
    metadata: async () => ({ summary: "Bootstrapped title" }),
    expected: { sent: [presenceSent, "title_bootstrap:generated-2"] },
  },
  {
    name: "resumed startup replays the selected identity in canonical shape, minus historical shutdown",
    baseContext: { sessionId: "selected-durable-session" },
    history: [[
      ev("old-shutdown", -3660, "session.shutdown", { shutdownType: "routine" }),
      ev("resumed-idle", -3600, "session.idle"),
    ]],
    steps: ["heartbeat"],
    expected: {
      sent: [presenceSent, "went_idle:resumed-idle", "heartbeat:generated-3"],
      // generated-2 is spent on the blank metadata title bootstrap, which reports nothing.
      reports: [
        resumedShape("generated-1", runtimeNowIso, "session_present"),
        resumedShape("resumed-idle", at(-3600), "went_idle"),
        resumedShape("generated-3", runtimeNowIso, "heartbeat"),
      ],
      identities: [identityOf({ ...baseContext, sessionId: "selected-durable-session" })],
    },
  },
];

test("presence retries only transient transport and explicit retry acknowledgements", () => {
  assert.deepEqual(
    [
      acknowledged,
      answer({ recorded: false, monitored: true, retryable: true, reason: "store busy" }),
      answer({ recorded: false, monitored: false, retryable: true }),
      answer({ recorded: false, monitored: true }),
      { ok: false, status: 400, statusText: "Bad Request" },
      replies.unavailable,
    ].map(classifyPresenceResult),
    [
      { kind: "ok" }, { kind: "retry", reason: "store busy" },
      { kind: "terminal", reason: "worktree is not monitored by this endpoint" },
      { kind: "terminal", reason: "presence was not recorded" },
      { kind: "terminal", reason: "HTTP 400 Bad Request" },
      { kind: "retry", reason: "HTTP 503 Unavailable" },
    ],
  );
});

test("presence backoff grows exponentially and stays bounded", () => {
  assert.deepEqual([0, 1, 3, 7, 40].map(presenceRetryDelay), [1000, 2000, 8000, 120000, 120000]);
});

for (const scenario of scenarios) test(scenario.name, () => runScenario(scenario));

test("the heartbeat cadence starts on acknowledgement while replay is still in flight", async () => {
  const historicalRead = deferred();
  const fixture = createFixture({ history: () => historicalRead.promise });
  const startTask = fixture.runtime.start();

  await fixture.heartbeat.started;
  assertObservation(fixture, { state: "replaying", heartbeat: true, sent: presenceOnly });

  await fixture.heartbeat.run();
  assertObservation(fixture, { sent: [presenceSent, "heartbeat:generated-2"] });

  historicalRead.resolve([]);
  await startTask;
  await fixture.runtime.flush();
  assertObservation(fixture, { state: "ready", heartbeat: true });
  fixture.runtime.stop();
});

test("a heartbeat failure invalidates a slow reconnect replay instead of letting it finish", async () => {
  const slowPost = deferred();
  const slowPostStarted = deferred();
  let presenceAttempts = 0;

  const fixture = createFixture({
    history: async () => [ev("historical-working", -1800, "assistant.turn_start")],
    post: async (report) => {
      if (report.kind === "session_present") { presenceAttempts += 1; return acknowledged; }
      if (report.kind === "heartbeat") throw new Error("heartbeat failed");
      if (report.eventId === "trigger-reconnect" && presenceAttempts === 1) {
        throw new Error("connection reset");
      }
      if (report.eventId !== "historical-working" || presenceAttempts !== 2) return acknowledged;
      slowPostStarted.resolve();
      return slowPost.promise;
    },
  });
  const { runtime, scheduler, heartbeat } = fixture;
  await runtime.start();
  await runtime.flush();

  fixture.emit(ev("trigger-reconnect", -900, "assistant.intent", { intent: "Reconnect" }));
  await runtime.flush();
  assertObservation(fixture, { state: "connecting", pending: 1 });

  const reconnectTask = scheduler.run();
  await slowPostStarted.promise;
  assertObservation(fixture, { state: "replaying", heartbeat: true });

  await heartbeat.run();
  assertObservation(fixture, { state: "connecting", heartbeat: false, pending: 1 });

  slowPost.resolve(acknowledged);
  await reconnectTask;
  assertObservation(fixture, { state: "connecting", pending: 1 });

  await scheduler.run();
  await runtime.flush();
  assert.equal(presenceAttempts, 3);
  assertObservation(fixture, { state: "ready", heartbeat: true, historyReads: 1 });
  runtime.stop();
});

test("shutdown survives a transient replay failure and closes after reconnect", async () => {
  const replayPost = deferred();
  const replayStarted = deferred();
  let presenceAttempts = 0;

  const fixture = createFixture({
    history: async () => [ev("historical-working", -1800, "assistant.turn_start")],
    post: async (report) => {
      if (report.kind === "session_present") {
        presenceAttempts += 1;
        return acknowledged;
      }
      if (report.eventId === "historical-working") {
        replayStarted.resolve();
        return replayPost.promise;
      }
      return acknowledged;
    },
  });

  const startTask = fixture.runtime.start();
  await replayStarted.promise;
  fixture.emit(ev("shutdown-during-replay", 1, "session.shutdown"));
  replayPost.resolve(replies.unavailable);
  await startTask;

  assertObservation(fixture, {
    state: "connecting",
    closed: true,
    heartbeat: false,
    pending: 1,
  });

  await fixture.scheduler.run();
  await fixture.runtime.flush();

  assert.equal(presenceAttempts, 2);
  assertObservation(fixture, {
    sent: [
      presenceSent,
      "turn_started:historical-working",
      presenceSent,
      "session_closed:shutdown-during-replay",
    ],
    state: "terminal",
    heartbeat: false,
    closed: true,
    pending: 0,
  });
});

/** Starts a runtime whose first presence attempt is still in flight when a live shutdown arrives. */
async function shutdownDuringPresence() {
  const pendingPresence = deferred();
  const presenceInFlight = deferred();
  let presenceAttempts = 0;

  const fixture = createFixture({
    post: async (report) => {
      if (report.kind !== "session_present") return acknowledged;
      presenceAttempts += 1;
      presenceInFlight.resolve();
      return presenceAttempts === 1 ? pendingPresence.promise : acknowledged;
    },
  });
  const startTask = fixture.runtime.start();

  await presenceInFlight.promise;
  assertObservation(fixture, { state: "connecting", sent: presenceOnly, closed: false });
  fixture.emit(ev("shutdown-in-flight", 1, "session.shutdown"));
  assertObservation(fixture, { state: "connecting", closed: true, sent: presenceOnly });

  return { ...fixture, pendingPresence, startTask };
}

for (const { name, reply, retried, sent } of [
  {
    name: "shutdown retries an unresolved presence and then closes exactly once",
    reply: replies.unavailable, retried: true,
    sent: [presenceSent, presenceSent, "session_closed:shutdown-in-flight"],
  },
  {
    name: "a permanently rejected presence terminates a pending shutdown without a close report",
    reply: replies.deadParent, retried: false, sent: presenceOnly,
  },
]) {
  test(name, async () => {
    const fixture = await shutdownDuringPresence();

    fixture.pendingPresence.resolve(reply);
    await fixture.startTask;
    if (retried) {
      assertObservation(fixture, { state: "connecting", pending: 1, sent: presenceOnly });
      await fixture.scheduler.run();
    }

    await fixture.runtime.flush();
    assertObservation(fixture, {
      sent, state: "terminal", heartbeat: false, closed: true, pending: 0,
    });
  });
}

test("a live shutdown stops the heartbeat before delivering exactly one closure", async () => {
  const fixture = createFixture();
  await fixture.runtime.start();
  await fixture.runtime.flush();
  fixture.order.length = 0;

  fixture.emit(ev("live-shutdown", 60, "session.shutdown", { shutdownType: "routine" }));
  fixture.runtime.stop();
  await fixture.runtime.flush();

  assert.deepEqual(fixture.order, ["heartbeat-stop", "post:session_closed"]);
  const closedOnce = {
    sent: [presenceSent, "session_closed:live-shutdown"], state: "terminal",
    heartbeat: false, closed: true,
  };
  assertObservation(fixture, closedOnce);

  await fixture.heartbeat.run();
  await fixture.runtime.flush();
  assertObservation(fixture, closedOnce);
});

test("the metadata title bootstrap stays non-blocking and never overwrites a live title", async () => {
  const metadataRead = deferred();
  const fixture = createFixture({ metadata: () => metadataRead.promise });
  await fixture.runtime.start();

  fixture.emit(ev("live-title", 1, "session.title_changed", { title: "Live title" }));
  metadataRead.resolve({ summary: "Stale bootstrap title" });
  await fixture.runtime.flush();

  assertObservation(fixture, { state: "ready", sent: [presenceSent, "title_reported:live-title"] });
  fixture.runtime.stop();
});
