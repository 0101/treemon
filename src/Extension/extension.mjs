import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve, sep } from "node:path";

const TREEMON_PORT = process.env.TREEMON_PORT || "5000";
const TREEMON_REGISTER_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/register`;
const HEARTBEAT_INTERVAL_MS = 30000;
const HEARTBEAT_MAX_INTERVAL_MS = 120000;

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

function isValidCanvasFilename(filename) {
  return typeof filename === "string" && /^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.html$/.test(filename);
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

function startHttpServer(session, state) {
  return new Promise((resolvePromise, reject) => {
    const server = createServer(async (req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
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

function startHeartbeat(worktreePath, injectUrl, sessionId) {
  let currentInterval = HEARTBEAT_INTERVAL_MS;
  let wasDisconnected = false;
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

function createCanvasHook(state) {
  return async ({ toolName, toolArgs, toolResult }) => {
    if (!state.browserMode) return {};

    const isCreateOrEdit = toolName === "create" || toolName === "edit";
    if (!isCreateOrEdit) return {};

    const filePath = toolArgs?.path || toolArgs?.file_path || "";
    const normalized = filePath.replace(/\\/g, "/");
    if (!/\/.agents\/canvas\/[^/]+\.html$/.test(normalized) && !/^\.agents\/canvas\/[^/]+\.html$/.test(normalized)) return {};

    if (toolResult?.resultType === "failure") return {};

    const filename = normalized.split("/").pop();
    const url = `http://127.0.0.1:${state.port}/canvas/${encodeURIComponent(filename)}`;

    return {
      additionalContext: `Canvas file served at: ${url}\nOpen this URL in a browser to view the canvas doc. The page auto-reloads on changes and postMessage interactions are forwarded back to this session.`,
    };
  };
}

const extensionState = { browserMode: false, port: 0 };
const session = await joinSession({ hooks: { onPostToolUse: createCanvasHook(extensionState) } });
const sessionId = session.id ?? session.sessionId;

const worktreePath = process.cwd();
const { server, port } = await startHttpServer(session, extensionState);
extensionState.port = port;
const injectUrl = `http://127.0.0.1:${port}/inject`;
const registered = await registerWithTreemon(worktreePath, injectUrl, sessionId);
const browserMode = !registered.reachable || !registered.monitored;
extensionState.browserMode = browserMode;
Object.freeze(extensionState);

if (browserMode) {
  const reason = !registered.reachable ? "Treemon unreachable" : "directory not monitored by Treemon";
  log(`● canvas-bridge listening in BROWSER mode on port ${port} (${reason})`);
} else {
  log(`● canvas-bridge listening on ${injectUrl}`);
}

const stopHeartbeat = browserMode ? () => {} : startHeartbeat(worktreePath, injectUrl, sessionId);

const cleanup = () => {
  stopHeartbeat();
  server.close();
};
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);
