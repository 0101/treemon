import assert from "node:assert/strict";
import test from "node:test";

import {
  CLAIM_STALE_MS,
  claimIsLive,
  claimPath,
  subagentId,
  toReport,
  transcriptFacts,
  treemonEvents,
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
  assert.deepEqual(kinds({ hook_event_name: "SessionStart", source: "startup" }), [
    "went_idle",
    "title_bootstrap",
  ]);
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
  assert.deepEqual(kinds({ hook_event_name: "Stop" }), [
    "user_input_completed",
    "assistant_message",
    "title_reported",
    "turn_ended",
  ]);
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

test("a skill is reported by name, which is what the card renders", () => {
  const [event] = treemonEvents({
    hook_event_name: "PreToolUse",
    tool_name: "Skill",
    tool_input: { skill: "code-review" },
  });

  assert.deepEqual(event, { kind: "skill_invoked", skillName: "code-review" });
  assert.equal(toReport(context, event).skillName, "code-review");
});

test("a skill with no name is dropped, because the server requires one", () => {
  assert.equal(toReport(context, { kind: "skill_invoked", skillName: "  " }), null);
  assert.equal(toReport(context, { kind: "skill_invoked" }), null);
});

test("a tool that blocks on the human reports the wait, not just liveness", () => {
  // Neither of these raises a Notification, so without this the card shows Working while the agent
  // is in fact waiting.
  const [asked] = treemonEvents({
    hook_event_name: "PreToolUse",
    tool_name: "AskUserQuestion",
    tool_input: { questions: [{ question: "Which database?" }] },
  });

  assert.deepEqual(asked, { kind: "awaiting_user_input", text: "Which database?" });

  // A plan is a document rather than a question, so the wait is reported without one.
  assert.deepEqual(treemonEvents({ hook_event_name: "PreToolUse", tool_name: "ExitPlanMode" }), [
    { kind: "awaiting_user_input", text: undefined },
  ]);
});

test("answering, refusing or failing a question all end the wait", () => {
  for (const hook_event_name of ["PostToolUse", "PostToolUseFailure", "PermissionDenied"]) {
    assert.deepEqual(kinds({ hook_event_name, tool_name: "AskUserQuestion" }), [
      "user_input_completed",
    ]);
  }
});

test("the title and the last reply come from the transcript, which no hook payload carries", () => {
  const facts = transcriptFacts(
    [
      '{"type":"assistant","message":{"content":[{"type":"text","text":"older"}]}}',
      '{"type":"ai-title","aiTitle":"Superseded title"}',
      '{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"a subagent"}]}}',
      '{"type":"assistant","message":{"content":[{"type":"text","text":"Pushed as e0c12ba6f."}]}}',
      '{"type":"ai-title","aiTitle":"Auth0 organization management"}',
    ].join("\n"),
  );

  assert.deepEqual(facts, {
    title: "Auth0 organization management",
    assistantText: "Pushed as e0c12ba6f.",
  });
});

test("a transcript tail that starts mid-line still yields its facts", () => {
  // The caller reads the last bytes of a multi-megabyte file, so the first line is usually a fragment.
  const facts = transcriptFacts(
    ['ent":[{"type":"text","text":"truncated"}]}}', '{"type":"ai-title","aiTitle":"Open tickets"}'].join("\n"),
  );

  assert.equal(facts.title, "Open tickets");
  assert.equal(facts.assistantText, undefined);
});

test("a transcript with nothing to say yields nothing rather than empty text", () => {
  assert.deepEqual(transcriptFacts(""), { title: undefined, assistantText: undefined });
  assert.deepEqual(transcriptFacts('{"type":"ai-title","aiTitle":"   "}'), {
    title: undefined,
    assistantText: undefined,
  });
});

test("a title with no text is dropped, because the server rejects the bare event", () => {
  assert.equal(toReport(context, { kind: "title_reported" }), null);
  assert.equal(toReport(context, { kind: "title_bootstrap", text: "" }), null);
  assert.equal(toReport(context, { kind: "assistant_message" }), null);
});

test("a claim is live only while an owner is still beating for it", () => {
  const now = Date.parse("2026-03-01T10:00:00.000Z");
  const beating = { owner: "o1", beatAt: "2026-03-01T09:59:00.000Z" };

  assert.equal(claimIsLive(beating, now), true);
  // Turn end clears the owner rather than deleting the file, so the beater cannot recreate it.
  assert.equal(claimIsLive({ ...beating, owner: null }, now), false);
  assert.equal(claimIsLive({ ...beating, beatAt: new Date(now - CLAIM_STALE_MS - 1).toISOString() }, now), false);
  assert.equal(claimIsLive({ owner: "o1" }, now), false);
  assert.equal(claimIsLive(null, now), false);
});

test("a claim path is stable per session and never pastes the session id into a filename", () => {
  const sessionId = "27743c85-c276-44d2-a4ee-dc32dbf5fa57";

  assert.equal(claimPath(sessionId), claimPath(sessionId));
  assert.notEqual(claimPath(sessionId), claimPath("other"));
  assert.ok(!claimPath("../../escape").includes(".."));
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
