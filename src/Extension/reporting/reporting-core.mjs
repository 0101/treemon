export const MAX_MESSAGE_CHARS = 2000;

export function cap(text) {
  const value = String(text ?? "");
  return value.length > MAX_MESSAGE_CHARS ? value.slice(0, MAX_MESSAGE_CHARS) : value;
}

export function buildMetadataTitleReport(snapshot, context) {
  const text = String(snapshot?.summary ?? "");
  if (!text.trim()) return null;

  return {
    sessionId: context.sessionId,
    worktreePath: context.worktreePath,
    provider: context.provider,
    eventId: context.eventId,
    occurredAt: context.occurredAt,
    kind: "title_reported",
    message: { text: cap(text), at: context.occurredAt },
  };
}

export async function loadMetadataTitleReport(session, context) {
  return buildMetadataTitleReport(await session.rpc.metadata.snapshot(), context);
}
