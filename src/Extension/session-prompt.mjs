import { isValidCanvasFilename } from "./canvas-filename.mjs";

/** @typedef {"canvas" | "agent-prompt" | "startup-prompt"} SessionPromptKind */
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
      return promptForCanvasMessage(transport.prompt, transport.source);
    case "agent-prompt":
    case "startup-prompt":
      return { kind: transport.kind, prompt: transport.prompt };
    default:
      throw new Error("unknown prompt kind");
  }
}

/**
 * Validates a canvas message from either host without adding authoring instructions.
 *
 * @param {string} body
 * @param {unknown} source
 * @returns {SessionPrompt}
 */
export function promptForCanvasMessage(body, source) {
  if (!isRecord(source) ||
      typeof source.worktreePath !== "string" || !source.worktreePath.trim() ||
      !isValidCanvasFilename(source.filename)) {
    throw new Error("missing or invalid canvas source");
  }
  const identity = { worktreePath: source.worktreePath, filename: source.filename };
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

  const { authoringReminder: _legacyReminder, ...payload } = message;
  const envelope = JSON.stringify({ source: identity, payload });
  const prompt = envelope.replaceAll("\u2028", "\\u2028").replaceAll("\u2029", "\\u2029");

  return { kind: "canvas", prompt: `[canvas] ${prompt}` };
}
