import { basename, dirname, relative, resolve } from "node:path";

const CANVAS_FILENAME_RE = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.html$/;
const PATCH_FILE_HEADER_RE =
  /^\*\*\* (Add File|Update File|Move to):[ \t]*(.+?)[ \t]*\r?$/gm;

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

function uniqueLatestFilenames(filenames) {
  return filenames.filter((filename, index) => !filenames.slice(index + 1).includes(filename));
}

function patchCanvasFilenames(worktreePath, toolArgs) {
  const headers = [...patchText(toolArgs).matchAll(PATCH_FILE_HEADER_RE)]
    .map(([, operation, filePath]) => ({ operation, filePath }));

  return uniqueLatestFilenames(
    headers.flatMap((header, index) => {
      const next = headers[index + 1];
      if (header.operation === "Move to") return [];
      if (header.operation === "Update File" && next?.operation === "Move to") {
        return [canvasFilename(worktreePath, next.filePath)];
      }
      return [canvasFilename(worktreePath, header.filePath)];
    })
    .filter(Boolean),
  );
}

function directCanvasFilenames(worktreePath, toolArgs) {
  const args = parseToolArgs(toolArgs);
  return [canvasFilename(worktreePath, args?.path ?? args?.file_path)].filter(Boolean);
}

export function isValidCanvasFilename(filename) {
  return typeof filename === "string" && CANVAS_FILENAME_RE.test(filename);
}

export function canvasFilenamesForTool(toolName, toolArgs, worktreePath = process.cwd()) {
  return (
    toolName === "apply_patch"
      ? patchCanvasFilenames(worktreePath, toolArgs)
      : toolName === "create" || toolName === "edit"
        ? directCanvasFilenames(worktreePath, toolArgs)
        : []
  );
}

export function watchCanvasWrites(session, worktreePath = process.cwd()) {
  const pendingByToolCallId = new Map();
  const bufferedWrites = [];
  let onCanvasWrite = (write) => bufferedWrites.push(write);

  const unsubscribeStart = session.on("tool.execution_start", (event) => {
    const data = event?.data;
    if (!data) return;
    const filenames = canvasFilenamesForTool(data.toolName, data.arguments, worktreePath);
    if (filenames.length > 0) pendingByToolCallId.set(data.toolCallId, filenames);
  });

  const unsubscribeComplete = session.on("tool.execution_complete", (event) => {
    const data = event?.data;
    if (!data) return;
    const filenames = pendingByToolCallId.get(data.toolCallId);
    if (filenames === undefined) return;
    pendingByToolCallId.delete(data.toolCallId);
    if (!data.success) return;
    filenames.forEach(onCanvasWrite);
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
