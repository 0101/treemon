import test from "node:test";
import assert from "node:assert/strict";
import {
  buildNonBlankMessageReport,
  buildReport,
  MAX_TOOL_CALL_ID_CHARS,
  mapSdkEvent,
} from "./reporting-core.mjs";

const context = {
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

test("metadata summary maps to title_bootstrap without a live title event", () => {
  assert.deepEqual(buildNonBlankMessageReport(context, "title_bootstrap", "Investigate Intent Title Runtime"), {
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
      sessionId: "session-1",
      worktreePath: "worktree",
      provider: "copilot_cli",
      eventId: "subagent-completed",
      occurredAt: "2026-07-20T12:33:00.000Z",
      kind: "background_agent_finished",
      toolCallId: "tool-1",
    },
    {
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

test("blank metadata summary emits no title report", () => {
  assert.equal(buildNonBlankMessageReport(context, "title_bootstrap", "   "), null);
  assert.equal(buildNonBlankMessageReport(context, "title_bootstrap", undefined), null);
});

test("live and bootstrap messages share the canonical report shape", () => {
  assert.deepEqual(buildNonBlankMessageReport(context, "title_reported", "Live title"), {
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
