// The pure half of the Claude Code reporter: hook payload in, Treemon events out. Kept separate from
// report.mjs so it can be imported and tested - report.mjs reads stdin and posts as soon as it is
// loaded, and every failure in it is deliberately swallowed, so a mapping regression would otherwise
// exit successfully and be invisible.

import { createHash } from "node:crypto";
import { homedir } from "node:os";
import { join } from "node:path";

import { buildReport, buildNonBlankMessageReport } from "../Extension/reporting/reporting-core.mjs";

/** Tools whose whole purpose is to block on the human, which is what Treemon shows as WaitingForUser. */
const ASK_USER_TOOLS = new Set(["AskUserQuestion", "ExitPlanMode"]);

/** Kinds the server rejects without a message, so an event carrying no text is dropped instead. */
const MESSAGE_REQUIRED = new Set([
  "user_prompt",
  "assistant_message",
  "intent_reported",
  "title_reported",
  "title_bootstrap",
]);

const TOOL_CALL_ID_REQUIRED = new Set(["background_agent_started", "background_agent_finished"]);

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

/** @param {unknown} value */
const trimmed = (value) => (typeof value === "string" && value.trim() ? value.trim() : undefined);

/**
 * The question an ask_user tool is blocking on, when it has one to show. ExitPlanMode blocks on a
 * plan rather than a question, and a whole plan is not a prompt line, so it reports the wait with no
 * text - which the server accepts.
 *
 * @param {Record<string, any>} hook
 */
export function askUserPrompt(hook) {
  const questions = hook.tool_input?.questions;
  return Array.isArray(questions) ? trimmed(questions[0]?.question) : undefined;
}

/**
 * Claude's hook events, mapped onto Treemon's vocabulary. A hook can produce more than one event:
 * submitting a prompt clears any pending question, carries the prompt text, and starts a turn.
 *
 * `facts` carries what only the transcript knows - Claude's own generated session title and what it
 * last said - because no hook payload contains either.
 *
 * @param {Record<string, any>} hook
 * @param {{title?: string, assistantText?: string}} [facts]
 * @returns {{kind: string, text?: string, toolCallId?: string|null, skillName?: string}[]}
 */
export function treemonEvents(hook, facts = {}) {
  switch (hook.hook_event_name) {
    // Registers the session as present but not working. A bare heartbeat does not open a session, so
    // a started-and-waiting Claude would otherwise stay invisible until its first prompt. Compaction
    // reuses this hook mid-turn, though, and reporting idle there would blank an working agent.
    case "SessionStart":
      return hook.source === "compact"
        ? [{ kind: "heartbeat" }]
        : // A resumed session already has a title; bootstrap is the kind that restores one without
          // claiming it just changed.
          [{ kind: "went_idle" }, { kind: "title_bootstrap", text: facts.title }];
    // An ask_user wait is a durable gate on the server: it holds WaitingForUser until told the input
    // completed, so everything that means "the human has answered" clears it first.
    case "UserPromptSubmit":
      return [
        { kind: "user_input_completed" },
        { kind: "user_prompt", text: hook.prompt },
        { kind: "turn_started" },
      ];
    // The end of a turn is the one moment both facts are settled: Claude has finished speaking, and
    // has restamped the title if the turn changed what the session is about.
    case "Stop":
      return [
        { kind: "user_input_completed" },
        { kind: "assistant_message", text: facts.assistantText },
        { kind: "title_reported", text: facts.title },
        { kind: "turn_ended" },
      ];
    case "SessionEnd":
      return [{ kind: "user_input_completed" }, { kind: "went_idle" }];
    // Permission prompts and idle notifications both arrive here, and both mean the same thing to a
    // dashboard: this agent is blocked on a human.
    case "Notification":
      return [{ kind: "awaiting_user_input", text: hook.message }];
    case "PreToolUse":
      if (hook.tool_name === "Task")
        return [{ kind: "background_agent_started", toolCallId: subagentId(hook) }];
      if (hook.tool_name === "Skill")
        return [{ kind: "skill_invoked", skillName: trimmed(hook.tool_input?.skill) }];
      if (ASK_USER_TOOLS.has(hook.tool_name))
        return [{ kind: "awaiting_user_input", text: askUserPrompt(hook) }];
      return [{ kind: "heartbeat" }];
    // A tool that fails or is denied never reaches PostToolUse, so those hooks close the delegated
    // agent too. Without them a failed sub-agent leaves its clock open and pins the card to Working.
    // The same applies to a question: refusing or failing it ends the wait as surely as answering.
    case "PostToolUse":
    case "PostToolUseFailure":
    case "PermissionDenied":
      if (hook.tool_name === "Task")
        return [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }];
      if (ASK_USER_TOOLS.has(hook.tool_name)) return [{ kind: "user_input_completed" }];
      return [{ kind: "heartbeat" }];
    case "SubagentStop":
      return [{ kind: "background_agent_finished", toolCallId: subagentId(hook) }];
    default:
      // An unrecognised hook still says the session is alive, which beats letting a stretch of
      // unmapped events decay it to Idle.
      return [{ kind: "heartbeat" }];
  }
}

/**
 * The message builder yields nothing for blank text, and some kinds are still meaningful without
 * one. The kinds the server requires a message for are dropped instead, because it would reject the
 * bare event. A delegated-agent event without an id, or a skill without a name, is dropped for the
 * same reason.
 *
 * @param {Record<string, any>} context
 * @param {{kind: string, text?: string, toolCallId?: string|null, skillName?: string}} event
 */
export function toReport(context, event) {
  if (TOOL_CALL_ID_REQUIRED.has(event.kind) && !event.toolCallId) return null;

  if (event.kind === "skill_invoked") {
    const skillName = trimmed(event.skillName);
    return skillName ? { ...buildReport(context, "skill_invoked"), skillName } : null;
  }

  const report =
    (event.text ? buildNonBlankMessageReport(context, event.kind, event.text) : null) ??
    (MESSAGE_REQUIRED.has(event.kind) ? null : buildReport(context, event.kind));

  if (report && event.toolCallId) report.toolCallId = event.toolCallId;

  return report;
}

/** @param {Record<string, any>} entry */
function assistantTextOf(entry) {
  const content = entry.message?.content;
  if (!Array.isArray(content)) return undefined;

  const blocks = content
    .filter((block) => block?.type === "text")
    .map((block) => trimmed(block.text))
    .filter(Boolean);

  return blocks.length ? blocks.join("\n\n") : undefined;
}

/**
 * What Claude's transcript knows that its hooks do not. Claude restamps its own generated title as
 * an `ai-title` entry, and its replies are `assistant` entries; neither reaches a hook payload, so
 * without this a Claude card can show only that it is working, never what it is working on.
 *
 * Read from the end, because the caller passes a tail of the file rather than the whole thing - a
 * transcript runs to megabytes and this is on the critical path of a hook Claude waits for. That
 * makes the first line a likely fragment, which simply fails to parse and is skipped.
 *
 * A sidechain entry is a delegated agent talking, not the session, so it is not the session's message.
 *
 * @param {string} chunk
 * @returns {{title?: string, assistantText?: string}}
 */
export function transcriptFacts(chunk) {
  const lines = chunk.split("\n");
  let title;
  let assistantText;

  for (let i = lines.length - 1; i >= 0 && !(title && assistantText); i -= 1) {
    const line = lines[i].trim();
    if (!line.startsWith("{")) continue;

    let entry;
    try {
      entry = JSON.parse(line);
    } catch {
      continue;
    }

    if (!title && entry.type === "ai-title") title = trimmed(entry.aiTitle);
    if (!assistantText && entry.type === "assistant" && !entry.isSidechain)
      assistantText = assistantTextOf(entry);
  }

  return { title, assistantText };
}

/**
 * Hooks fire only when Claude does something, so a turn spending minutes inside one tool call emits
 * nothing at all - and the server demotes any non-Idle session whose last_seen is older than its
 * five-minute staleness timeout. A detached beater covers that gap the way the Copilot extension's
 * timer does. One minute is comfortably under the timeout, so a few missed beats are survivable.
 */
export const HEARTBEAT_INTERVAL_MS = 60000;

/**
 * How old a claim may be before the session is assumed to have no beater. Long enough to survive two
 * missed beats, so an alive-but-slow beater is not duplicated; short enough that a crashed one is
 * replaced within the staleness timeout it exists to defend.
 */
export const CLAIM_STALE_MS = 150000;

/**
 * A backstop, not a lifecycle: turn end retires the beater, and this only bounds one that never
 * heard about it (a Claude killed outright mid-turn). Long enough not to cut a genuinely long turn
 * short.
 */
export const HEARTBEAT_MAX_MS = 4 * 60 * 60 * 1000;

/**
 * Where a session's beater claim lives. Under Treemon's own config directory rather than the system
 * temp directory: temp is shared with every other user on the machine, so a claim there could be
 * pre-created as a symlink pointing anywhere the hook can write. `~/.treemon` is the convention the
 * server already uses, and TREEMON_CONFIG_DIR redirects it the same way for tests.
 *
 * The session id is Claude's, not ours, so it is hashed rather than pasted into a path.
 *
 * Pure in the sense that matters here - same session, same path - though it reads the environment to
 * build one; report.mjs and heartbeat.mjs must agree on it, so it has exactly one definition.
 *
 * @param {string} sessionId
 */
export function claimPath(sessionId) {
  const name = createHash("sha256").update(sessionId).digest("hex").slice(0, 32);
  const configDir = process.env.TREEMON_CONFIG_DIR || join(homedir(), ".treemon");
  return join(configDir, "claude-heartbeat", `${name}.json`);
}

/**
 * Whether a claim still has a beater behind it. A claim with no owner is a retired one: turn end
 * clears the owner in place rather than deleting the file, so the beater reading it next cannot
 * mistake its own write for the claim still being live.
 *
 * @param {{owner?: string|null, beatAt?: string}|null|undefined} claim
 * @param {number} nowMs
 */
export function claimIsLive(claim, nowMs) {
  if (!claim?.owner) return false;
  const beatAt = Date.parse(claim.beatAt ?? "");
  return Number.isFinite(beatAt) && nowMs - beatAt < CLAIM_STALE_MS;
}
