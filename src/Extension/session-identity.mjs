/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/**
 * Reads the current SDK session identifier while preserving compatibility with the older `id`
 * property. Blank and non-string values are not usable identities.
 *
 * @param {unknown} session
 * @returns {string | undefined}
 */
export function sessionIdFrom(session) {
  if (!isRecord(session)) return undefined;
  const raw = session.sessionId ?? session.id;
  if (typeof raw !== "string") return undefined;
  const sessionId = raw.trim();
  return sessionId || undefined;
}
