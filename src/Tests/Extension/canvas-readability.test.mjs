import test from "node:test";
import assert from "node:assert/strict";
import { join, resolve } from "node:path";
import { createCanvasReadabilityHooks, measureCanvasProse } from "../../Extension/canvas-readability.mjs";

const words = (count) => Array.from({ length: count }, () => "word").join(" ");
const prose = (count) => `<p>${words(count)}</p>`;
const worktreePath = resolve("readability-worktree");

function fixture(initialHtml = prose(71)) {
  let html = initialHtml;
  const reads = [];
  const logs = [];
  const hooks = createCanvasReadabilityHooks({
    worktreePath,
    readCanvasFile: async (filename) => {
      reads.push(filename);
      return html;
    },
    log: (message) => logs.push(message),
  });
  const write = (overrides = {}) => hooks.onPostToolUse({
    sessionId: "author",
    timestamp: new Date(0),
    workingDirectory: worktreePath,
    toolName: "edit",
    toolArgs: { path: join(".agents", "canvas", "review.html") },
    toolResult: { resultType: "success", textResultForLlm: "saved" },
    ...overrides,
  }, { sessionId: "author" });
  const nextPrompt = () => hooks.onUserPromptSubmitted({
    sessionId: "author", timestamp: new Date(0), workingDirectory: worktreePath, prompt: "Update the canvas",
  }, { sessionId: "author" });
  return { write, nextPrompt, reads, logs, hooks, setHtml: (value) => { html = value; } };
}

test("counts overview words and block lengths rather than markup or source line breaks", () => {
  assert.deepEqual(measureCanvasProse(
    "<h1>A result</h1><p>One <strong>clear</strong>\n statement.</p><ul><li>Next step</li></ul>",
  ), { words: 7, longestBlock: 3 });
});

test("ignores comments, document head, code, scripts, styles, and nested disclosures including open", () => {
  const long = words(600);
  assert.deepEqual(measureCanvasProse(
    `<head><title>${long}</title><style>${long}</style></head>` +
    `<!-- ${long} --><script>${long}</script><template>${long}</template>` +
    `<pre>${long}</pre><code>${long}</code>` +
    `<DETAILS open><summary>${long}</summary><details><p>${long}</p></details>${long}</DETAILS>` +
    "<p>Keep this conclusion.</p>",
  ), { words: 3, longestBlock: 3 });
});

test("counts diagram labels but not SVG metadata or entities as prose", () => {
  assert.deepEqual(measureCanvasProse(
    `<svg><title>${words(600)}</title><desc>${words(600)}</desc><defs>${words(600)}</defs>` +
    "<text>Write</text><text>Check</text></svg><p>A&nbsp;&amp;&#160;B</p>",
  ), { words: 4, longestBlock: 2 });
});

test("keeps diagram labels and captions separate from adjacent prose without consuming its warning budget", async () => {
  const diagram = `<svg>${Array.from({ length: 5 }, () => `<text>${words(3)}</text>`).join("")}</svg>`;
  for (const [figure, expectedWords] of [
    [diagram, 75],
    [`<figure>${diagram}<figcaption>${words(15)}</figcaption></figure>`, 90],
  ]) {
    const html = figure + prose(60);
    assert.deepEqual(measureCanvasProse(html), { words: expectedWords, longestBlock: 60 });
    const check = fixture(html);
    assert.equal(await check.write(), undefined);
    check.setHtml(figure + prose(71));
    assert.match((await check.write()).additionalContext, /longest prose block is 71 words/);
  }
});

test("keeps short prose at the thresholds quiet and flags oversized blocks", async () => {
  const check = fixture(prose(70));
  assert.equal(await check.write(), undefined);
  check.setHtml(prose(71));
  const feedback = await check.write();
  assert.match(feedback.additionalContext, /longest prose block is 71 words \(guide: 70\)/);
  assert.match(feedback.additionalContext, /useful diagrams/);
  assert.ok(feedback.additionalContext.length < 600);
  assert.deepEqual(Object.keys(feedback), ["additionalContext"]);
});

test("flags a long overview even when every individual block is short", async () => {
  const check = fixture(Array.from({ length: 9 }, () => prose(50)).join(""));
  assert.equal(await check.write(), undefined);
  check.setHtml(Array.from({ length: 9 }, () => prose(50)).join("") + prose(1));
  const feedback = await check.write();
  assert.match(feedback.additionalContext, /main-view text is 451 words \(guide: 450\)/);
  assert.doesNotMatch(feedback.additionalContext, /longest prose block/);
});

test("limits each document to one warning per prompt and suppresses unchanged or improving prose", async () => {
  const check = fixture(prose(90));
  assert.ok(await check.write());
  check.setHtml(prose(100));
  assert.equal(await check.write(), undefined);
  check.nextPrompt();
  assert.equal(await check.write(), undefined);
  check.setHtml(prose(80));
  assert.equal(await check.write(), undefined);
  check.setHtml(prose(85));
  assert.match((await check.write()).additionalContext, /85 words/);
});

test("keeps prompt budgets and measurements isolated by editing session", async () => {
  const check = fixture();
  assert.ok(await check.write());
  assert.ok(await check.write({ sessionId: "another-author" }));
  check.setHtml(prose(90));
  check.nextPrompt();
  assert.ok(await check.write());
  assert.equal(await check.write({ sessionId: "another-author" }), undefined);
});

test("ignores failures, shell writes, unsafe paths, generated views, and different worktrees", async () => {
  const check = fixture();
  for (const overrides of [
    { toolResult: { resultType: "failure", textResultForLlm: "failed" } },
    { toolName: "powershell", toolArgs: { command: "not executed" } },
    { toolArgs: { path: join(".agents", "canvas", "diff.html") } },
    { toolArgs: { path: join(".agents", "canvas", "beads.html") } },
    { toolArgs: { path: join(".agents", "canvas", "unsafe name.html") } },
    { toolArgs: { path: join(".agents", "canvas", "archive", "old.html") } },
    { toolArgs: { path: join("..", ".agents", "canvas", "review.html") } },
    { workingDirectory: resolve("another-worktree") },
  ]) {
    assert.equal(await check.write(overrides), undefined);
  }
  assert.deepEqual(check.reads, []);
});

test("handles successful create and multi-file apply_patch with bounded feedback", async () => {
  const check = fixture();
  assert.ok(await check.write({ toolName: "create" }));
  const patch = ["*** Begin Patch",
    ...["one", "two", "three", "four"].flatMap((name) => [
      `*** Add File: ${join(".agents", "canvas", `${name}.html`)}`, "+content",
    ]),
    "*** End Patch",
  ].join("\n");
  const feedback = await check.write({ toolName: "apply_patch", toolArgs: patch });
  assert.match(feedback.additionalContext, /"one.html"/);
  assert.match(feedback.additionalContext, /"three.html"/);
  assert.doesNotMatch(feedback.additionalContext, /"four.html"/);
  assert.match((await check.write({
    toolArgs: { path: join(".agents", "canvas", "four.html") },
  })).additionalContext, /"four.html"/);
});

test("logs unreadable files without failing a successful tool result", async () => {
  const logs = [];
  const hooks = createCanvasReadabilityHooks({
    worktreePath,
    readCanvasFile: async () => { throw Object.assign(new Error("private path"), { code: "ENOENT" }); },
    log: (message) => logs.push(message),
  });
  assert.equal(await hooks.onPostToolUse({
    sessionId: "author", timestamp: new Date(0), workingDirectory: worktreePath,
    toolName: "create", toolArgs: { path: join(".agents", "canvas", "missing.html") },
    toolResult: { resultType: "success", textResultForLlm: "created" },
  }, { sessionId: "author" }), undefined);
  assert.deepEqual(logs, ['canvas readability: read failed for "missing.html" (ENOENT)']);
});

test("clears session observations on session end", async () => {
  const check = fixture();
  assert.ok(await check.write());
  check.hooks.onSessionEnd({
    sessionId: "author", timestamp: new Date(0), workingDirectory: worktreePath, reason: "complete",
  }, { sessionId: "author" });
  assert.ok(await check.write());
});
