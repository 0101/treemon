/** @typedef {"canvas" | "agent-prompt"} SessionPromptKind */
export const MAX_CANVAS_MESSAGE_CHARS = 64000;

/**
 * @typedef SessionPrompt
 * @property {SessionPromptKind} kind
 * @property {string} prompt
 */

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** @param {string} body */
function parseJson(body) {
  /** @type {unknown} */
  let value;
  try {
    value = JSON.parse(body);
  } catch {
    throw new Error("invalid JSON");
  }
  return value;
}

/**
 * Returns the transport kind alongside the session prompt text, so the send queue can compare exact
 * payloads without re-parsing the body or conflating a canvas payload with a same-text agent prompt.
 *
 * @param {string} body
 * @returns {SessionPrompt}
 */
export function promptForSession(body) {
  const transport = parseJson(body);
  if (!isRecord(transport) || typeof transport.prompt !== "string") {
    throw new Error("missing prompt");
  }

  switch (transport.kind) {
    case "canvas":
      return { kind: transport.kind, prompt: `[canvas] ${transport.prompt}` };
    case "agent-prompt":
      return { kind: transport.kind, prompt: transport.prompt };
    default:
      throw new Error("unknown prompt kind");
  }
}

/**
 * Validates a browser canvas message and converts it to the same queued session transport used by
 * `/inject`.
 *
 * @param {string} body
 * @returns {SessionPrompt}
 */
export function promptForCanvasMessage(body) {
  const message = parseJson(body);
  if (!isRecord(message) || !Object.hasOwn(message, "action")) {
    throw new Error("missing action");
  }
  if (typeof message.action !== "string" || !message.action.trim()) {
    throw new Error("action must be a nonblank string");
  }

  const serialized = JSON.stringify(message);
  if (serialized.length > MAX_CANVAS_MESSAGE_CHARS) {
    throw new Error("payload too large");
  }

  return { kind: "canvas", prompt: `[canvas] ${serialized}` };
}
