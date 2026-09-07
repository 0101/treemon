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

test("reporting startup diagnostics expose process and inherited-origin presence without the origin value", () => {
  const reporting =
    readFileSync(new URL("../../Extension/reporting/extension.mjs", import.meta.url), "utf8");

  assert.match(
    reporting,
    /startup pid=\$\{process\.pid\} parentPid=\$\{parentProcessId\} terminalOrigin=\$\{terminalSessionId \? "present" : "absent"\} endpoints=\$\{activityUrls\.length\}/,
  );
  assert.doesNotMatch(
    reporting,
    /startup[^`]*\$\{terminalSessionId\}/,
    "the exact terminal origin must not enter extension diagnostics",
  );
});

test("canvas bridge registration carries exact process and opaque shutdown metadata", () => {
  const canvas =
    readFileSync(new URL("../../Extension/extension.mjs", import.meta.url), "utf8");

  assert.match(canvas, /const parentProcessId = process\.ppid;/);
  assert.match(canvas, /process\.env\.TREEMON_TERMINAL_SESSION_ID/);
  assert.match(
    canvas,
    /const shutdownCapability = randomBytes\(32\)\.toString\("base64url"\);/,
  );
  assert.match(canvas, /const shutdownUrl = `http:\/\/127\.0\.0\.1:\$\{port\}\/shutdown`;/);
  assert.match(canvas, /registerWithTreemon\(registration\)/);
  assert.doesNotMatch(
    canvas,
    /log\(`[^`]*\$\{shutdown(?:Capability|Url)\}/,
    "shutdown capabilities and URLs must never enter extension diagnostics",
  );
});

test("extension endpoints share the canonical request-body reader", () => {
  const canvas =
    readFileSync(new URL("../../Extension/extension.mjs", import.meta.url), "utf8");
  const shutdown =
    readFileSync(new URL("../../Extension/shutdown-endpoint.mjs", import.meta.url), "utf8");

  assert.match(canvas, /import \{ readBody \} from "\.\/request-body\.mjs";/);
  assert.match(shutdown, /import \{ readBody \} from "\.\/request-body\.mjs";/);
  assert.doesNotMatch(canvas, /function readBody\(/);
  assert.doesNotMatch(shutdown, /function readShutdownBody\(/);
  assert.match(shutdown, /readBody\(req, MAX_SHUTDOWN_BODY_BYTES\)/);
});

test("unmonitored registration logs the browser-fallback outcome truthfully", () => {
  const canvas =
    readFileSync(new URL("../../Extension/extension.mjs", import.meta.url), "utf8");

  assert.match(
    canvas,
    /monitored\s*\?\s*`registered \$\{registration\.worktreePath\} \(monitored=true\)`\s*:\s*`not registered \$\{registration\.worktreePath\} \(unmonitored; using browser fallback\)`/,
  );
});
