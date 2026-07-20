const MAX_MESSAGE_CHARS = 2000;

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

export function buildMessageReport(context, kind, text) {
  return {
    ...buildReport(context, kind),
    message: { text: cap(text), at: context.occurredAt },
  };
}

export function buildTitleBootstrapReport(snapshot, context) {
  const text = String(snapshot?.summary ?? "");
  return text.trim() ? buildMessageReport(context, "title_bootstrap", text) : null;
}
