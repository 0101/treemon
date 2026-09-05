const MAX_MESSAGE_CHARS = 2000;
export const MAX_TOOL_CALL_ID_CHARS = 512;
export const BACKGROUND_AGENT_CLOCK_RETENTION_MS = 5 * 60 * 1000;

/**
 * @typedef ReportBaseContext
 * @property {number} parentProcessId
 * @property {string} sessionId
 * @property {string} [terminalSessionId]
 * @property {string} worktreePath
 * @property {string} provider
 */

/**
 * @typedef ReportContext
 * @property {number} parentProcessId
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
export function isRecord(value) {
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
    parentProcessId: context.parentProcessId,
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
    case "session.shutdown":
      return buildReport(context, "session_closed");
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

/**
 * Historical shutdown belongs to an earlier physical CLI process. Replaying it for a newly resumed
 * process would close the new exact identity immediately, so shutdown is live-only.
 *
 * @param {ReportBaseContext} context
 * @param {unknown} eventValue
 */
export function reportForReplaySdkEvent(context, eventValue) {
  return isRecord(eventValue) && eventValue.type === "session.shutdown"
    ? null
    : reportForSdkEvent(context, eventValue);
}

/** @param {Record<string, unknown>} report */
function reportOccurrenceTime(report) {
  const parsed = Date.parse(stringValue(report.occurredAt) ?? "");
  return Number.isNaN(parsed) ? -Infinity : parsed;
}

/**
 * @param {Record<string, unknown>} left
 * @param {Record<string, unknown>} right
 */
export function compareReportsByOccurrence(left, right) {
  const leftTime = reportOccurrenceTime(left);
  const rightTime = reportOccurrenceTime(right);
  if (leftTime !== rightTime) return leftTime < rightTime ? -1 : 1;

  const leftId = stringValue(left.eventId) ?? "";
  const rightId = stringValue(right.eventId) ?? "";
  return leftId < rightId ? -1 : leftId > rightId ? 1 : 0;
}

/** @param {Record<string, unknown> | null} current @param {Record<string, unknown>} next */
function newestReport(current, next) {
  if (!current) return next;
  return compareReportsByOccurrence(current, next) <= 0 ? next : current;
}

/**
 * Keep only the current-process facts that can be missing from an in-flight getEvents()
 * snapshot. They are replayed after history on reconnect; matching event IDs make the overlap
 * idempotent.
 */
export function createCurrentProcessState(now = Date.now) {
  /** @type {Record<string, unknown> | null} */
  let lifecycle = null;
  /** @type {Record<string, unknown> | null} */
  let skill = null;
  /** @type {Record<string, unknown> | null} */
  let intent = null;
  /** @type {Record<string, unknown> | null} */
  let title = null;
  /** @type {Record<string, unknown> | null} */
  let userMessage = null;
  /** @type {Record<string, unknown> | null} */
  let assistantMessage = null;
  /** @type {Record<string, unknown> | null} */
  let awaitingUser = null;
  /** @type {Record<string, unknown> | null} */
  let userInputCompleted = null;
  /** @type {Record<string, unknown> | null} */
  let usage = null;
  /** @type {Map<string, { started: Record<string, unknown> | null, finished: Record<string, unknown> | null }>} */
  const backgroundAgents = new Map();

  function pruneBackgroundAgents() {
    const cutoff = now() - BACKGROUND_AGENT_CLOCK_RETENTION_MS;
    for (const [toolCallId, { started, finished }] of backgroundAgents) {
      if (!started || !finished) continue;

      const isActive = reportOccurrenceTime(started) > reportOccurrenceTime(finished);
      const isRecentCompletion = reportOccurrenceTime(finished) > cutoff;
      if (!isActive && !isRecentCompletion) backgroundAgents.delete(toolCallId);
    }
  }

  /** @param {Record<string, unknown>} report */
  function observe(report) {
    const kind = stringValue(report.kind);
    switch (kind) {
      case "turn_started":
      case "turn_ended":
      case "went_idle":
        lifecycle = newestReport(lifecycle, report);
        break;
      default:
        break;
    }

    switch (kind) {
      case "skill_invoked":
        skill = newestReport(skill, report);
        break;
      case "intent_reported":
        intent = newestReport(intent, report);
        break;
      case "title_reported":
      case "title_bootstrap":
        title = newestReport(title, report);
        break;
      case "user_prompt":
        userMessage = newestReport(userMessage, report);
        break;
      case "assistant_message":
        assistantMessage = newestReport(assistantMessage, report);
        break;
      case "awaiting_user_input":
        awaitingUser = newestReport(awaitingUser, report);
        break;
      case "user_input_completed":
        userInputCompleted = newestReport(userInputCompleted, report);
        break;
      case "usage_info":
        usage = newestReport(usage, report);
        break;
      case "background_agent_started":
      case "background_agent_finished": {
        const toolCallId = stringValue(report.toolCallId);
        if (!toolCallId) break;
        const current = backgroundAgents.get(toolCallId) ?? {
          started: null,
          finished: null,
        };
        backgroundAgents.set(toolCallId, kind === "background_agent_started"
          ? { ...current, started: newestReport(current.started, report) }
          : { ...current, finished: newestReport(current.finished, report) });
        break;
      }
      default:
        break;
    }

    pruneBackgroundAgents();
  }

  function snapshot() {
    pruneBackgroundAgents();
    const reports = [
      lifecycle,
      skill,
      intent,
      title,
      userMessage,
      assistantMessage,
      awaitingUser,
      userInputCompleted,
      usage,
      ...[...backgroundAgents.values()].flatMap(({ started, finished }) => [
        started,
        finished,
      ]),
    ].filter((report) => report !== null);

    return mergeReplayReports([], reports);
  }

  return { observe, snapshot };
}

/**
 * @param {Record<string, unknown>[]} historical
 * @param {Record<string, unknown>[]} current
 */
export function mergeReplayReports(historical, current) {
  /** @type {Map<string, Record<string, unknown>>} */
  const byEventId = new Map();
  for (const report of [...historical, ...current]) {
    const eventId = stringValue(report.eventId);
    if (eventId) byEventId.set(eventId, report);
  }

  return [...byEventId.values()].sort(compareReportsByOccurrence);
}
