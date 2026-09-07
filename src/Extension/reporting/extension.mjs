import { joinSession } from "@github/copilot-sdk/extension";
import { randomUUID } from "node:crypto";
import { createReportingRuntime } from "./reporting-runtime.mjs";

// treemon-reporting is passive: it registers no tools or canvas, never sends a prompt, and only
// forwards a closed set of SDK lifecycle facts to Treemon.

const PROVIDER = "copilot_cli";
const TREEMON_FETCH_TIMEOUT_MS = 5000;

const portsRaw = process.env.TREEMON_PORTS || process.env.TREEMON_PORT || "5000";
const activityUrls = portsRaw
  .split(",")
  .map((port) => port.trim())
  .filter(Boolean)
  .map((port) => `http://127.0.0.1:${port}/api/session/activity`);

const log = (message) => console.error(`[treemon-reporting] ${message}`);

let lastErrorLogMs = 0;
function logErrorThrottled(message) {
  const now = Date.now();
  if (now - lastErrorLogMs > 60000) {
    lastErrorLogMs = now;
    log(message);
  }
}

async function postActivityReport(url, report) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(report),
    signal: AbortSignal.timeout(TREEMON_FETCH_TIMEOUT_MS),
  });

  let body;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }

  return {
    ok: response.ok,
    status: response.status,
    statusText: response.statusText,
    body,
  };
}

let session;
try {
  session = await joinSession();
} catch (error) {
  log(`joinSession failed: ${error?.message ?? error}`);
  process.exit(0);
}

const sessionWithLegacyId =
  /** @type {{ sessionId?: unknown, id?: unknown }} */ (session);
const rawSessionId = sessionWithLegacyId.sessionId ?? sessionWithLegacyId.id;
const sessionId = typeof rawSessionId === "string" ? rawSessionId.trim() : "";
if (!sessionId) {
  log("no session id (session.sessionId/session.id both absent) — reporting disabled for this session");
  process.exit(0);
}

const parentProcessId = process.ppid;
if (!Number.isSafeInteger(parentProcessId) || parentProcessId <= 0) {
  log("no parent Copilot process id — reporting disabled for this session");
  process.exit(0);
}

const terminalSessionId =
  process.env.TREEMON_TERMINAL_SESSION_ID?.trim() || undefined;
const runtime = createReportingRuntime({
  session,
  baseContext: {
    parentProcessId,
    sessionId,
    ...(terminalSessionId ? { terminalSessionId } : {}),
    worktreePath: process.cwd(),
    provider: PROVIDER,
  },
  activityUrls,
  post: postActivityReport,
  randomId: randomUUID,
  log: logErrorThrottled,
});

const cleanup = () => runtime.stop();
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);

log(
  `startup pid=${process.pid} parentPid=${parentProcessId} terminalOrigin=${terminalSessionId ? "present" : "absent"} endpoints=${activityUrls.length}`,
);
await runtime.start();
log(`joined ${sessionId} — reporting to ${activityUrls.join(", ")}`);
