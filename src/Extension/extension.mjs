import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

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

function readBody(req) {
  return new Promise((resolve) => {
    let body = "";
    req.on("data", (chunk) => { body += chunk; });
    req.on("end", () => resolve(body));
  });
}

function isValidCanvasFilename(filename) {
  return typeof filename === "string" && /^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.html$/.test(filename);
}

async function readCanvasFile(filename) {
  const filePath = resolve(CANVAS_DIR, filename);
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

function startHttpServer(session) {
  return new Promise((resolvePromise, reject) => {
    const server = createServer(async (req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
        const body = await readBody(req);
        log(`/inject received: payload length=${body.length}`);
        enqueueSend(session, `[canvas] ${body}`);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true }));
        return;
      }

      if (server.browserMode) {
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
      return false;
    }
    log(`registered ${worktreePath} → ${injectUrl}`);
    return true;
  } catch (err) {
    log(`could not reach Treemon: ${err.message}`);
    return false;
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
    const ok = await registerWithTreemon(worktreePath, injectUrl, sessionId);
    if (ok) {
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

const session = await joinSession({});
const sessionId = session.id ?? session.sessionId ?? undefined;

const worktreePath = process.cwd();
const { server, port } = await startHttpServer(session);
const injectUrl = `http://127.0.0.1:${port}/inject`;
const registered = await registerWithTreemon(worktreePath, injectUrl, sessionId);
const browserMode = !registered;

if (browserMode) {
  server.browserMode = true;
  log(`● canvas-bridge listening in BROWSER mode on port ${port}`);
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
