#!/usr/bin/env node
// Reports Claude Code session activity to Treemon.
//
// Claude Code has no extension host to observe a session from the inside, the way the Copilot CLI
// reporter does, so status comes from hooks instead: Claude invokes this script with the hook payload
// on stdin, and it translates that into the same POST /api/session/activity events the Copilot
// reporter sends. The payload shape is deliberately not re-implemented here - it comes from the
// reporting core the other provider already uses.
//
// Install: see README.md in this directory.
//
// Two rules this script must never break, because Claude waits for it:
//   - it always exits 0, so a Treemon that is down or slow can never fail a hook;
//   - every post is bounded, so an unresponsive server cannot stall the session.

import { buildReport, buildNonBlankMessageReport } from "../Extension/reporting/reporting-core.mjs";

// Claude runs this hook synchronously and waits for it, so this is latency added to the user's
// session. The budget is shared across every post one invocation makes rather than applied per
// request: a per-request timeout multiplies by the number of events, and a server that accepts a
// connection and then never answers would hold up a prompt for the sum of them.
const HOOK_BUDGET_MS = 1500;

// Same convention as the Copilot reporter: TREEMON_PORTS fans out to several instances, TREEMON_PORT
// names one, and 5000 is production.
const ports = (process.env.TREEMON_PORTS || process.env.TREEMON_PORT || "5000")
  .split(",")
  .map((p) => p.trim())
  .filter(Boolean);

const activityUrls = ports.map((p) => `http://127.0.0.1:${p}/api/session/activity`);

const readStdin = async () => {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
};

/**
 * Claude's hook events, mapped onto Treemon's vocabulary. A hook can produce more than one event:
 * submitting a prompt both starts a turn and carries the prompt text.
 *
 * @param {Record<string, any>} hook
 * @returns {{kind: string, text?: string, toolCallId?: string}[]}
 */
function treemonEvents(hook) {
  const name = hook.hook_event_name;

  switch (name) {
    // Registers the session as present but not working. A bare heartbeat does not open a session,
    // so a started-and-waiting Claude would otherwise stay invisible until its first prompt.
    case "SessionStart":
      return [{ kind: "went_idle" }];
    // An ask_user wait is a durable gate on the server: it holds WaitingForUser until told the input
    // completed, so every event that means "the human has answered" has to clear it first, or the
    // dashboard shows an agent blocked on a question that was answered long ago.
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
    // Claude notifies when it needs the user - permission requests and idle prompts both arrive
    // here, and both mean the same thing to a dashboard: this agent is blocked on a human.
    case "Notification":
      return [{ kind: "awaiting_user_input", text: hook.message }];
    // A Task tool call is a delegated agent. Reporting its start and finish is what keeps a root
    // turn from settling Idle while its sub-agents are still working.
    case "PreToolUse":
      return hook.tool_name === "Task"
        ? [{ kind: "background_agent_started", toolCallId: subagentId(hook) }]
        : [{ kind: "heartbeat" }];
    case "PostToolUse":
      return hook.tool_name === "Task"
        ? [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }]
        : [{ kind: "heartbeat" }];
    case "SubagentStop":
      return [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }];
    default:
      // An unrecognised hook still says the session is alive, which is better than letting a long
      // stretch of unmapped events decay it to Idle.
      return [{ kind: "heartbeat" }];
  }
}

/**
 * Treemon pairs a delegated agent's start with its finish by id. Claude does not always give the
 * same identifier to both sides, so fall back to the tool name: a mismatched pair would leave the
 * dashboard showing an agent that never finished.
 *
 * @param {Record<string, any>} hook
 */
function subagentId(hook) {
  return String(hook.tool_use_id || hook.toolUseId || `${hook.session_id}:task`);
}

/**
 * The message builder yields nothing for blank text, and most kinds are still meaningful without
 * one: a notification with no text still means the agent is blocked. `user_prompt` is the exception,
 * because the server requires its message and would reject the bare event.
 *
 * @param {Record<string, any>} context
 * @param {{kind: string, text?: string}} event
 */
function toReport(context, event) {
  if (event.text) {
    const withMessage = buildNonBlankMessageReport(context, event.kind, event.text);
    if (withMessage) return withMessage;
  }

  return event.kind === "user_prompt" ? null : buildReport(context, event.kind);
}

const post = async (body, signal) => {
  await Promise.all(
    activityUrls.map((url) =>
      fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal,
      }).catch(() => {}),
    ),
  );
};

try {
  const raw = await readStdin();
  const hook = raw ? JSON.parse(raw) : {};

  const sessionId = hook.session_id;
  const worktreePath = hook.cwd;

  // Without both, Treemon has nothing to attach the status to.
  if (sessionId && worktreePath) {
    const occurredAt = new Date().toISOString();
    // One deadline for the whole invocation. Events are posted in order because the server's state
    // machine reads them that way - clearing an ask_user wait before the turn it belongs to, for
    // instance - so they cannot simply be fired in parallel to save time.
    const budget = AbortSignal.timeout(HOOK_BUDGET_MS);

    for (const event of treemonEvents(hook)) {
      const context = {
        sessionId,
        worktreePath,
        provider: "claude_code",
        // Treemon de-duplicates by event id, so each event needs its own.
        eventId: `${sessionId}:${occurredAt}:${event.kind}:${Math.random().toString(36).slice(2)}`,
        occurredAt,
        ...(process.env.TREEMON_TERMINAL_SESSION_ID
          ? { terminalSessionId: process.env.TREEMON_TERMINAL_SESSION_ID }
          : {}),
      };

      const report = toReport(context, event)

      if (report) {
        if (event.toolCallId) report.toolCallId = event.toolCallId;
        await post(report, budget);
      }
    }
  }
} catch {
  // Deliberately silent: a reporting failure must never surface inside the user's session.
}

process.exit(0);
