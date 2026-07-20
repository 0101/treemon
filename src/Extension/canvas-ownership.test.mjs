import test from "node:test";
import assert from "node:assert/strict";
import { EOL } from "node:os";
import { join } from "node:path";
import {
  canvasFilenamesForTool,
  createOwnershipDeclarer,
  isValidCanvasFilename,
  replayOwnershipIfMonitored,
  watchCanvasWrites,
} from "./canvas-ownership.mjs";

function patch(lines) {
  return lines.join(EOL);
}

function fakeSession() {
  const handlers = new Map();

  return {
    on(eventName, handler) {
      handlers.set(eventName, [...(handlers.get(eventName) ?? []), handler]);
      return () => handlers.set(eventName, handlers.get(eventName).filter((item) => item !== handler));
    },
    emit(eventName, data) {
      (handlers.get(eventName) ?? []).forEach((handler) => handler({ data }));
    },
  };
}

test("extracts every unique canvas target from apply_patch headers", () => {
  const input = patch([
    "*** Begin Patch",
    "*** Add File: .agents/canvas/added.html",
    "+content",
    "*** Update File: src/ignored.mjs",
    "@@",
    "-old",
    "+new",
    `*** Update File: ${join(".agents", "canvas", "updated.html")}`,
    "@@",
    "-old",
    "+new",
    "*** Move to: .agents/canvas/moved.html",
    "*** Update File: .agents/canvas/added.html",
    "@@",
    "-old",
    "+new",
    "*** Add File: .agents/canvas/nested/ignored.html",
    "+content",
    "*** Add File: .agents/canvas/unsafe name.html",
    "+content",
    "*** End Patch",
  ]);

  assert.deepEqual(
    canvasFilenamesForTool("apply_patch", input),
    ["added.html", "updated.html", "moved.html"],
  );
});

test("preserves create and edit canvas detection", () => {
  assert.deepEqual(
    canvasFilenamesForTool("create", { path: join("repo", ".agents", "canvas", "created.html") }),
    ["created.html"],
  );
  assert.deepEqual(
    canvasFilenamesForTool("edit", JSON.stringify({ file_path: ".agents/canvas/edited.html" })),
    ["edited.html"],
  );
  assert.deepEqual(canvasFilenamesForTool("edit", { path: "src/ignored.html" }), []);
  assert.equal(isValidCanvasFilename("unsafe name.html"), false);
});

test("attributes all buffered apply_patch writes only after successful completion", () => {
  const session = fakeSession();
  const watcher = watchCanvasWrites(session);
  const writes = [];

  session.emit("tool.execution_start", {
    toolCallId: "successful",
    toolName: "apply_patch",
    arguments: patch([
      "*** Begin Patch",
      "*** Add File: .agents/canvas/one.html",
      "+one",
      "*** Update File: .agents/canvas/two.html",
      "@@",
      "-old",
      "+new",
      "*** End Patch",
    ]),
  });
  session.emit("tool.execution_complete", { toolCallId: "successful", success: true });
  session.emit("tool.execution_start", {
    toolCallId: "failed",
    toolName: "apply_patch",
    arguments: patch([
      "*** Begin Patch",
      "*** Add File: .agents/canvas/failed.html",
      "+failed",
      "*** End Patch",
    ]),
  });
  session.emit("tool.execution_complete", { toolCallId: "failed", success: false });

  watcher.activate((filename) => writes.push(filename));

  assert.deepEqual(writes, ["one.html", "two.html"]);
  watcher.stop();
});

test("replays failed declarations once and clears them after success", async () => {
  const attempts = [];
  const outcomes = [
    { ok: false, retryable: true, error: "offline" },
    { ok: true, attributed: true },
  ];
  const declarations = createOwnershipDeclarer(async (filename) => {
    attempts.push(filename);
    return outcomes.shift();
  });

  await declarations.declare("report.html");
  await declarations.replay();
  await declarations.replay();

  assert.deepEqual(attempts, ["report.html", "report.html"]);
});

test("does not retain an unmonitored ownership response", async () => {
  const attempts = [];
  const declarations = createOwnershipDeclarer(async (filename) => {
    attempts.push(filename);
    return { ok: true, attributed: false };
  });

  await declarations.declare("report.html");
  await declarations.replay();

  assert.deepEqual(attempts, ["report.html"]);
});

test("does not retain a permanent ownership failure", async () => {
  const attempts = [];
  const declarations = createOwnershipDeclarer(async (filename) => {
    attempts.push(filename);
    return { ok: false, retryable: false, error: "bad request" };
  });

  await declarations.declare("report.html");
  await declarations.replay();

  assert.deepEqual(attempts, ["report.html"]);
});

test("replays pending ownership only after monitored registration", async () => {
  const registrations = [
    { reachable: false, monitored: false },
    { reachable: true, monitored: false },
    { reachable: true, monitored: true },
  ];
  const replayed = [];

  await Promise.all(
    registrations.map((registration) =>
      replayOwnershipIfMonitored(registration, async () => replayed.push(registration))),
  );

  assert.deepEqual(replayed, [{ reachable: true, monitored: true }]);
});
