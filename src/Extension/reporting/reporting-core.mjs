const MAX_MESSAGE_CHARS = 2000;
export const MAX_TOOL_CALL_ID_CHARS = 512;

function cap(text) {
  const value = String(text ?? "");
  return value.length > MAX_MESSAGE_CHARS ? value.slice(0, MAX_MESSAGE_CHARS) : value;
}

export function buildReport(context, kind) {
  return {
    sessionId: context.sessionId,
    worktreePath: context.worktreePath,
    provider: context.provider,
    eventId: context.eventId,
    occurredAt: context.occurredAt,
    kind,
  };
}

function buildMessageReport(context, kind, text) {
  return {
    ...buildReport(context, kind),
    message: { text: cap(text), at: context.occurredAt },
  };
}

export function buildNonBlankMessageReport(context, kind, text) {
  const value = String(text ?? "");
  return value.trim() ? buildMessageReport(context, kind, value) : null;
}

function isSkillContextInjection(data) {
  // Require both trusted SDK source metadata and the matching preamble so genuine user text cannot
  // be mistaken for a runtime injection.
  const source = String(data?.source ?? "").toLowerCase();
  const content = String(data?.content ?? "").replace(/^\s+/, "").toLowerCase();
  return source.startsWith("skill-") && content.startsWith("<skill-context");
}

function backgroundAgentReport(context, event, data) {
  const kind = event.type === "subagent.started"
    ? "background_agent_started"
    : "background_agent_finished";
  const toolCallId = String(data.toolCallId ?? "");
  return toolCallId.trim() && toolCallId.length <= MAX_TOOL_CALL_ID_CHARS
    ? { ...buildReport(context, kind), toolCallId }
    : null;
}

export function mapSdkEvent(context, event) {
  const data = event.data ?? {};

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
      const name = String(data.name ?? "").trim();
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
      const cur = Number(data.currentTokens);
      const lim = Number(data.tokenLimit);
      if (!Number.isFinite(cur) || !Number.isFinite(lim) || lim <= 0) return null;
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
