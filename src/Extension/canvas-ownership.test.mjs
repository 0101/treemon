import test from "node:test";
import assert from "node:assert/strict";
import { EOL } from "node:os";
import { join, resolve } from "node:path";
import {
  canvasChangesForTool,
  createOwnershipDeclarer,
  isValidCanvasFilename,
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

test("extracts lifecycle-aware canvas changes from apply_patch headers", () => {
  const worktreePath = resolve("worktree");
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
    "*** Delete File: .agents/canvas/deleted.html",
    "*** End Patch",
  ]);

  assert.deepEqual(
    canvasChangesForTool("apply_patch", input, worktreePath),
    [
      { kind: "remove", filename: "updated.html" },
      { kind: "attribute", filename: "moved.html" },
      { kind: "attribute", filename: "added.html" },
      { kind: "remove", filename: "deleted.html" },
    ],
  );
});

test("preserves create and edit canvas detection", () => {
  const worktreePath = resolve("worktree");
  assert.deepEqual(
    canvasChangesForTool(
      "create",
      { path: join(worktreePath, ".agents", "canvas", "created.html") },
      worktreePath,
    ),
    [{ kind: "attribute", filename: "created.html" }],
  );
  assert.deepEqual(
    canvasChangesForTool(
      "edit",
      JSON.stringify({ file_path: ".agents/canvas/edited.html" }),
      worktreePath,
    ),
    [{ kind: "attribute", filename: "edited.html" }],
  );
  assert.deepEqual(canvasChangesForTool("edit", { path: "src/ignored.html" }, worktreePath), []);
  assert.equal(isValidCanvasFilename("unsafe name.html"), false);
});

test("ignores canvas-shaped paths outside the worktree root canvas directory", () => {
  const worktreePath = resolve("worktree");
  const nestedPath = join("fixtures", ".agents", "canvas", "report.html");

  assert.deepEqual(
    canvasChangesForTool(
      "apply_patch",
      patch(["*** Begin Patch", `*** Update File: ${nestedPath}`, "*** End Patch"]),
      worktreePath,
    ),
    [],
  );
  assert.deepEqual(
    canvasChangesForTool(
      "edit",
      { path: join(worktreePath, nestedPath) },
      worktreePath,
    ),
    [],
  );
});

test("attributes all buffered apply_patch writes only after successful completion", () => {
  const worktreePath = resolve("worktree");
  const session = fakeSession();
  const watcher = watchCanvasWrites(session, worktreePath, () => 123);
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

  assert.deepEqual(writes, [
    { kind: "attribute", filename: "one.html", version: 123 },
    { kind: "attribute", filename: "two.html", version: 123 },
  ]);
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

  await declarations.declare({ kind: "attribute", filename: "report.html", version: 1 });
  await declarations.replay();
  await declarations.replay();

  assert.deepEqual(attempts, [
    { kind: "attribute", filename: "report.html", version: 1 },
    { kind: "attribute", filename: "report.html", version: 1 },
  ]);
});

test("does not retain an unmonitored ownership response", async () => {
  const attempts = [];
  const declarations = createOwnershipDeclarer(async (filename) => {
    attempts.push(filename);
    return { ok: true, attributed: false };
  });

  await declarations.declare({ kind: "attribute", filename: "report.html", version: 1 });
  await declarations.replay();

  assert.deepEqual(attempts, [{ kind: "attribute", filename: "report.html", version: 1 }]);
});

test("does not retain a permanent ownership failure", async () => {
  const attempts = [];
  const declarations = createOwnershipDeclarer(async (filename) => {
    attempts.push(filename);
    return { ok: false, retryable: false, error: "bad request" };
  });

  await declarations.declare({ kind: "attribute", filename: "report.html", version: 1 });
  await declarations.replay();

  assert.deepEqual(attempts, [{ kind: "attribute", filename: "report.html", version: 1 }]);
});

test("a newer failed write replaces an older pending declaration for the same doc", async () => {
  const attempts = [];
  const outcomes = [
    { ok: false, retryable: true, error: "offline" },
    { ok: false, retryable: true, error: "offline" },
    { ok: true, attributed: true },
  ];
  const declarations = createOwnershipDeclarer(async (write) => {
    attempts.push(write);
    return outcomes.shift();
  });

  await declarations.declare({ kind: "remove", filename: "report.html", version: 1 });
  await declarations.declare({ kind: "attribute", filename: "report.html", version: 2 });
  await declarations.replay();

  assert.deepEqual(attempts, [
    { kind: "remove", filename: "report.html", version: 1 },
    { kind: "attribute", filename: "report.html", version: 2 },
    { kind: "attribute", filename: "report.html", version: 2 },
  ]);
});
