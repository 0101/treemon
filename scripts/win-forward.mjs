#!/usr/bin/env node
// Forwards a Windows loopback port to Treemon running inside WSL.
//
// Two things make this necessary together. WSL's own localhost forwarding is not working on this
// setup, so a Windows browser can only reach the VM by its address - and Treemon's CSRF guard
// rejects any state-changing request whose Origin is not loopback, because the remoting surface has
// no auth and `createWorktree` launches a coding agent. Browsing http://<vm ip>:5010 therefore
// renders a dashboard whose every button answers 403.
//
// Listening on Windows loopback makes the browser's Origin `http://localhost:<port>` again, which
// the guard accepts, without widening what the guard allows. The companion scripts/wsl-forward.py
// runs inside WSL and publishes the server's 127.0.0.1 port on the VM's interface; this is the
// Windows end of that chain.
//
//   node scripts/win-forward.mjs [localPort] [remotePort] [wslHost]
//
// Defaults: 5010 -> <wsl ip>:5010. The WSL address is discovered with `wsl.exe hostname -I` when not
// given, because it changes across restarts.

import net from "node:net";
import { execFileSync } from "node:child_process";

const [localPortArg, remotePortArg, hostArg] = process.argv.slice(2);

const localPort = Number(localPortArg ?? 5010);
const remotePort = Number(remotePortArg ?? 5010);

const wslAddress = () => {
  // `hostname -I` lists every address; the first is the one Windows routes to.
  const out = execFileSync("wsl.exe", ["hostname", "-I"], { encoding: "utf8" });
  const first = out.trim().split(/\s+/)[0];
  if (!first) throw new Error("wsl.exe hostname -I returned no address");
  return first;
};

const host = hostArg ?? wslAddress();

const server = net.createServer((client) => {
  const upstream = net.connect(remotePort, host);

  // A relay has two sockets and either can fail; tearing down both on any error keeps a half-open
  // pair from leaking for the life of the process.
  const close = () => {
    client.destroy();
    upstream.destroy();
  };

  client.on("error", close);
  upstream.on("error", close);
  client.pipe(upstream);
  upstream.pipe(client);
});

server.on("error", (err) => {
  console.error(`relay failed: ${err.message}`);
  process.exit(1);
});

// Loopback only. Binding the wildcard would publish a dashboard that launches coding agents to the
// whole network - and would defeat the point, since the guard trusts loopback origins.
server.listen(localPort, "127.0.0.1", () => {
  console.log(`http://localhost:${localPort}  ->  ${host}:${remotePort}`);
});
