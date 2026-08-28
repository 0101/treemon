import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { MAX_CANVAS_MESSAGE_CHARS } from "../../Extension/session-prompt.mjs";

function json(relativeUrl) {
  return JSON.parse(readFileSync(new URL(relativeUrl, import.meta.url), "utf8"));
}

test("extension packages and static checking use one pinned Copilot SDK version", () => {
  const root = json("../../../package.json");
  const canvas = json("../../Extension/package.json");
  const reporting = json("../../Extension/reporting/package.json");

  assert.equal(canvas.dependencies["@github/copilot-sdk"], "1.0.9");
  assert.equal(reporting.dependencies["@github/copilot-sdk"], "1.0.9");
  assert.equal(root.devDependencies["@github/copilot-sdk"], "1.0.9");
});

test("browser fallback and canvasSend share one payload cap", () => {
  const canvasSend =
    readFileSync(new URL("../../Extension/canvas-send.js", import.meta.url), "utf8");
  const helperCap = Number(canvasSend.match(/var MAX=(\d+);/)?.[1]);

  assert.equal(MAX_CANVAS_MESSAGE_CHARS, helperCap);
});

test("each installed extension keeps its session-id compatibility boundary local", () => {
  const canvas =
    readFileSync(new URL("../../Extension/extension.mjs", import.meta.url), "utf8");
  const reporting =
    readFileSync(new URL("../../Extension/reporting/extension.mjs", import.meta.url), "utf8");
  const fallback =
    /sessionWithLegacyId\.sessionId \?\? sessionWithLegacyId\.id/;

  assert.match(canvas, fallback);
  assert.match(reporting, fallback);
  assert.doesNotMatch(reporting, /session-identity\.mjs/);
});
