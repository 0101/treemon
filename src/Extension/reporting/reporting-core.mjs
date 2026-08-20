const MAX_MESSAGE_CHARS = 2000;
export const MAX_TOOL_CALL_ID_CHARS = 512;

/**
 * @typedef ReportBaseContext
 * @property {string} sessionId
 * @property {string} [terminalSessionId]
 * @property {string} worktreePath
 * @property {string} provider
 */

/**
 * @typedef ReportContext
 * @property {string} sessionId
 * @property {string} [terminalSessionId]
 * @property {string} worktreePath
 * @property {string} provider
 * @property {string} eventId
 * @property {string} occurredAt
 */

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** @param {unknown} value */
function stringValue(value) {
  return typeof value === "string" ? value : null;
}

/**
 * @param {ReportBaseContext} context
 * @param {unknown} eventValue
 */
export function reportForSdkEvent(context, eventValue) {
  if (!isRecord(eventValue)) return null;
  const eventId = stringValue(eventValue.id)?.trim();
  const occurredAt = stringValue(eventValue.timestamp)?.trim();
  if (!eventId || !occurredAt) return null;

  return mapSdkEvent({ ...context, eventId, occurredAt }, eventValue);
}

/** @param {string} text */
function cap(text) {
  return text.length > MAX_MESSAGE_CHARS ? text.slice(0, MAX_MESSAGE_CHARS) : text;
}

/**
 * @param {ReportContext} context
 * @param {string} kind
 */
export function buildReport(context, kind) {
  const terminalSessionId = context.terminalSessionId?.trim();
  return {
    sessionId: context.sessionId,
    ...(terminalSessionId ? { terminalSessionId } : {}),
    worktreePath: context.worktreePath,
    provider: context.provider,
    eventId: context.eventId,
    occurredAt: context.occurredAt,
    kind,
  };
}

/**
 * @param {ReportContext} context
 * @param {string} kind
 * @param {string} text
 */
function buildMessageReport(context, kind, text) {
  return {
    ...buildReport(context, kind),
    message: { text, at: context.occurredAt },
  };
}

/**
 * @param {ReportContext} context
 * @param {string} kind
 * @param {unknown} text
 */
export function buildNonBlankMessageReport(context, kind, text) {
  const value = stringValue(text);
  return value?.trim() ? buildMessageReport(context, kind, cap(value)) : null;
}

/** @param {Record<string, unknown>} data */
function isSkillContextInjection(data) {
  // Require both trusted SDK source metadata and the matching preamble so genuine user text cannot
  // be mistaken for a runtime injection.
  const source = stringValue(data.source)?.toLowerCase() ?? "";
  const content = stringValue(data.content)?.replace(/^\s+/, "").toLowerCase() ?? "";
  return source.startsWith("skill-") && content.startsWith("<skill-context");
}

/**
 * @param {ReportContext} context
 * @param {Record<string, unknown>} event
 * @param {Record<string, unknown>} data
 */
function backgroundAgentReport(context, event, data) {
  const kind = event.type === "subagent.started"
    ? "background_agent_started"
    : "background_agent_finished";
  const toolCallId = stringValue(data.toolCallId);
  return toolCallId?.trim() && toolCallId.length <= MAX_TOOL_CALL_ID_CHARS
    ? { ...buildReport(context, kind), toolCallId }
    : null;
}

/** @param {unknown} value */
function finiteNumber(value) {
  if (typeof value !== "number" && (typeof value !== "string" || !value.trim())) return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

/**
 * @param {ReportContext} context
 * @param {unknown} eventValue
 */
export function mapSdkEvent(context, eventValue) {
  if (!isRecord(eventValue) || typeof eventValue.type !== "string") return null;
  const event = eventValue;
  const data = isRecord(event.data) ? event.data : {};

  // Lifecycle events carry agentId too, so they must map before the generic sub-agent content filter.
  switch (event.type) {
    case "subagent.started":
    case "subagent.completed":
    case "subagent.failed":
      return backgroundAgentReport(context, event, data);
    default:
      break;
  }

  if (event.agentId) return null;

  switch (event.type) {
    case "assistant.turn_start":
      return buildReport(context, "turn_started");
    case "assistant.turn_end":
      return buildReport(context, "turn_ended");
    case "session.idle":
      return buildReport(context, "went_idle");
    case "skill.invoked": {
      const name = stringValue(data.name)?.trim() ?? "";
      return name ? { ...buildReport(context, "skill_invoked"), skillName: name } : null;
    }
    case "assistant.message":
      return buildNonBlankMessageReport(context, "assistant_message", data.content);
    case "assistant.intent":
      return buildNonBlankMessageReport(context, "intent_reported", data.intent);
    case "session.title_changed":
      return buildNonBlankMessageReport(context, "title_reported", data.title);
    case "user.message":
      return isSkillContextInjection(data)
        ? null
        : buildNonBlankMessageReport(context, "user_prompt", data.content);
    case "session.usage_info": {
      const cur = finiteNumber(data.currentTokens);
      const lim = finiteNumber(data.tokenLimit);
      if (cur === null || lim === null || lim <= 0) return null;
      return {
        ...buildReport(context, "usage_info"),
        currentTokens: Math.max(0, Math.round(cur)),
        tokenLimit: Math.round(lim),
      };
    }
    case "elicitation.requested":
    case "user_input.requested":
      return buildNonBlankMessageReport(
        context,
        "awaiting_user_input",
        data.message ?? data.question,
      ) ?? buildReport(context, "awaiting_user_input");
    case "elicitation.completed":
    case "user_input.completed":
      return buildReport(context, "user_input_completed");
    default:
      return null;
  }
}
