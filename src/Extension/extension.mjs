import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve, sep } from "node:path";
import {
  createOwnershipDeclarer,
  isValidCanvasFilename,
  watchCanvasWrites,
} from "./canvas-ownership.mjs";

const TREEMON_PORT = process.env.TREEMON_PORT || "5000";
const TREEMON_REGISTER_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/register`;
const TREEMON_ATTRIBUTE_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/attribute`;
const HEARTBEAT_INTERVAL_MS = 30000;
const HEARTBEAT_MAX_INTERVAL_MS = 120000;
// Bound every Treemon fetch so a TCP-alive-but-unresponsive server can't stall the caller
// (undici's default headersTimeout is ~5min). These calls are best-effort; the catch blocks
// swallow the resulting AbortError, so a timeout degrades exactly like an unreachable Treemon.
const TREEMON_FETCH_TIMEOUT_MS = 5000;

const log = (msg) => console.error(`[canvas-bridge] ${msg}`);

const TRANSPORT_SHIM = `<script>
if (window.parent === window) {
  window.addEventListener('message', function(e) {
    if (e.source === window && e.data && typeof e.data.action === 'string') {
      fetch('http://127.0.0.1:__PORT__/_message', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(e.data)
      });
    }
  });
}
</script>`;

const CONTENT_POLL_SCRIPT = `<script>
(function() {
  var lastHash = null;
  setInterval(function() {
    fetch(location.href + '/hash').then(r => r.text()).then(function(hash) {
      if (lastHash && hash !== lastHash) location.reload();
      lastHash = hash;
    }).catch(function() {});
  }, 3000);
})();
</script>`;

const CANVAS_DIR = resolve(process.cwd(), ".agents", "canvas");

let sendQueue = Promise.resolve();
const enqueueSend = (session, prompt) => {
  sendQueue = sendQueue
    .then(() => session.send({ prompt }))
    .then(() => log(`session.send succeeded (${prompt.length} chars)`))
    .catch((err) => log(`session.send FAILED: ${err?.message ?? err}`));
};

function readBody(req, maxBytes = 1024 * 1024) {
  return new Promise((resolve, reject) => {
    let body = "";
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > maxBytes) { req.destroy(); reject(new Error("body too large")); return; }
      body += chunk;
    });
    req.on("end", () => resolve(body));
  });
}

async function readCanvasFile(filename) {
  const filePath = resolve(CANVAS_DIR, filename);
  if (!filePath.startsWith(CANVAS_DIR + sep) && filePath !== CANVAS_DIR) {
    throw Object.assign(new Error("path traversal blocked"), { code: "EACCES" });
  }
  return readFile(filePath, "utf-8");
}

function hashContent(content) {
  return createHash("sha256").update(content, "utf-8").digest("hex");
}

function injectScripts(html, port) {
  const shim = TRANSPORT_SHIM.replaceAll("__PORT__", String(port));
  const scripts = shim + "\n" + CONTENT_POLL_SCRIPT;
  if (html.includes("</head>")) {
    return html.replace("</head>", scripts + "\n</head>");
  }
  return scripts + "\n" + html;
}

function parseCanvasRoute(url) {
  const match = url.match(/^\/canvas\/([^/]+)(\/hash)?$/);
  if (!match) return null;
  return { filename: decodeURIComponent(match[1]), isHash: !!match[2] };
}

// Guard the local injection endpoints (/inject, /_message) against cross-origin abuse. Requiring
// application/json turns any cross-origin browser POST into a preflighted (non-simple) request that
// this server never answers, so the browser blocks it and the text/plain simple-request bypass is
// closed; rejecting a present, non-loopback Origin is defense-in-depth. Legitimate callers comply:
// Treemon POSTs /inject as application/json with no Origin, and the served-doc shim POSTs /_message
// same-origin as application/json.
function isLoopbackOrigin(origin) {
  return /^https?:\/\/(127(?:\.\d{1,3}){3}|localhost|\[::1\])(?::\d+)?$/i.test(origin);
}

function isTrustedInjectionRequest(req) {
  const contentType = String(req.headers["content-type"] || "").split(";")[0].trim().toLowerCase();
  if (contentType !== "application/json") return false;
  const origin = req.headers["origin"];
  if (origin && !isLoopbackOrigin(origin)) return false;
  return true;
}

function startHttpServer(session, state) {
  return new Promise((resolvePromise, reject) => {
    const server = createServer(async (req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
        if (!isTrustedInjectionRequest(req)) {
          log(`/inject rejected: untrusted request (content-type=${req.headers["content-type"] ?? ""}, origin=${req.headers["origin"] ?? ""})`);
          res.writeHead(403, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ ok: false, error: "forbidden" }));
          return;
        }
        let body;
        try { body = await readBody(req); } catch {
          res.writeHead(413, { "Content-Type": "text/plain" });
          res.end("Payload Too Large");
          return;
        }
        log(`/inject received: payload length=${body.length}`);
        enqueueSend(session, `[canvas] ${body}`);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true }));
        return;
      }

      if (state.browserMode) {
        if (req.method === "POST" && req.url === "/_message") {
          if (!isTrustedInjectionRequest(req)) {
            log(`/_message rejected: untrusted request (content-type=${req.headers["content-type"] ?? ""}, origin=${req.headers["origin"] ?? ""})`);
            res.writeHead(403, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: false, error: "forbidden" }));
            return;
          }
          let body;
          try { body = await readBody(req); } catch {
            res.writeHead(413, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: false, error: "payload too large" }));
            return;
          }
          log(`/_message received: payload length=${body.length}`);
          let parsed;
          try { parsed = JSON.parse(body); } catch {
            res.writeHead(400, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: false, error: "invalid JSON" }));
            return;
          }
          if (typeof parsed?.action !== "string") {
            res.writeHead(400, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: false, error: "missing action" }));
            return;
          }
          enqueueSend(session, `[canvas] ${JSON.stringify(parsed)}`);
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ ok: true }));
          return;
        }

        const canvasRoute = parseCanvasRoute(req.url);
        if (req.method === "GET" && canvasRoute) {
          if (!isValidCanvasFilename(canvasRoute.filename)) {
            res.writeHead(400, { "Content-Type": "text/plain" });
            res.end("Bad Request: invalid filename");
            return;
          }
          try {
            const content = await readCanvasFile(canvasRoute.filename);
            if (canvasRoute.isHash) {
              res.writeHead(200, { "Content-Type": "text/plain" });
              res.end(hashContent(content));
            } else {
              const port = server.address().port;
              res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
              res.end(injectScripts(content, port));
            }
          } catch (err) {
            if (err.code === "ENOENT") {
              res.writeHead(404, { "Content-Type": "text/plain" });
              res.end("Not Found");
            } else {
              log(`canvas read error: ${err.message}`);
              res.writeHead(500, { "Content-Type": "text/plain" });
              res.end("Internal Server Error");
            }
          }
          return;
        }
      }

      res.writeHead(404);
      res.end("Not Found");
    });

    server.listen(0, "127.0.0.1", () => {
      resolvePromise({ server, port: server.address().port });
    });
    server.on("error", reject);
  });
}

async function registerWithTreemon(worktreePath, injectUrl, sessionId) {
  try {
    const res = await fetch(TREEMON_REGISTER_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ worktreePath, injectUrl, sessionId }),
      signal: AbortSignal.timeout(TREEMON_FETCH_TIMEOUT_MS),
    });
    if (!res.ok) {
      log(`registration failed: ${res.status} ${res.statusText}`);
      return { reachable: false, monitored: false };
    }
    let monitored = true;
    try {
      const data = await res.json();
      if (typeof data?.monitored === "boolean") monitored = data.monitored;
    } catch {
      // older Treemon returns a non-JSON body — assume monitored to preserve prior behavior
    }
    log(`registered ${worktreePath} → ${injectUrl} (monitored=${monitored})`);
    return { reachable: true, monitored };
  } catch (err) {
    log(`could not reach Treemon: ${err.message}`);
    return { reachable: false, monitored: false };
  }
}

// Apply a versioned ownership change for an automatic write, or an unversioned explicit claim.
// The extension stamps its own sessionId; Treemon rejects delayed automatic changes older than the
// current owner. Unmonitored worktrees are completed no-ops, while retryable failures are retained.
async function changeOwnership(worktreePath, change, sessionId) {
  try {
    const res = await fetch(TREEMON_ATTRIBUTE_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        worktreePath,
        filename: change.filename,
        sessionId,
        remove: change.kind === "remove",
        ...(change.version === undefined ? {} : { version: change.version }),
      }),
      signal: AbortSignal.timeout(TREEMON_FETCH_TIMEOUT_MS),
    });
    if (!res.ok) {
      log(`ownership change failed for ${change.filename}: ${res.status} ${res.statusText}`);
      return {
        ok: false,
        retryable: res.status === 408 || res.status === 429 || res.status >= 500,
        error: `Treemon returned ${res.status} ${res.statusText}`,
      };
    }
    const outcome = await res.json().catch(() => ({}));
    const attributed = outcome?.attributed === true;
    log(`ownership change: ${change.kind} ${change.filename} → ${sessionId} (attributed=${attributed})`);
    return { ok: true, attributed };
  } catch (err) {
    log(`could not change ownership for ${change.filename}: ${err.message}`);
    return { ok: false, retryable: true, error: err.message };
  }
}

function startHeartbeat(worktreePath, injectUrl, sessionId, replayOwnership) {
  let currentInterval = HEARTBEAT_INTERVAL_MS;
  let wasDisconnected = false;
  let timerId = null;

  const scheduleNext = () => {
    timerId = setTimeout(tick, currentInterval);
  };

  const tick = async () => {
    const registration = await registerWithTreemon(worktreePath, injectUrl, sessionId);
    if (registration.reachable && registration.monitored) {
      await replayOwnership();
    }
    const { reachable } = registration;
    if (reachable) {
      if (wasDisconnected) {
        log("Bridge reconnected to Treemon");
        wasDisconnected = false;
      }
      currentInterval = HEARTBEAT_INTERVAL_MS;
    } else {
      wasDisconnected = true;
      currentInterval = Math.min(currentInterval * 2, HEARTBEAT_MAX_INTERVAL_MS);
      log(`heartbeat failed, retrying in ${currentInterval / 1000}s`);
    }
    scheduleNext();
  };

  scheduleNext();

  return () => {
    if (timerId != null) {
      clearTimeout(timerId);
      timerId = null;
    }
  };
}

// React to a successful canvas-doc write. Monitored: declare ownership (the authoritative
// attribution path; the server's file-watcher is fallback-only) — the extension stamps in its own
// sessionId, the agent only supplied the filename. Browser mode (Treemon unreachable/unmonitored):
// serve the doc locally and hand the session a clickable URL via session.send (events cannot inject
// tool-result context the way the old onPostToolUse hook did).
async function handleCanvasWrite(session, state, write, declareOwner) {
  const { filename } = write;
  if (!isValidCanvasFilename(filename)) {
    log(`canvas write: ignoring unsafe filename ${JSON.stringify(filename)}`);
    return;
  }
  if (!state.browserMode) {
    if (state.sessionId) {
      await declareOwner(write);
    } else {
      log(`canvas write: sessionId not ready, skipping ownership declaration for ${filename}`);
    }
    return;
  }

  if (write.kind === "remove") return;

  const url = `http://127.0.0.1:${state.port}/canvas/${encodeURIComponent(filename)}`;
  log(`canvas write: serving ${filename} in browser mode → ${url}`);
  enqueueSend(
    session,
    `Canvas doc "${filename}" is served in browser-fallback mode at ${url} — Treemon is not monitoring this worktree. Share this ctrl+clickable URL with the user (or open it) to view the doc; it auto-reloads on changes and interactions are forwarded back to this session.`,
  );
}

const worktreePath = process.cwd();
const extensionState = { browserMode: false, port: 0, sessionId: null, worktreePath };
const automaticOwnership = createOwnershipDeclarer((write) =>
  changeOwnership(
    extensionState.worktreePath,
    write,
    extensionState.sessionId,
  ),
);

// Explicit ownership tool the agent can call on demand — for a doc produced by a script or
// another tool (no supported write event fired to auto-declare), or one whose messages are reaching
// the wrong session. It stamps THIS session's id, so the agent only supplies the filename.
const takeOwnershipTool = {
  name: "canvas_take_ownership",
  description:
    "Declare THIS session as the owner of a canvas doc under .agents/canvas/, so replies from that doc route back to this session. Use it when a canvas doc was produced by a script or unsupported tool, or when a doc's messages are reaching the wrong session. Pass the doc's filename, e.g. \"review.html\".",
  parameters: {
    type: "object",
    properties: {
      filename: {
        type: "string",
        description:
          "The canvas doc's filename under .agents/canvas/ (e.g. \"review.html\"). A full path is accepted; only the filename is used.",
      },
    },
    required: ["filename"],
  },
  skipPermission: true,
  handler: async ({ filename }) => {
    const name = String(filename ?? "").split(/[\\/]/).pop();
    if (!isValidCanvasFilename(name)) {
      throw new Error(`Not a valid canvas filename: ${JSON.stringify(filename)} (expected e.g. "review.html").`);
    }
    if (!extensionState.sessionId) {
      throw new Error("This session has no id yet; cannot declare ownership.");
    }
    const result =
      await changeOwnership(
        extensionState.worktreePath,
        { kind: "attribute", filename: name },
        extensionState.sessionId,
      );
    if (!result.ok) {
      throw new Error(`Ownership declaration failed: ${result.error}`);
    }
    if (!result.attributed) {
      throw new Error(`Treemon is not monitoring this worktree, so ownership was not recorded for ${name}.`);
    }
    return `This session now owns "${name}" — replies from that canvas doc will route here.`;
  },
};

// No hooks: the native runtime rejects SDK hook callbacks on resume. Canvas writes are observed via
// session events (watchCanvasWrites), subscribed immediately below so startup writes aren't missed.
// Tools are registered at join; if a resumed session rejects tool registration (experimental API),
// fall back to a plain join so the extension still loads — ownership auto-declaration is unaffected.
let session;
try {
  session = await joinSession({ tools: [takeOwnershipTool] });
} catch (err) {
  log(`joinSession with tools failed (${err?.message ?? err}); retrying without tools`);
  session = await joinSession();
}
// Read the session id defensively: the current native runtime populates `session.sessionId`,
// but older/other SDK shapes expose it as `session.id`. The `@github/copilot-sdk` dependency is
// floating ("*") with no runtime/version guard, so keep both. Dropping the `session.id` fallback
// yields `undefined` on an id-only runtime -> anonymous registration + skipped declareOwnership
// (see the `if (state.sessionId)` guard in handleCanvasWrite) -> unowned docs whose canvas replies
// are queued rather than delivered (the server's single-session fallback is intentionally gone).
const sessionId = session.sessionId ?? session.id;
extensionState.sessionId = sessionId;
const canvasWrites = watchCanvasWrites(session, worktreePath);

const { server, port } = await startHttpServer(session, extensionState);
extensionState.port = port;
const injectUrl = `http://127.0.0.1:${port}/inject`;
const registered = await registerWithTreemon(worktreePath, injectUrl, sessionId);
const browserMode = !registered.reachable || !registered.monitored;
extensionState.browserMode = browserMode;
Object.freeze(extensionState);

// State is frozen and valid; start handling canvas writes (flushing any buffered during startup).
canvasWrites.activate((write) =>
  handleCanvasWrite(session, extensionState, write, automaticOwnership.declare),
);

if (browserMode) {
  const reason = !registered.reachable ? "Treemon unreachable" : "directory not monitored by Treemon";
  log(`● canvas-bridge listening in BROWSER mode on port ${port} (${reason})`);
} else {
  log(`● canvas-bridge listening on ${injectUrl}`);
}

const stopHeartbeat =
  browserMode
    ? () => {}
    : startHeartbeat(worktreePath, injectUrl, sessionId, automaticOwnership.replay);

const cleanup = () => {
  canvasWrites.stop();
  stopHeartbeat();
  server.close();
};
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);
