import assert from "node:assert/strict";
import test from "node:test";

import {
  subagentId,
  treemonEvents,
  toReport,
} from "../../../ClaudeHooks/claude-hooks-core.mjs";

const kinds = (hook) => treemonEvents(hook).map((event) => event.kind);

const context = {
  sessionId: "session-1",
  worktreePath: "/repo",
  provider: "claude_code",
  eventId: "event-1",
  occurredAt: "2026-03-01T10:00:00.000Z",
};

test("a session registers as idle so it is visible before its first prompt", () => {
  assert.deepEqual(kinds({ hook_event_name: "SessionStart", source: "startup" }), ["went_idle"]);
});

test("compaction does not report idle, because it happens mid-turn", () => {
  assert.deepEqual(kinds({ hook_event_name: "SessionStart", source: "compact" }), ["heartbeat"]);
});

test("submitting a prompt clears a pending question before starting the turn", () => {
  assert.deepEqual(kinds({ hook_event_name: "UserPromptSubmit", prompt: "go" }), [
    "user_input_completed",
    "user_prompt",
    "turn_started",
  ]);
});

test("ending a turn or a session clears a pending question", () => {
  // Without this the durable ask_user gate holds the card at WaitingForUser indefinitely.
  assert.deepEqual(kinds({ hook_event_name: "Stop" }), ["user_input_completed", "turn_ended"]);
  assert.deepEqual(kinds({ hook_event_name: "SessionEnd" }), ["user_input_completed", "went_idle"]);
});

test("a notification carries the question it is blocked on", () => {
  const [event] = treemonEvents({ hook_event_name: "Notification", message: "Approve?" });
  assert.equal(event.kind, "awaiting_user_input");
  assert.equal(event.text, "Approve?");
});

test("a delegated agent is opened and closed with the same id", () => {
  const started = treemonEvents({
    hook_event_name: "PreToolUse",
    tool_name: "Task",
    tool_use_id: "toolu_1",
  });
  const finished = treemonEvents({
    hook_event_name: "PostToolUse",
    tool_name: "Task",
    tool_use_id: "toolu_1",
  });

  assert.deepEqual(started, [{ kind: "background_agent_started", toolCallId: "toolu_1" }]);
  assert.deepEqual(finished, [{ kind: "background_agent_finished", toolCallId: "toolu_1" }]);
});

test("a tool that fails or is denied still closes its delegated agent", () => {
  // Neither hook reaches PostToolUse, and an unclosed clock pins the session to Working.
  for (const hook_event_name of ["PostToolUseFailure", "PermissionDenied"]) {
    assert.deepEqual(treemonEvents({ hook_event_name, tool_name: "Task", tool_use_id: "toolu_2" }), [
      { kind: "background_agent_finished", toolCallId: "toolu_2" },
    ]);
  }
});

test("SubagentStop is identified by agent_id, which is what Claude sends there", () => {
  assert.equal(subagentId({ hook_event_name: "SubagentStop", agent_id: "agent-9" }), "agent-9");
  assert.deepEqual(treemonEvents({ hook_event_name: "SubagentStop", agent_id: "agent-9" }), [
    { kind: "background_agent_finished", toolCallId: "agent-9" },
  ]);
});

test("an ordinary tool call only says the session is alive", () => {
  assert.deepEqual(kinds({ hook_event_name: "PreToolUse", tool_name: "Read" }), ["heartbeat"]);
  assert.deepEqual(kinds({ hook_event_name: "PostToolUse", tool_name: "Read" }), ["heartbeat"]);
});

test("an unknown or malformed hook still reports liveness rather than nothing", () => {
  assert.deepEqual(kinds({ hook_event_name: "SomethingNew" }), ["heartbeat"]);
  assert.deepEqual(kinds({}), ["heartbeat"]);
});

test("a delegated-agent event with no id is dropped rather than sent unpaired", () => {
  assert.equal(toReport(context, { kind: "background_agent_started", toolCallId: null }), null);
  assert.equal(toReport(context, { kind: "background_agent_finished" }), null);
});

test("user_prompt is dropped without text, because the server requires the message", () => {
  assert.equal(toReport(context, { kind: "user_prompt", text: "   " }), null);
  assert.equal(toReport(context, { kind: "user_prompt" }), null);
});

test("a question with no text still reports that the agent is blocked", () => {
  const report = toReport(context, { kind: "awaiting_user_input", text: "" });
  assert.equal(report.kind, "awaiting_user_input");
  assert.equal(report.provider, "claude_code");
});

test("a report carries the identity Treemon attaches status to", () => {
  const report = toReport(context, { kind: "turn_started" });
  assert.equal(report.sessionId, "session-1");
  assert.equal(report.worktreePath, "/repo");
  assert.equal(report.occurredAt, "2026-03-01T10:00:00.000Z");
});
