import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { promptForSession } from "./session-prompt.mjs";

test("canvas transport preserves the existing canvas prompt prefix", () => {
  assert.equal(
    promptForSession(JSON.stringify({
      kind: "canvas",
      prompt: "{\"action\":\"refresh\"}",
    })),
    "[canvas] {\"action\":\"refresh\"}",
  );
});

test("agent-prompt transport reaches the session without a canvas prefix", () => {
  assert.equal(
    promptForSession(JSON.stringify({
      kind: "agent-prompt",
      prompt: "Sync with upstream/main when safe.",
    })),
    "Sync with upstream/main when safe.",
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
});

test("inject delivery uses the existing serialized enqueueSend path", () => {
  const extension = readFileSync(new URL("./extension.mjs", import.meta.url), "utf8");
  assert.match(extension, /promptForSession\(body\)/);
  assert.match(extension, /enqueueSend\(session, prompt\)/);
});
