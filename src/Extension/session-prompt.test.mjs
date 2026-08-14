import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  promptForCanvasMessage,
  promptForSession,
} from "./session-prompt.mjs";

test("canvas transport preserves the existing canvas prompt prefix", () => {
  assert.deepEqual(
    promptForSession(JSON.stringify({
      kind: "canvas",
      prompt: "{\"action\":\"refresh\"}",
    })),
    { kind: "canvas", prompt: "[canvas] {\"action\":\"refresh\"}" },
  );
});

test("agent-prompt transport reaches the session without a canvas prefix", () => {
  assert.deepEqual(
    promptForSession(JSON.stringify({
      kind: "agent-prompt",
      prompt: "Sync with upstream/main when safe.",
    })),
    { kind: "agent-prompt", prompt: "Sync with upstream/main when safe." },
  );
});

test("invalid transport is rejected instead of reaching session.send", () => {
  assert.throws(() => promptForSession("not-json"), /invalid JSON/);
  assert.throws(
    () => promptForSession(JSON.stringify({ kind: "unknown", prompt: "text" })),
    /unknown prompt kind/,
  );
  assert.throws(
    () => promptForSession(JSON.stringify({ kind: "agent-prompt" })),
    /missing prompt/,
  );
  assert.throws(
    () => promptForSession(JSON.stringify({ kind: "agent-prompt", prompt: 42 })),
    /missing prompt/,
  );
  assert.throws(() => promptForSession(JSON.stringify([])), /missing prompt/);
});

test("browser messages use the same validated canvas transport", () => {
  assert.deepEqual(
    promptForCanvasMessage(JSON.stringify({
      action: "canvas-selection",
      intent: "explain",
      selectedText: "selected",
    })),
    {
      kind: "canvas",
      prompt:
        '[canvas] {"action":"canvas-selection","intent":"explain","selectedText":"selected"}',
    },
  );
});

test("browser messages reject malformed and blank actions", () => {
  assert.throws(() => promptForCanvasMessage("not-json"), /invalid JSON/);
  assert.throws(() => promptForCanvasMessage(JSON.stringify([])), /missing action/);
  assert.throws(
    () => promptForCanvasMessage(JSON.stringify({ action: "   " })),
    /missing action/,
  );
  assert.throws(
    () => promptForCanvasMessage(JSON.stringify({ action: 42 })),
    /missing action/,
  );
});

test("inject delivery uses the existing serialized enqueueSend path", () => {
  const extension = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");
  assert.match(extension, /promptForSession\(body\)/);
  assert.match(extension, /enqueueSend\(session, kind, prompt\)/);
  assert.match(extension, /promptForCanvasMessage\(body\)/);
  assert.match(extension, /enqueueSend\(session, transport\.kind, transport\.prompt\)/);
});
