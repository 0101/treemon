import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve, sep } from "node:path";
import { isValidCanvasFilename } from "./canvas-filename.mjs";
import {
  canvasFilenameForClaim,
  watchCanvasWrites,
} from "./canvas-ownership.mjs";
import { isTrustedInjectionHeaders } from "./injection-request.mjs";
import {
  promptForCanvasMessage,
  promptForSession,
} from "./session-prompt.mjs";
import { createSendQueue } from "./send-queue.mjs";

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
const CANVAS_SEND_SCRIPT =
  `<script>${readFileSync(new URL("./canvas-send.js", import.meta.url), "utf8")}</script>`;
const CANVAS_SELECTION_CONTEXT_SCRIPT =
  `<script>${readFileSync(new URL("./canvas-selection-context.js", import.meta.url), "utf8")}</script>`;
const SYSTEM_VIEW_FILENAMES = new Set(
  JSON.parse(readFileSync(new URL("./canvas-doc-kinds.json", import.meta.url), "utf8"))
    .map((filename) => filename.toLowerCase()),
);

const TRANSPORT_SHIM = `<script>
if (window.parent === window) {
  window.__canvasTopLevelTransportAvailable = true;
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

const { enqueue: enqueueSend } = createSendQueue({ log });

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

function injectScripts(html, port, filename) {
  const shim = TRANSPORT_SHIM.replaceAll("__PORT__", String(port));
  const agentDocScripts =
    SYSTEM_VIEW_FILENAMES.has(filename.toLowerCase())
      ? ""
      : "\n" + CANVAS_SEND_SCRIPT + "\n" + CANVAS_SELECTION_CONTEXT_SCRIPT;
  const scripts = shim + agentDocScripts + "\n" + CONTENT_POLL_SCRIPT;
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

function serverPort(server) {
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("canvas bridge did not bind a TCP port");
  }
  return address.port;
}

// Guard the local injection endpoints (/inject, /_message) against cross-origin abuse. Requiring
// application/json turns any cross-origin browser POST into a preflighted (non-simple) request that
// this server never answers, so the browser blocks it and the text/plain simple-request bypass is
// closed; rejecting a present, non-loopback Origin is defense-in-depth. Legitimate callers comply:
// Treemon POSTs /inject as application/json with no Origin, and the served-doc shim POSTs /_message
// same-origin as application/json.
function startHttpServer(session, state) {
  return new Promise((resolvePromise, reject) => {
    const server = createServer(async (req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
        if (!isTrustedInjectionHeaders(req.headers)) {
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
        let transport;
        try {
          transport = promptForSession(body);
        } catch (err) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ ok: false, error: err.message }));
          return;
        }
        const { kind, prompt } = transport;
        log(`/inject received: transport length=${body.length}, prompt length=${prompt.length}`);
        enqueueSend(session, kind, prompt);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true }));
        return;
      }

      if (state.browserMode) {
        if (req.method === "POST" && req.url === "/_message") {
          if (!isTrustedInjectionHeaders(req.headers)) {
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
          let transport;
          try {
            transport = promptForCanvasMessage(body);
          } catch (err) {
            res.writeHead(400, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: false, error: err.message }));
            return;
          }
          enqueueSend(session, transport.kind, transport.prompt);
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
              const port = serverPort(server);
              res.writeHead(200, {
                "Content-Type": "text/html; charset=utf-8",
                "Content-Security-Policy": "frame-ancestors 'none'",
              });
              res.end(injectScripts(content, port, canvasRoute.filename));
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
      resolvePromise({ server, port: serverPort(server) });
    });
    server.on("error", reject);
  });
}

async function registerWithTreemon(worktreePath, injectUrl, sessionId) {
  try {
    const res = await fetch(TREEMON_REGISTER_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        worktreePath,
        injectUrl,
        sessionId,
      }),
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

// Declare ownership for a successful write. The extension stamps its own sessionId;
// unreachable or unmonitored Treemon remains a best-effort no-op.
async function declareOwnership(worktreePath, filename, sessionId) {
  try {
    const res = await fetch(TREEMON_ATTRIBUTE_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        worktreePath,
        filename,
        sessionId,
      }),
      signal: AbortSignal.timeout(TREEMON_FETCH_TIMEOUT_MS),
    });
    if (!res.ok) {
      log(`ownership declaration failed for ${filename}: ${res.status} ${res.statusText}`);
      return {
        ok: false,
        error: `Treemon returned ${res.status} ${res.statusText}`,
      };
    }
    const outcome = await res.json().catch(() => ({}));
    const attributed = outcome?.attributed === true;
    // `monitored` distinguishes the two non-attributed cases: an unmonitored worktree (nothing
    // recorded) from a SystemView, which has no author to record.
    const monitored = outcome?.monitored === true;
    log(`declared ownership: ${filename} → ${sessionId} (attributed=${attributed})`);
    return { ok: true, attributed, monitored };
  } catch (err) {
    log(`could not declare ownership for ${filename}: ${err.message}`);
    return { ok: false, error: err.message };
  }
}

function startHeartbeat(worktreePath, injectUrl, sessionId) {
  let currentInterval = HEARTBEAT_INTERVAL_MS;
  let wasDisconnected = false;
  /** @type {ReturnType<typeof setTimeout> | null} */
  let timerId = null;

  const scheduleNext = () => {
    timerId = setTimeout(tick, currentInterval);
  };

  const tick = async () => {
    const { reachable } = await registerWithTreemon(worktreePath, injectUrl, sessionId);
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
async function handleCanvasWrite(session, state, filename) {
  if (!isValidCanvasFilename(filename)) {
    log(`canvas write: ignoring unsafe filename ${JSON.stringify(filename)}`);
    return;
  }
  if (!state.browserMode) {
    if (state.sessionId) {
      await declareOwnership(state.worktreePath, filename, state.sessionId);
    } else {
      log(`canvas write: sessionId not ready, skipping ownership declaration for ${filename}`);
    }
    return;
  }

  const url = `http://127.0.0.1:${state.port}/canvas/${encodeURIComponent(filename)}`;
  log(`canvas write: serving ${filename} in browser mode → ${url}`);
  enqueueSend(
    session,
    "agent-prompt",
    `Canvas doc "${filename}" is served in browser-fallback mode at ${url} — Treemon is not monitoring this worktree. Share this ctrl+clickable URL with the user (or open it) to view the doc; it auto-reloads on changes and interactions are forwarded back to this session.`,
  );
}

const worktreePath = process.cwd();
/**
 * @type {{
 *   browserMode: boolean,
 *   port: number,
 *   sessionId: string | undefined,
 *   worktreePath: string
 * }}
 */
const extensionState = { browserMode: false, port: 0, sessionId: undefined, worktreePath };

// Explicit routing tool the agent can call on demand. AgentDocs assign author ownership;
// SystemViews are not claimable - their interactions resolve to a live session. It stamps THIS session's id,
// so the agent only supplies the filename.
const takeOwnershipTool = {
  name: "canvas_take_ownership",
  description:
    "Route an authored canvas doc's replies to THIS session by claiming its author ownership. Pass the filename under .agents/canvas/, e.g. \"review.html\". SystemViews such as diff.html and beads.html are not claimable: they always reach the worktree's most recently active session.",
  parameters: {
    type: "object",
    properties: {
      filename: {
        type: "string",
        description:
          "The bare canvas doc filename under .agents/canvas/ (e.g. \"review.html\"). Paths and directory separators are rejected.",
      },
    },
    required: ["filename"],
  },
  skipPermission: true,
  handler: async ({ filename }) => {
    const name = canvasFilenameForClaim(filename);
    if (name === null) {
      throw new Error(`Not a valid canvas filename: ${JSON.stringify(filename)} (expected e.g. "review.html").`);
    }
    if (!extensionState.sessionId) {
      throw new Error("This session has no id yet; cannot declare ownership.");
    }
    const result =
      await declareOwnership(extensionState.worktreePath, name, extensionState.sessionId);
    if (!result.ok) {
      throw new Error(`Ownership declaration failed: ${result.error}`);
    }
    if (!result.attributed) {
      if (result.monitored) {
        throw new Error(
          `"${name}" is a generated SystemView, so it has no author to claim. Its interactions always reach the worktree's most recently active session.`,
        );
      }
      throw new Error(`Treemon is not monitoring this worktree, so ownership was not recorded for ${name}.`);
    }
    return `Replies from "${name}" now route to this session.`;
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
// The pinned SDK currently exposes `sessionId`; keep the older `id` compatibility boundary
// locally so each independently installed extension remains self-contained.
const sessionWithLegacyId =
  /** @type {{ sessionId?: unknown, id?: unknown }} */ (session);
const rawSessionId = sessionWithLegacyId.sessionId ?? sessionWithLegacyId.id;
const sessionId =
  typeof rawSessionId === "string"
    ? rawSessionId.trim() || undefined
    : undefined;
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
canvasWrites.activate((write) => handleCanvasWrite(session, extensionState, write));

if (browserMode) {
  const reason = !registered.reachable ? "Treemon unreachable" : "directory not monitored by Treemon";
  log(`● canvas-bridge listening in BROWSER mode on port ${port} (${reason})`);
} else {
  log(`● canvas-bridge listening on ${injectUrl}`);
}

const stopHeartbeat =
  browserMode
    ? () => {}
    : startHeartbeat(worktreePath, injectUrl, sessionId);

const cleanup = () => {
  canvasWrites.stop();
  stopHeartbeat();
  server.close();
};
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);
