import test from "node:test";
import assert from "node:assert/strict";
import {
  BACKGROUND_AGENT_CLOCK_RETENTION_MS,
  buildNonBlankMessageReport,
  buildReport,
  compareReportsByOccurrence,
  createCurrentProcessState,
  MAX_TOOL_CALL_ID_CHARS,
  mapSdkEvent,
  mergeReplayReports,
  reportForReplaySdkEvent,
  reportForSdkEvent,
} from "../../../Extension/reporting/reporting-core.mjs";

const context = {
  parentProcessId: 4321,
  sessionId: "session-1",
  worktreePath: "worktree",
  provider: "copilot_cli",
  eventId: "event-1",
  occurredAt: "2026-07-20T12:31:02.493Z",
};

function map(event) {
  return mapSdkEvent(
    {
      ...context,
      eventId: event.id,
      occurredAt: event.timestamp,
    },
    event,
  );
}

test("terminal origin is carried on every mapped report and omitted when absent", () => {
  const event = {
    id: "terminal-origin",
    timestamp: context.occurredAt,
    type: "assistant.turn_start",
    data: {},
  };
  const terminalSessionId = "0123456789abcdef0123456789abcdef";

  assert.deepEqual(
    reportForSdkEvent({ ...context, terminalSessionId }, event),
    {
      parentProcessId: 4321,
      sessionId: "session-1",
      terminalSessionId,
      worktreePath: "worktree",
      provider: "copilot_cli",
      eventId: "terminal-origin",
      occurredAt: "2026-07-20T12:31:02.493Z",
      kind: "turn_started",
    },
  );
  assert.equal(
    Object.hasOwn(reportForSdkEvent(context, event), "terminalSessionId"),
    false,
  );
});

test("metadata summary maps to title_bootstrap without a live title event", () => {
  assert.deepEqual(buildNonBlankMessageReport(context, "title_bootstrap", "Investigate Intent Title Runtime"), {
    parentProcessId: 4321,
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "event-1",
    occurredAt: "2026-07-20T12:31:02.493Z",
    kind: "title_bootstrap",
    message: {
      text: "Investigate Intent Title Runtime",
      at: "2026-07-20T12:31:02.493Z",
    },
  });
});

test("subagent.started maps before agentId filtering", () => {
  assert.deepEqual(map({
    id: "subagent-start",
    timestamp: "2026-07-20T12:32:00.000Z",
    type: "subagent.started",
    agentId: "agent-1",
    data: { toolCallId: "tool-1" },
  }), {
    parentProcessId: 4321,
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "subagent-start",
    occurredAt: "2026-07-20T12:32:00.000Z",
    kind: "background_agent_started",
    toolCallId: "tool-1",
  });
});

test("subagent.completed and subagent.failed map to terminal lifecycle reports", () => {
  assert.deepEqual([
    map({
      id: "subagent-completed",
      timestamp: "2026-07-20T12:33:00.000Z",
      type: "subagent.completed",
      agentId: "agent-1",
      data: { toolCallId: "tool-1" },
    }),
    map({
      id: "subagent-failed",
      timestamp: "2026-07-20T12:34:00.000Z",
      type: "subagent.failed",
      agentId: "agent-2",
      data: { toolCallId: "tool-2" },
    }),
  ], [
    {
      parentProcessId: 4321,
      sessionId: "session-1",
      worktreePath: "worktree",
      provider: "copilot_cli",
      eventId: "subagent-completed",
      occurredAt: "2026-07-20T12:33:00.000Z",
      kind: "background_agent_finished",
      toolCallId: "tool-1",
    },
    {
      parentProcessId: 4321,
      sessionId: "session-1",
      worktreePath: "worktree",
      provider: "copilot_cli",
      eventId: "subagent-failed",
      occurredAt: "2026-07-20T12:34:00.000Z",
      kind: "background_agent_finished",
      toolCallId: "tool-2",
    },
  ]);
});

test("background lifecycle requires a nonblank data.toolCallId", () => {
  assert.deepEqual(
    ["subagent.started", "subagent.completed", "subagent.failed"]
      .flatMap((type) => [
        map({
          id: `${type}-missing`,
          timestamp: context.occurredAt,
          type,
          agentId: "agent-1",
          data: {},
        }),
        map({
          id: `${type}-blank`,
          timestamp: context.occurredAt,
          type,
          agentId: "agent-1",
          data: { toolCallId: "   " },
        }),
      ]),
    [null, null, null, null, null, null],
  );
});

test("background lifecycle preserves a maximum-length toolCallId", () => {
  const toolCallId = ` ${"x".repeat(MAX_TOOL_CALL_ID_CHARS - 2)} `;

  assert.deepEqual(
    ["subagent.started", "subagent.completed", "subagent.failed"]
      .map((type) => map({
        id: `${type}-max-id`,
        timestamp: context.occurredAt,
        type,
        agentId: "agent-1",
        data: { toolCallId },
      })?.toolCallId),
    [toolCallId, toolCallId, toolCallId],
  );
});

test("background lifecycle drops an overlong toolCallId instead of truncating it", () => {
  const toolCallId = "x".repeat(MAX_TOOL_CALL_ID_CHARS + 1);

  assert.deepEqual(
    ["subagent.started", "subagent.completed", "subagent.failed"]
      .map((type) => map({
        id: `${type}-overlong-id`,
        timestamp: context.occurredAt,
        type,
        agentId: "agent-1",
        data: { toolCallId },
      })),
    [null, null, null],
  );
});

test("agentId continues to filter sub-agent content and turn events", () => {
  assert.deepEqual([
    map({
      id: "turn-start",
      timestamp: context.occurredAt,
      type: "assistant.turn_start",
      agentId: "agent-1",
      data: {},
    }),
    map({
      id: "turn-end",
      timestamp: context.occurredAt,
      type: "assistant.turn_end",
      agentId: "agent-1",
      data: {},
    }),
    map({
      id: "assistant",
      timestamp: context.occurredAt,
      type: "assistant.message",
      agentId: "agent-1",
      data: { content: "sub-agent response" },
    }),
    map({
      id: "user",
      timestamp: context.occurredAt,
      type: "user.message",
      agentId: "agent-1",
      data: { content: "sub-agent prompt" },
    }),
    map({
      id: "skill",
      timestamp: context.occurredAt,
      type: "skill.invoked",
      agentId: "agent-1",
      data: { name: "research" },
    }),
    map({
      id: "intent",
      timestamp: context.occurredAt,
      type: "assistant.intent",
      agentId: "agent-1",
      data: { intent: "Investigating" },
    }),
    map({
      id: "title",
      timestamp: context.occurredAt,
      type: "session.title_changed",
      agentId: "agent-1",
      data: { title: "Sub-agent title" },
    }),
    map({
      id: "idle",
      timestamp: context.occurredAt,
      type: "session.idle",
      agentId: "agent-1",
      data: {},
    }),
  ], [null, null, null, null, null, null, null, null]);
});

test("live and replay mapping preserve the same source identity", () => {
  const liveEvent = {
    id: "subagent-live-or-replay",
    timestamp: "2026-07-20T12:35:00.000Z",
    type: "subagent.started",
    agentId: "agent-1",
    data: { toolCallId: "tool-live-or-replay" },
  };
  const replayedEvent = JSON.parse(JSON.stringify(liveEvent));

  assert.deepEqual(map(liveEvent), map(replayedEvent));
});

test("session shutdown is reported live but never replayed into a resumed process", () => {
  const shutdown = {
    id: "old-process-shutdown",
    timestamp: "2026-09-04T16:00:00.000Z",
    type: "session.shutdown",
    data: { shutdownType: "routine" },
  };

  assert.deepEqual(reportForSdkEvent(context, shutdown), {
    parentProcessId: 4321,
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "old-process-shutdown",
    occurredAt: "2026-09-04T16:00:00.000Z",
    kind: "session_closed",
  });
  assert.equal(reportForReplaySdkEvent(context, shutdown), null);
});

test("current-process replay preserves waiting and background truth across reconnect", () => {
  const state = createCurrentProcessState();
  const reports = [
    buildReport({
      ...context,
      eventId: "turn-ended",
      occurredAt: "2026-09-04T16:00:00.000Z",
    }, "turn_ended"),
    buildReport({
      ...context,
      eventId: "awaiting-user",
      occurredAt: "2026-09-04T16:00:01.000Z",
    }, "awaiting_user_input"),
    {
      ...buildReport({
        ...context,
        eventId: "background-start",
        occurredAt: "2026-09-04T16:00:02.000Z",
      }, "background_agent_started"),
      toolCallId: "tool-current",
    },
  ];
  reports.forEach(state.observe);

  assert.deepEqual(
    mergeReplayReports([reports[0]], state.snapshot()).map((report) => report.eventId),
    ["turn-ended", "awaiting-user", "background-start"],
  );
});

test("report occurrence ordering handles invalid dates and timestamp ties", () => {
  const report = (eventId, occurredAt) => buildReport({
    ...context,
    eventId,
    occurredAt,
  }, "turn_started");
  const invalidA = report("invalid-a", "not-a-date");
  const invalidB = report("invalid-b", "also-not-a-date");
  const sameTimeA = report("same-a", "2026-09-04T16:00:00.000Z");
  const sameTimeB = report("same-b", "2026-09-04T16:00:00.000Z");

  assert.equal(compareReportsByOccurrence(invalidA, sameTimeA), -1);
  assert.equal(compareReportsByOccurrence(invalidA, invalidB), -1);
  assert.equal(compareReportsByOccurrence(sameTimeB, sameTimeA), 1);
  assert.equal(compareReportsByOccurrence(sameTimeA, { ...sameTimeA }), 0);
});

test("current-process state keeps the next report on an exact occurrence tie", () => {
  const state = createCurrentProcessState();
  const first = {
    ...buildReport(context, "intent_reported"),
    message: { text: "First", at: context.occurredAt },
  };
  const next = {
    ...first,
    message: { text: "Next", at: context.occurredAt },
  };

  state.observe(first);
  state.observe(next);

  assert.equal(state.snapshot()[0].message.text, "Next");
});

test("current-process state prunes only old resolved background-agent pairs", () => {
  const now = Date.parse("2026-09-04T16:10:00.000Z");
  const state = createCurrentProcessState(() => now);
  const at = (offsetMs) => new Date(now + offsetMs).toISOString();
  const backgroundReport = (eventId, occurredAt, kind, toolCallId) => ({
    ...buildReport({ ...context, eventId, occurredAt }, kind),
    toolCallId,
  });

  Array.from({ length: 40 }, (_, index) => {
    const toolCallId = `old-complete-${index}`;
    return [
      backgroundReport(
        `${toolCallId}-start`,
        at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 2000 - index),
        "background_agent_started",
        toolCallId,
      ),
      backgroundReport(
        `${toolCallId}-finish`,
        at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 1000 - index),
        "background_agent_finished",
        toolCallId,
      ),
    ];
  }).flat().forEach(state.observe);

  [
    backgroundReport(
      "active-finish",
      at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 4000),
      "background_agent_finished",
      "active",
    ),
    backgroundReport(
      "active-start",
      at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 3000),
      "background_agent_started",
      "active",
    ),
    backgroundReport(
      "finish-only",
      at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 5000),
      "background_agent_finished",
      "finish-only",
    ),
    backgroundReport(
      "start-only",
      at(-BACKGROUND_AGENT_CLOCK_RETENTION_MS - 6000),
      "background_agent_started",
      "start-only",
    ),
    backgroundReport(
      "recent-start",
      at(-60000),
      "background_agent_started",
      "recent",
    ),
    backgroundReport(
      "recent-finish",
      at(-59000),
      "background_agent_finished",
      "recent",
    ),
  ].forEach(state.observe);

  assert.deepEqual(
    state.snapshot().map((report) => report.eventId),
    [
      "start-only",
      "finish-only",
      "active-finish",
      "active-start",
      "recent-start",
      "recent-finish",
    ],
  );
});

test("current-process snapshot prunes resolved pairs after the retention window passes", () => {
  let now = Date.parse("2026-09-04T16:00:00.000Z");
  const state = createCurrentProcessState(() => now);
  const started = {
    ...buildReport({
      ...context,
      eventId: "aging-start",
      occurredAt: "2026-09-04T15:59:00.000Z",
    }, "background_agent_started"),
    toolCallId: "aging",
  };
  const finished = {
    ...buildReport({
      ...context,
      eventId: "aging-finish",
      occurredAt: "2026-09-04T15:59:01.000Z",
    }, "background_agent_finished"),
    toolCallId: "aging",
  };

  state.observe(started);
  state.observe(finished);
  assert.deepEqual(state.snapshot().map((report) => report.eventId), [
    "aging-start",
    "aging-finish",
  ]);

  now += BACKGROUND_AGENT_CLOCK_RETENTION_MS + 61000;
  assert.deepEqual(state.snapshot(), []);
});

test("blank metadata summary emits no title report", () => {
  assert.equal(buildNonBlankMessageReport(context, "title_bootstrap", "   "), null);
  assert.equal(buildNonBlankMessageReport(context, "title_bootstrap", undefined), null);
});

test("message blankness is checked before the stored text is capped", () => {
  const whitespacePrefix = " ".repeat(2000);
  const report =
    buildNonBlankMessageReport(context, "title_bootstrap", `${whitespacePrefix}Title`);

  assert.ok(report);
  assert.equal(report.message.text, whitespacePrefix);
});

test("live and bootstrap messages share the canonical report shape", () => {
  assert.deepEqual(buildNonBlankMessageReport(context, "title_reported", "Live title"), {
    parentProcessId: 4321,
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "event-1",
    occurredAt: "2026-07-20T12:31:02.493Z",
    kind: "title_reported",
    message: {
      text: "Live title",
      at: "2026-07-20T12:31:02.493Z",
    },
  });
});

test("malformed events and non-string fields are dropped without coercion", () => {
  assert.equal(mapSdkEvent(context, null), null);
  assert.equal(mapSdkEvent(context, {}), null);
  assert.equal(mapSdkEvent(context, {
    type: "assistant.message",
    data: { content: { text: "not an SDK string" } },
  }), null);
  assert.equal(mapSdkEvent(context, {
    type: "skill.invoked",
    data: { name: 42 },
  }), null);
  assert.equal(mapSdkEvent(context, {
    type: "subagent.started",
    data: { toolCallId: 42 },
  }), null);
  assert.equal(buildNonBlankMessageReport(context, "title_bootstrap", 42), null);
});

test("usage mapping accepts finite numeric strings but rejects blank and structured values", () => {
  assert.deepEqual(mapSdkEvent(context, {
    type: "session.usage_info",
    data: { currentTokens: "12.6", tokenLimit: "100" },
  }), {
    ...buildReport(context, "usage_info"),
    currentTokens: 13,
    tokenLimit: 100,
  });
  assert.equal(mapSdkEvent(context, {
    type: "session.usage_info",
    data: { currentTokens: "", tokenLimit: 100 },
  }), null);
  assert.equal(mapSdkEvent(context, {
    type: "session.usage_info",
    data: { currentTokens: { value: 12 }, tokenLimit: 100 },
  }), null);
});

test("the production event boundary drops malformed identities before mapping", () => {
  const baseContext = {
    parentProcessId: context.parentProcessId,
    sessionId: context.sessionId,
    worktreePath: context.worktreePath,
    provider: context.provider,
  };

  assert.equal(reportForSdkEvent(baseContext, null), null);
  assert.equal(reportForSdkEvent(baseContext, {
    type: "assistant.turn_start",
    timestamp: context.occurredAt,
  }), null);
  assert.equal(reportForSdkEvent(baseContext, {
    id: "event",
    type: "assistant.turn_start",
  }), null);
  assert.deepEqual(reportForSdkEvent(baseContext, {
    id: "event",
    timestamp: context.occurredAt,
    type: "assistant.turn_start",
    data: {},
  }), {
    parentProcessId: context.parentProcessId,
    sessionId: context.sessionId,
    worktreePath: context.worktreePath,
    provider: context.provider,
    eventId: "event",
    occurredAt: context.occurredAt,
    kind: "turn_started",
  });
});

test("a rejected live title produces no title report", () => {
  const baseContext = {
    parentProcessId: context.parentProcessId,
    sessionId: context.sessionId,
    worktreePath: context.worktreePath,
    provider: context.provider,
  };

  assert.equal(reportForSdkEvent(baseContext, {
    id: "invalid-title",
    timestamp: context.occurredAt,
    type: "session.title_changed",
    data: { title: 42 },
  }), null);
});
