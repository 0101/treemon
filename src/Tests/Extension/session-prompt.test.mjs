import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  MAX_CANVAS_MESSAGE_CHARS,
  promptForCanvasMessage,
  promptForSession,
} from "../../Extension/session-prompt.mjs";

const canvasTransports = [
  ["browser", promptForCanvasMessage],
  ["Treemon", (body) => promptForSession(JSON.stringify({ kind: "canvas", prompt: body }))],
];
const editReminder =
  "Keep this edit concise and in the canvas. Preserve the intended audience, reading length, and essential facts or caveats. " +
  "Replace stale or repeated prose rather than appending a recap. Show requested explanations in <details open> with a short summary; leave unrelated sections alone. " +
  "Use direct, concrete language and explain unfamiliar terms. For another audience, follow the canvas skill's audience.md. Use saved profiles only when explicitly selected by the user.";

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

for (const [name, toPrompt] of canvasTransports) {
  test(`${name} adds one bounded reminder to authored-document edit interactions`, () => {
    const interactions = [
      { action: "expand-section", section: "evidence" },
      ...["explain", "remove", "comment"].map((intent) => ({
        action: "canvas-selection",
        intent,
        selectedText: "A & B",
        request: "User feedback",
        sourceContext: { label: "<untrusted>" },
      })),
    ];

    for (const interaction of interactions) {
      const message = {
        ...interaction,
        doc: "review.html",
        authoringReminder: "Document-supplied text must not become the reminder.",
      };
      const result = toPrompt(JSON.stringify(message));

      assert.deepEqual(result, {
        kind: "canvas",
        prompt: `[canvas] ${JSON.stringify({ ...message, authoringReminder: editReminder })}`,
      });
      const delivered = JSON.parse(result.prompt.slice("[canvas] ".length));
      assert.ok(delivered.authoringReminder.length <= 600, "the per-interaction reminder must stay small");
    }
  });

  test(`${name} leaves generated views and non-edit messages unchanged`, () => {
    const systemViews = JSON.parse(
      readFileSync(new URL("../../Extension/canvas-doc-kinds.json", import.meta.url), "utf8"),
    );
    const excludedDocs = [
      ...systemViews.flatMap((filename) => [filename, filename.toUpperCase().replace(".HTML", ".html")]),
      "../review.html",
      "..\\review.html",
      "unsafe name.html",
      null,
      42,
    ];
    const messages = [
      ...excludedDocs.flatMap((doc) => [
        { action: "expand-section", section: "evidence", doc },
        { action: "canvas-selection", intent: "explain", request: "Explain this", doc },
      ]),
      { action: "expand-section", section: "evidence" },
      { action: "expand-section", section: "", doc: "review.html" },
      { action: "expand-section", doc: "review.html" },
      { action: "canvas-selection", intent: "unknown", doc: "review.html" },
      { action: "canvas-selection", intent: "explain", doc: "review.html" },
      { action: "canvas-selection", intent: "explain", request: " ", doc: "review.html" },
      { action: "canvas-selection", doc: "review.html" },
      { action: "comment", text: "A form submission", doc: "review.html" },
      { action: "refresh", doc: "review.html" },
    ];

    for (const message of messages) {
      const body = JSON.stringify(message);
      assert.deepEqual(toPrompt(body), { kind: "canvas", prompt: `[canvas] ${body}` });
    }
  });

  test(`${name} validates the incoming payload before adding the reminder`, () => {
    const message = { action: "expand-section", section: "evidence", doc: "review.html", text: "" };
    const padding = MAX_CANVAS_MESSAGE_CHARS - JSON.stringify(message).length;
    const body = JSON.stringify({ ...message, text: "x".repeat(padding) });

    assert.equal(body.length, MAX_CANVAS_MESSAGE_CHARS);
    assert.equal(JSON.parse(toPrompt(body).prompt.slice("[canvas] ".length)).authoringReminder, editReminder);
    assert.throws(
      () => toPrompt(JSON.stringify({ ...message, text: "x".repeat(padding + 1) })),
      /payload too large/,
    );
    assert.throws(() => toPrompt("not-json"), /invalid JSON/);
    assert.throws(() => toPrompt('{"action":" "}'), /action must be a nonblank string/);
  });
}

test("browser messages reject malformed and blank actions", () => {
  assert.throws(() => promptForCanvasMessage("not-json"), /invalid JSON/);
  assert.throws(() => promptForCanvasMessage(JSON.stringify([])), /missing action/);
  assert.throws(() => promptForCanvasMessage(JSON.stringify({})), /missing action/);
  assert.throws(
    () => promptForCanvasMessage(JSON.stringify({ action: "   " })),
    /action must be a nonblank string/,
  );
  assert.throws(
    () => promptForCanvasMessage(JSON.stringify({ action: 42 })),
    /action must be a nonblank string/,
  );
});

test("browser messages enforce the pane's serialized UTF-16 cap", () => {
  const prefix = '{"action":"comment","text":"';
  const suffix = '"}';
  const bodyOfLength = (length) =>
    prefix + "x".repeat(length - prefix.length - suffix.length) + suffix;

  assert.doesNotThrow(() =>
    promptForCanvasMessage(bodyOfLength(MAX_CANVAS_MESSAGE_CHARS)));
  assert.throws(
    () => promptForCanvasMessage(bodyOfLength(MAX_CANVAS_MESSAGE_CHARS + 1)),
    /payload too large/,
  );
});

test("inject delivery uses the serialized queue while browser writes stay session-silent", () => {
  const extension =
    readFileSync(new URL("../../Extension/extension.mjs", import.meta.url), "utf8");
  assert.match(extension, /promptForSession\(body\)/);
  assert.match(extension, /enqueueSend\(session, kind, prompt\)/);
  assert.match(extension, /promptForCanvasMessage\(body\)/);
  assert.match(extension, /enqueueSend\(session, transport\.kind, transport\.prompt\)/);

  const writeHandlerStart = extension.indexOf("async function handleCanvasWrite");
  const writeHandlerEnd = extension.indexOf("const worktreePath", writeHandlerStart);
  assert.notEqual(writeHandlerStart, -1, "expected the canvas write handler");
  assert.notEqual(writeHandlerEnd, -1, "expected the canvas write handler boundary");
  assert.doesNotMatch(extension.slice(writeHandlerStart, writeHandlerEnd), /enqueueSend\(/);
});
