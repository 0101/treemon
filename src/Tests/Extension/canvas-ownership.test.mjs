import test from "node:test";
import assert from "node:assert/strict";
import { EOL } from "node:os";
import { join, resolve } from "node:path";
import {
  canvasFilenameForClaim,
  canvasFilenamesForTool,
  watchCanvasWrites,
} from "../../Extension/canvas-ownership.mjs";

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

test("extracts every unique canvas destination from apply_patch headers", () => {
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
    "*** End Patch",
  ]);

  assert.deepEqual(
    canvasFilenamesForTool("apply_patch", input, worktreePath),
    ["moved.html", "added.html"],
  );
});

test("preserves create and edit canvas detection", () => {
  const worktreePath = resolve("worktree");
  assert.deepEqual(
    canvasFilenamesForTool(
      "create",
      { path: join(worktreePath, ".agents", "canvas", "created.html") },
      worktreePath,
    ),
    ["created.html"],
  );
  assert.deepEqual(
    canvasFilenamesForTool(
      "edit",
      JSON.stringify({ file_path: ".agents/canvas/edited.html" }),
      worktreePath,
    ),
    ["edited.html"],
  );
  assert.deepEqual(canvasFilenamesForTool("edit", { path: "src/ignored.html" }, worktreePath), []);
});

test("ownership claims require a bare contract filename", () => {
  assert.equal(canvasFilenameForClaim("review.html"), "review.html");
  assert.equal(canvasFilenameForClaim(".agents/canvas/review.html"), null);
  assert.equal(canvasFilenameForClaim(join(".agents", "canvas", "review.html")), null);
  assert.equal(canvasFilenameForClaim("../review.html"), null);
  assert.equal(canvasFilenameForClaim("..\\review.html"), null);
});

test("ignores canvas-shaped paths outside the worktree root canvas directory", () => {
  const worktreePath = resolve("worktree");
  const nestedPath = join("fixtures", ".agents", "canvas", "report.html");

  assert.deepEqual(
    canvasFilenamesForTool(
      "apply_patch",
      patch(["*** Begin Patch", `*** Update File: ${nestedPath}`, "*** End Patch"]),
      worktreePath,
    ),
    [],
  );
  assert.deepEqual(
    canvasFilenamesForTool(
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
  const watcher = watchCanvasWrites(session, worktreePath);
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

test("startup-local write observation never attributes successful writes in another worktree", () => {
  const startup = resolve("startup-worktree");
  const other = resolve("other-worktree");
  const session = fakeSession();
  const watcher = watchCanvasWrites(session, startup);
  const writes = [];
  watcher.activate((filename) => writes.push(filename));
  for (const [toolCallId, path] of [
    ["local", join(startup, ".agents", "canvas", "local.html")],
    ["foreign", join(other, ".agents", "canvas", "foreign.html")],
  ]) {
    session.emit("tool.execution_start", { toolCallId, toolName: "edit", arguments: { path } });
    session.emit("tool.execution_complete", { toolCallId, success: true });
  }
  assert.deepEqual(writes, ["local.html"]);
  assert.equal(canvasFilenameForClaim(join(other, ".agents", "canvas", "foreign.html")), null);
  watcher.stop();
  session.emit("tool.execution_start", {
    toolCallId: "stopped", toolName: "create", arguments: { path: ".agents/canvas/stopped.html" },
  });
  session.emit("tool.execution_complete", { toolCallId: "stopped", success: true });
  assert.deepEqual(writes, ["local.html"]);
});
