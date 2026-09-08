// The pure half of the Claude Code reporter: hook payload in, Treemon events out. Kept separate from
// report.mjs so it can be imported and tested - report.mjs reads stdin and posts as soon as it is
// loaded, and every failure in it is deliberately swallowed, so a mapping regression would otherwise
// exit successfully and be invisible.

import { buildReport, buildNonBlankMessageReport } from "../Extension/reporting/reporting-core.mjs";

/**
 * Treemon pairs a delegated agent's start with its finish by id. PreToolUse and PostToolUse both
 * carry `tool_use_id`; SubagentStop identifies the agent instead, so its id is taken from there
 * rather than fabricated - a fabricated one can never match the start it belongs to, which leaves a
 * card showing work that finished.
 *
 * @param {Record<string, any>} hook
 */
export function subagentId(hook) {
  const id = hook.tool_use_id ?? hook.agent_id;
  return id ? String(id) : null;
}

/**
 * Claude's hook events, mapped onto Treemon's vocabulary. A hook can produce more than one event:
 * submitting a prompt clears any pending question, carries the prompt text, and starts a turn.
 *
 * @param {Record<string, any>} hook
 * @returns {{kind: string, text?: string, toolCallId?: string|null}[]}
 */
export function treemonEvents(hook) {
  switch (hook.hook_event_name) {
    // Registers the session as present but not working. A bare heartbeat does not open a session, so
    // a started-and-waiting Claude would otherwise stay invisible until its first prompt. Compaction
    // reuses this hook mid-turn, though, and reporting idle there would blank an working agent.
    case "SessionStart":
      return hook.source === "compact" ? [{ kind: "heartbeat" }] : [{ kind: "went_idle" }];
    // An ask_user wait is a durable gate on the server: it holds WaitingForUser until told the input
    // completed, so everything that means "the human has answered" clears it first.
    case "UserPromptSubmit":
      return [
        { kind: "user_input_completed" },
        { kind: "user_prompt", text: hook.prompt },
        { kind: "turn_started" },
      ];
    case "Stop":
      return [{ kind: "user_input_completed" }, { kind: "turn_ended" }];
    case "SessionEnd":
      return [{ kind: "user_input_completed" }, { kind: "went_idle" }];
    // Permission prompts and idle notifications both arrive here, and both mean the same thing to a
    // dashboard: this agent is blocked on a human.
    case "Notification":
      return [{ kind: "awaiting_user_input", text: hook.message }];
    case "PreToolUse":
      return hook.tool_name === "Task"
        ? [{ kind: "background_agent_started", toolCallId: subagentId(hook) }]
        : [{ kind: "heartbeat" }];
    // A tool that fails or is denied never reaches PostToolUse, so those hooks close the delegated
    // agent too. Without them a failed sub-agent leaves its clock open and pins the card to Working.
    case "PostToolUse":
    case "PostToolUseFailure":
    case "PermissionDenied":
      return hook.tool_name === "Task"
        ? [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }]
        : [{ kind: "heartbeat" }];
    case "SubagentStop":
      return [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }];
    default:
      // An unrecognised hook still says the session is alive, which beats letting a stretch of
      // unmapped events decay it to Idle.
      return [{ kind: "heartbeat" }];
  }
}

/**
 * The message builder yields nothing for blank text, and most kinds are still meaningful without
 * one. `user_prompt` is the exception, because the server requires its message and would reject the
 * bare event. A delegated-agent event without an id is dropped rather than sent unpaired.
 *
 * @param {Record<string, any>} context
 * @param {{kind: string, text?: string, toolCallId?: string|null}} event
 */
export function toReport(context, event) {
  const needsToolCallId =
    event.kind === "background_agent_started" || event.kind === "background_agent_finished";

  if (needsToolCallId && !event.toolCallId) return null;

  const report =
    (event.text ? buildNonBlankMessageReport(context, event.kind, event.text) : null) ??
    (event.kind === "user_prompt" ? null : buildReport(context, event.kind));

  if (report && event.toolCallId) report.toolCallId = event.toolCallId;

  return report;
}
