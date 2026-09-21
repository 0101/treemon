import { isSystemViewFilename } from "./canvas-doc-kinds.mjs";
import { isValidCanvasFilename } from "./canvas-filename.mjs";

/** @typedef {"canvas" | "agent-prompt"} SessionPromptKind */
export const MAX_CANVAS_MESSAGE_CHARS = 64000;

const CANVAS_EDIT_REMINDER =
  "Keep this edit concise and in the canvas. Preserve the intended audience, reading length, and essential facts or caveats. " +
  "Replace stale or repeated prose rather than appending a recap. Show requested explanations in <details open> with a short summary; leave unrelated sections alone. " +
  "Use direct, concrete language and explain unfamiliar terms. For another audience, follow the canvas skill's audience.md. Use saved profiles only when explicitly selected by the user.";

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
      return promptForCanvasMessage(transport.prompt);
    case "agent-prompt":
      return { kind: transport.kind, prompt: transport.prompt };
    default:
      throw new Error("unknown prompt kind");
  }
}

/**
 * Validates a canvas message from either host and reinforces writing guidance for doc edits
 * inside the existing JSON payload, without scheduling another turn.
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

  const isEdit =
    (message.action === "expand-section" &&
      typeof message.section === "string" && message.section.trim().length > 0) ||
    (message.action === "canvas-selection" &&
      typeof message.intent === "string" && ["explain", "remove", "comment"].includes(message.intent) &&
      typeof message.request === "string" && message.request.trim().length > 0);
  const remind =
    isEdit && isValidCanvasFilename(message.doc) && !isSystemViewFilename(message.doc);
  const prompt = remind
    ? JSON.stringify({ ...message, authoringReminder: CANVAS_EDIT_REMINDER })
    : serialized;

  return { kind: "canvas", prompt: `[canvas] ${prompt}` };
}
