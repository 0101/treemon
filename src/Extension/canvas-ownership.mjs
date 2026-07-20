const CANVAS_WRITE_RE = /(^|\/)\.agents\/canvas\/[^/]+\.html$/;
const CANVAS_FILENAME_RE = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.html$/;
const PATCH_FILE_HEADER_RE = /^\*\*\* (?:Add File|Update File|Move to):[ \t]*(.+?)[ \t]*\r?$/gm;

function parseToolArgs(toolArgs) {
  if (typeof toolArgs === "string") {
    try { return JSON.parse(toolArgs); } catch { return {}; }
  }
  return toolArgs ?? {};
}

function canvasFilename(filePath) {
  const normalized = String(filePath ?? "").replace(/\\/g, "/");
  if (!CANVAS_WRITE_RE.test(normalized)) return null;
  const filename = normalized.split("/").pop();
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

function patchCanvasFilenames(toolArgs) {
  return [...patchText(toolArgs).matchAll(PATCH_FILE_HEADER_RE)]
    .map(([, filePath]) => canvasFilename(filePath))
    .filter(Boolean);
}

function directCanvasFilenames(toolArgs) {
  const args = parseToolArgs(toolArgs);
  return [canvasFilename(args?.path ?? args?.file_path)];
}

export function isValidCanvasFilename(filename) {
  return typeof filename === "string" && CANVAS_FILENAME_RE.test(filename);
}

export function canvasFilenamesForTool(toolName, toolArgs) {
  const filenames =
    toolName === "apply_patch"
      ? patchCanvasFilenames(toolArgs)
      : toolName === "create" || toolName === "edit"
        ? directCanvasFilenames(toolArgs)
        : [];
  return [...new Set(filenames.filter(Boolean))];
}

export function watchCanvasWrites(session) {
  const pendingByToolCallId = new Map();
  const bufferedWrites = [];
  let onCanvasWrite = (filename) => bufferedWrites.push(filename);

  const unsubscribeStart = session.on("tool.execution_start", (event) => {
    const data = event?.data;
    if (!data) return;
    const filenames = canvasFilenamesForTool(data.toolName, data.arguments);
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

export function createOwnershipDeclarer(declareOwnership) {
  const pending = new Set();

  const declare = async (filename) => {
    const result = await declareOwnership(filename);
    if (result.ok || !result.retryable) pending.delete(filename);
    else pending.add(filename);
    return result;
  };

  const replay = async () => {
    await Promise.all([...pending].map(declare));
  };

  return { declare, replay };
}

export async function replayOwnershipIfMonitored(registration, replayOwnership) {
  if (registration.reachable && registration.monitored) {
    await replayOwnership();
  }
}
