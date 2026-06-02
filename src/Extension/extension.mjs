import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";

const TREEMON_PORT = process.env.TREEMON_PORT || "5000";
const TREEMON_REGISTER_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/register`;
const HEARTBEAT_INTERVAL_MS = 30000;
const HEARTBEAT_MAX_INTERVAL_MS = 120000;

const log = (msg) => console.error(`[canvas-bridge] ${msg}`);

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

function startInjectServer(session) {
  return new Promise((resolve, reject) => {
    const server = createServer(async (req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
        const body = await readBody(req);
        log(`/inject received: payload length=${body.length}`);
        enqueueSend(session, `[canvas] ${body}`);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true }));
      } else {
        res.writeHead(404);
        res.end("Not Found");
      }
    });

    server.listen(0, "127.0.0.1", () => {
      resolve({ server, port: server.address().port });
    });
    server.on("error", reject);
  });
}

async function registerWithTreemon(worktreePath, injectUrl) {
  try {
    const res = await fetch(TREEMON_REGISTER_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ worktreePath, injectUrl }),
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

function startHeartbeat(worktreePath, injectUrl) {
  let currentInterval = HEARTBEAT_INTERVAL_MS;
  let wasDisconnected = false;
  let timerId = null;

  const scheduleNext = () => {
    timerId = setTimeout(tick, currentInterval);
  };

  const tick = async () => {
    const ok = await registerWithTreemon(worktreePath, injectUrl);
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

const worktreePath = process.cwd();
const { server, port } = await startInjectServer(session);
const injectUrl = `http://127.0.0.1:${port}/inject`;
await registerWithTreemon(worktreePath, injectUrl);
log(`● canvas-bridge listening on ${injectUrl}`);

const stopHeartbeat = startHeartbeat(worktreePath, injectUrl);

const cleanup = () => {
  stopHeartbeat();
  server.close();
};
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);
