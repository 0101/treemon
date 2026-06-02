import { joinSession } from "@github/copilot-sdk/extension";
import { createServer } from "node:http";

const TREEMON_PORT = process.env.TREEMON_PORT || "5000";
const TREEMON_REGISTER_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/register`;

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
    } else {
      log(`registered ${worktreePath} → ${injectUrl}`);
    }
  } catch (err) {
    log(`could not reach Treemon: ${err.message}`);
  }
}

const session = await joinSession({});

const worktreePath = process.cwd();
const { server, port } = await startInjectServer(session);
const injectUrl = `http://127.0.0.1:${port}/inject`;
await registerWithTreemon(worktreePath, injectUrl);
log(`● canvas-bridge listening on ${injectUrl}`);

process.on("SIGTERM", () => server.close());
process.on("SIGINT", () => server.close());
