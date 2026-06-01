import http from "node:http";

const TREEMON_PORT = process.env.TREEMON_PORT || "5000";
const TREEMON_REGISTER_URL = `http://127.0.0.1:${TREEMON_PORT}/api/canvas/register`;

function startInjectServer(session) {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      if (req.method === "POST" && req.url === "/inject") {
        let body = "";
        req.on("data", (chunk) => { body += chunk; });
        req.on("end", () => {
          try {
            session.send(`[canvas] ${body}`);
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
          } catch (err) {
            res.writeHead(500, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ error: err.message }));
          }
        });
      } else {
        res.writeHead(404);
        res.end("Not Found");
      }
    });

    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      resolve({ server, port });
    });

    server.on("error", reject);
  });
}

async function registerWithTreemon(worktreePath, injectUrl) {
  const payload = JSON.stringify({ worktreePath, injectUrl });
  try {
    const res = await fetch(TREEMON_REGISTER_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: payload,
    });
    if (!res.ok) {
      console.error(`[canvas-bridge] Registration failed: ${res.status} ${res.statusText}`);
    } else {
      console.error(`[canvas-bridge] Registered ${worktreePath} → ${injectUrl}`);
    }
  } catch (err) {
    console.error(`[canvas-bridge] Could not reach Treemon: ${err.message}`);
  }
}

export default function activate(session) {
  const worktreePath = process.cwd();

  startInjectServer(session).then(async ({ server, port }) => {
    const injectUrl = `http://127.0.0.1:${port}/inject`;
    await registerWithTreemon(worktreePath, injectUrl);

    process.on("exit", () => server.close());
  });
}
