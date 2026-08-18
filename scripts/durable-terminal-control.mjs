import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

function parseArguments(argumentsList) {
  const command = argumentsList[0] ?? "status";
  const stateDirectoryIndex = argumentsList.indexOf("--state-dir");
  const stateDirectory =
    stateDirectoryIndex >= 0
      ? argumentsList[stateDirectoryIndex + 1]
      : ".agents\\durable-terminal";

  return { command, stateDirectory: resolve(stateDirectory) };
}

function readState(stateDirectory) {
  const path = resolve(stateDirectory, "host.json");
  if (!existsSync(path)) throw new Error(`No durable terminal host state at '${path}'`);
  return JSON.parse(readFileSync(path, "utf8"));
}

async function request(state, path, method = "GET") {
  return fetch(`http://127.0.0.1:${state.controlPort}${path}`, {
    method,
    headers: { authorization: `Bearer ${state.controlToken}` },
    signal: AbortSignal.timeout(method === "POST" ? 30_000 : 5000),
  });
}

async function status(state) {
  const response = await request(state, "/sessions");
  if (!response.ok) throw new Error(`Host status failed with HTTP ${response.status}`);
  const body = await response.json();
  const sessions = body.sessions.map((session) => ({
    id: session.id,
    lifecycle: session.lifecycle,
    ttydPid: session.ttydPid,
    shellPid: session.shellPid,
    publicUrl: session.endpoint ? new URL(session.endpoint).origin : null,
    browserAttachments: session.browserAttachments,
    upstreamOpenedAt: session.upstreamOpenedAt,
    lastPongAt: session.lastPongAt,
  }));

  process.stdout.write(
    `${JSON.stringify({
      generation: state.generation,
      hostPid: state.pid,
      processStartTicks: state.processStartTicks,
      controlPort: state.controlPort,
      sessions,
    }, null, 2)}\n`,
  );
}

async function stop(state, stateDirectory) {
  const response = await request(state, "/shutdown", "POST");
  if (!response.ok) throw new Error(`Host stop failed with HTTP ${response.status}`);

  const deadline = Date.now() + 40_000;
  const statePath = resolve(stateDirectory, "host.json");

  while (Date.now() < deadline && existsSync(statePath)) {
    await delay(100);
  }

  if (existsSync(statePath)) {
    throw new Error(`Host PID ${state.pid} did not stop within 40 seconds`);
  }

  process.stdout.write(`Stopped durable terminal host PID ${state.pid}\n`);
}

const options = parseArguments(process.argv.slice(2));

try {
  const state = readState(options.stateDirectory);
  if (options.command === "status") await status(state);
  else if (options.command === "stop") await stop(state, options.stateDirectory);
  else throw new Error(`Unsupported command '${options.command}'`);
} catch (error) {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
}
