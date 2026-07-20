import { performance } from "node:perf_hooks";
import { basename, dirname, relative, resolve } from "node:path";

const CANVAS_FILENAME_RE = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.html$/;
const PATCH_FILE_HEADER_RE =
  /^\*\*\* (Add File|Update File|Delete File|Move to):[ \t]*(.+?)[ \t]*\r?$/gm;

function parseToolArgs(toolArgs) {
  if (typeof toolArgs === "string") {
    try { return JSON.parse(toolArgs); } catch { return {}; }
  }
  return toolArgs ?? {};
}

function canvasFilename(worktreePath, filePath) {
  const resolvedPath = resolve(worktreePath, String(filePath ?? ""));
  const canvasPath = relative(resolve(worktreePath, ".agents", "canvas"), resolvedPath);
  if (dirname(canvasPath) !== ".") return null;
  const filename = basename(resolvedPath);
  return isValidCanvasFilename(filename) ? filename : null;
}

function patchText(toolArgs) {
  if (typeof toolArgs !== "string") {
    const value = toolArgs?.patch ?? toolArgs?.input;
    return typeof value === "string" ? value : "";
  }
  try {
    const parsed = JSON.parse(toolArgs);
    const value = typeof parsed === "string" ? parsed : parsed?.patch ?? parsed?.input;
    return typeof value === "string" ? value : "";
  } catch {
    return toolArgs;
  }
}

function canvasChange(kind, worktreePath, filePath) {
  const filename = canvasFilename(worktreePath, filePath);
  return filename ? { kind, filename } : null;
}

function uniqueLatestChanges(changes) {
  return changes.filter((change, index) =>
    !changes.slice(index + 1).some((candidate) => candidate.filename === change.filename));
}

function patchCanvasChanges(worktreePath, toolArgs) {
  const headers = [...patchText(toolArgs).matchAll(PATCH_FILE_HEADER_RE)]
    .map(([, operation, filePath]) => ({ operation, filePath }));

  return uniqueLatestChanges(
    headers.flatMap((header, index) => {
      const next = headers[index + 1];
      if (header.operation === "Move to") return [];
      if (header.operation === "Delete File") {
        return [canvasChange("remove", worktreePath, header.filePath)];
      }
      if (header.operation === "Update File" && next?.operation === "Move to") {
        return [
          canvasChange("remove", worktreePath, header.filePath),
          canvasChange("attribute", worktreePath, next.filePath),
        ];
      }
      return [canvasChange("attribute", worktreePath, header.filePath)];
    })
    .filter(Boolean),
  );
}

function directCanvasChanges(worktreePath, toolArgs) {
  const args = parseToolArgs(toolArgs);
  return [canvasChange("attribute", worktreePath, args?.path ?? args?.file_path)].filter(Boolean);
}

export function isValidCanvasFilename(filename) {
  return typeof filename === "string" && CANVAS_FILENAME_RE.test(filename);
}

export function canvasChangesForTool(toolName, toolArgs, worktreePath = process.cwd()) {
  return (
    toolName === "apply_patch"
      ? patchCanvasChanges(worktreePath, toolArgs)
      : toolName === "create" || toolName === "edit"
        ? directCanvasChanges(worktreePath, toolArgs)
        : []
  );
}

export function currentOwnershipVersion() {
  return Math.round((performance.timeOrigin + performance.now()) * 1000);
}

export function watchCanvasWrites(
  session,
  worktreePath = process.cwd(),
  ownershipVersion = currentOwnershipVersion,
) {
  const pendingByToolCallId = new Map();
  const bufferedWrites = [];
  let onCanvasWrite = (write) => bufferedWrites.push(write);

  const unsubscribeStart = session.on("tool.execution_start", (event) => {
    const data = event?.data;
    if (!data) return;
    const changes = canvasChangesForTool(data.toolName, data.arguments, worktreePath);
    if (changes.length > 0) pendingByToolCallId.set(data.toolCallId, changes);
  });

  const unsubscribeComplete = session.on("tool.execution_complete", (event) => {
    const data = event?.data;
    if (!data) return;
    const changes = pendingByToolCallId.get(data.toolCallId);
    if (changes === undefined) return;
    pendingByToolCallId.delete(data.toolCallId);
    if (!data.success) return;
    const version = ownershipVersion();
    changes.map((change) => ({ ...change, version })).forEach(onCanvasWrite);
  });

  const stop = () => {
    unsubscribeStart();
    unsubscribeComplete();
  };

  const activate = (handler) => {
    onCanvasWrite = handler;
    bufferedWrites.splice(0).forEach(handler);
  };

  return { stop, activate };
}

export function createOwnershipDeclarer(declareOwnership) {
  const pending = new Map();

  const declare = async (write) => {
    const result = await declareOwnership(write);
    if (result.ok || !result.retryable) pending.delete(write.filename);
    else pending.set(write.filename, write);
    return result;
  };

  const replay = async () => {
    await Promise.all([...pending.values()].map(declare));
  };

  return { declare, replay };
}
