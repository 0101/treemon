import { execFileSync, spawn } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { createServer as createNetServer } from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const repo = resolve(import.meta.dirname, "..");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const serverExecutable = join(
  repo,
  "src",
  "Server",
  "bin",
  "Debug",
  "net10.0",
  "Treemon.exe",
);
const viteExecutable = join(repo, "node_modules", "vite", "bin", "vite.js");
const fixture = join(tmpdir(), `treemon-durable-host-e2e-${Date.now()}`);
const worktree = join(fixture, "worktree");
const configDirectory = join(fixture, "config");
const stateDirectory = join(repo, ".agents", "durable-terminal-verification");
const statePath = join(stateDirectory, "host.json");
const diagnosticsPath = join(stateDirectory, "diagnostics.jsonl");
const evidencePath = join(stateDirectory, "evidence.json");
const markers = {
  initial: `TREEMON_INITIAL_${Date.now()}`,
  browserReload: `TREEMON_BROWSER_RELOAD_${Date.now()}`,
  serverRestart: `TREEMON_SERVER_RESTART_${Date.now()}`,
};
let apiPort;
let canvasPort;
let vitePort;
let server;
let restartedServer;
let vite;
let browser;
let page;
let hostState;
let terminalSession;

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function processIsAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;

  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    throw error;
  }
}

async function waitFor(description, predicate, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await delay(100);
  }

  throw new Error(`Timed out waiting for ${description}`);
}

async function freePort() {
  const listener = createNetServer();
  listener.listen(0, "127.0.0.1");
  await new Promise((resolveListening) => listener.once("listening", resolveListening));
  const { port } = listener.address();
  await new Promise((resolveClose, rejectClose) =>
    listener.close((error) => (error ? rejectClose(error) : resolveClose())),
  );
  return port;
}

async function waitForHttp(url) {
  return waitFor(`${url} to respond`, async () => {
    try {
      await fetch(url, { signal: AbortSignal.timeout(1000) });
      return true;
    } catch {
      return false;
    }
  });
}

function runGit(argumentsList) {
  execFileSync("git", argumentsList, {
    cwd: worktree,
    stdio: "ignore",
    windowsHide: true,
  });
}

function createFixtureRepository() {
  mkdirSync(worktree, { recursive: true });
  writeFileSync(join(worktree, "README.md"), "# Durable terminal fixture\n", "utf8");
  runGit(["init", "-b", "main"]);
  runGit(["config", "user.email", "durable-terminal@example.invalid"]);
  runGit(["config", "user.name", "Durable Terminal Verification"]);
  runGit(["add", "README.md"]);
  runGit(["commit", "-m", "Initialize verification fixture"]);
}

function startServer() {
  return spawn(
    serverExecutable,
    [
      worktree,
      "--port",
      String(apiPort),
      "--canvas-port",
      String(canvasPort),
    ],
    {
      cwd: repo,
      windowsHide: true,
      stdio: "ignore",
      env: {
        ...process.env,
        TREEMON_CONFIG_DIR: configDirectory,
        TREEMON_TERMINAL_STATE_DIR: stateDirectory,
      },
    },
  );
}

function startVite() {
  return spawn(
    process.execPath,
    [viteExecutable, "--port", String(vitePort), "--strictPort"],
    {
      cwd: repo,
      windowsHide: true,
      stdio: "ignore",
      env: {
        ...process.env,
        API_PORT: String(apiPort),
        CANVAS_PORT: String(canvasPort),
        VITE_PORT: String(vitePort),
      },
    },
  );
}

async function stopProcess(child, description) {
  if (!child || child.exitCode !== null) return;
  const pid = child.pid;
  child.kill();
  await waitFor(
    `${description} PID ${pid} to exit`,
    () => child.exitCode !== null || !processIsAlive(pid),
    15_000,
  );
}

function readHostState() {
  if (!existsSync(statePath)) return null;
  return JSON.parse(readFileSync(statePath, "utf8"));
}

async function control(path, method = "GET") {
  const response = await fetch(
    `http://127.0.0.1:${hostState.controlPort}${path}`,
    {
      method,
      headers: { authorization: `Bearer ${hostState.controlToken}` },
      signal: AbortSignal.timeout(10_000),
    },
  );
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Control ${method} ${path} failed with HTTP ${response.status}: ${text}`);
  }
  return text ? JSON.parse(text) : {};
}

async function currentTerminalSession() {
  if (!hostState) return null;
  const response = await control("/sessions");
  return response.sessions[0] ?? null;
}

async function terminalFrame() {
  const iframe = await page.waitForSelector("iframe.terminal-iframe", {
    state: "attached",
    timeout: 60_000,
  });
  const frame = await iframe.contentFrame();
  if (!frame) throw new Error("Terminal iframe had no content frame");
  await frame.waitForFunction(
    () => Boolean(window.term && document.querySelector(".xterm-helper-textarea")),
    null,
    { timeout: 30_000 },
  );
  return { iframe, frame };
}

async function proveTerminal(marker, expectedPid) {
  const expectedOutput = `${marker}:${expectedPid}`;
  const { iframe, frame } = await terminalFrame();
  const source = await iframe.getAttribute("src");

  await frame.evaluate(
    ({ encodedMarker }) => {
      window.term.paste(
        `Write-Output (([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encodedMarker}'))) + ':' + $PID)`,
      );
      window.term.input("\r", true);
    },
    { encodedMarker: Buffer.from(marker, "utf8").toString("base64") },
  );
  await frame.waitForFunction(
    (expectedText) => {
      const buffer = window.term.buffer.active;
      return Array.from(
        { length: buffer.length },
        (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
      )
        .join("\n")
        .includes(expectedText);
    },
    expectedOutput,
    { timeout: 30_000 },
  );
  return source;
}

async function shutdownHost() {
  if (!hostState || !processIsAlive(hostState.pid)) return;

  try {
    await control("/shutdown", "POST");
    await waitFor(
      `durable host PID ${hostState.pid} to exit`,
      () => !processIsAlive(hostState.pid),
      15_000,
    );
  } catch {
    process.stderr.write(
      `Graceful durable host shutdown failed; stopping recorded host PID ${hostState.pid}.\n`,
    );
    process.kill(hostState.pid);
  }
}

function cleanupRuntimeFiles() {
  [
    `session-activity-${apiPort}.db`,
    `session-activity-${apiPort}.db-shm`,
    `session-activity-${apiPort}.db-wal`,
    `merged-prs-${apiPort}.json`,
    `auto-sync-${apiPort}.json`,
  ].forEach((filename) => rmSync(join(repo, "data", filename), { force: true }));
}

try {
  assert(existsSync(ttyd), `Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
  rmSync(stateDirectory, { recursive: true, force: true });
  mkdirSync(stateDirectory, { recursive: true });
  mkdirSync(configDirectory, { recursive: true });
  createFixtureRepository();

  apiPort = await freePort();
  canvasPort = await freePort();
  vitePort = await freePort();
  assert(![apiPort, canvasPort, vitePort].includes(5000), "Verification selected production port 5000");

  execFileSync(
    "dotnet",
    ["fable", "src/Client", "--outDir", "src/Client/output", "--noCache"],
    { cwd: repo, stdio: "ignore", windowsHide: true },
  );
  execFileSync(
    "dotnet",
    ["build", "src/Server/Server.fsproj", "--nologo", "--verbosity:quiet"],
    { cwd: repo, stdio: "ignore", windowsHide: true },
  );

  server = startServer();
  await waitForHttp(`http://localhost:${apiPort}/`);
  vite = startVite();
  await waitForHttp(`http://localhost:${vitePort}/`);

  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();
  await page.goto(`http://localhost:${vitePort}/`);
  await page.waitForSelector(".wt-card", { timeout: 60_000 });
  await page.click('button[title="Open embedded terminal"]');

  hostState = await waitFor("durable host state", () => readHostState());
  terminalSession = await waitFor(
    "durable ttyd and PowerShell identities",
    async () => {
      const candidate = await currentTerminalSession();
      return candidate?.lifecycle === "running" &&
        candidate.ttydPid &&
        candidate.shellPid
        ? candidate
        : null;
    },
  );
  const terminalUrl = await proveTerminal(
    markers.initial,
    terminalSession.shellPid,
  );

  await page.reload();
  const reloadedTerminalUrl = await proveTerminal(
    markers.browserReload,
    terminalSession.shellPid,
  );
  const afterBrowserReload = await currentTerminalSession();

  await stopProcess(server, "first Treemon server");
  assert(processIsAlive(hostState.pid), "Durable host exited with the first Treemon server");
  assert(processIsAlive(terminalSession.ttydPid), "ttyd exited with the first Treemon server");
  assert(processIsAlive(terminalSession.shellPid), "PowerShell exited with the first Treemon server");

  restartedServer = startServer();
  await waitForHttp(`http://localhost:${apiPort}/`);
  await page.reload();
  const restartedTerminalUrl = await proveTerminal(
    markers.serverRestart,
    terminalSession.shellPid,
  );
  const afterServerRestart = await currentTerminalSession();

  await page.click('button[title="Close embedded terminal"]');
  await waitFor("terminal session to close", async () => {
    const response = await control("/sessions");
    return response.sessions.length === 0;
  });
  await waitFor(
    `PowerShell PID ${terminalSession.shellPid} to exit`,
    () => !processIsAlive(terminalSession.shellPid),
  );
  await waitFor(
    `ttyd PID ${terminalSession.ttydPid} to exit`,
    () => !processIsAlive(terminalSession.ttydPid),
  );

  const diagnostics = readFileSync(diagnosticsPath, "utf8");
  const diagnosticEvents = diagnostics
    .trim()
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => JSON.parse(line));
  const connectedPids = diagnosticEvents
    .filter((event) => event.kind === "treemon-connected")
    .map((event) => event.treemonPid);

  assert(
    Object.values(markers).every((marker) => !diagnostics.includes(marker)),
    "Durable host diagnostics captured terminal output",
  );
  assert(
    connectedPids.includes(server.pid) && connectedPids.includes(restartedServer.pid),
    "Diagnostics did not record both Treemon server instances",
  );
  assert(
    diagnosticEvents.some((event) => event.kind === "browser-detached"),
    "Diagnostics did not record browser detachment",
  );

  const evidence = {
    verifiedAt: new Date().toISOString(),
    apiUrl: `http://localhost:${apiPort}`,
    canvasUrl: `http://127.0.0.1:${canvasPort}`,
    viteUrl: `http://localhost:${vitePort}`,
    terminalUrl: new URL(terminalUrl).origin,
    firstTreemonPid: server.pid,
    restartedTreemonPid: restartedServer.pid,
    vitePid: vite.pid,
    durableHostPid: hostState.pid,
    terminalSessionId: terminalSession.id,
    ttydPid: terminalSession.ttydPid,
    powershellPid: terminalSession.shellPid,
    browserReloadPreservedEndpoint: terminalUrl === reloadedTerminalUrl,
    browserReloadPreservedPowerShell:
      afterBrowserReload.shellPid === terminalSession.shellPid,
    serverRestartRediscoveredEndpoint:
      terminalUrl === restartedTerminalUrl,
    serverRestartPreservedPowerShell:
      afterServerRestart.shellPid === terminalSession.shellPid,
    explicitCloseStoppedTtyd: !processIsAlive(terminalSession.ttydPid),
    explicitCloseStoppedPowerShell: !processIsAlive(terminalSession.shellPid),
    diagnosticsMetadataOnly: true,
    diagnosticsBytes: Buffer.byteLength(diagnostics),
  };
  writeFileSync(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
  process.stdout.write(`${JSON.stringify(evidence, null, 2)}\n`);
} finally {
  if (
    terminalSession &&
    hostState &&
    processIsAlive(hostState.pid)
  ) {
    try {
      await control(`/sessions/${encodeURIComponent(terminalSession.id)}`, "DELETE");
    } catch (error) {
      process.stderr.write(`Terminal cleanup failed: ${error.message}\n`);
    }
  }

  if (browser) await browser.close();
  await stopProcess(server, "first Treemon server");
  await stopProcess(restartedServer, "restarted Treemon server");
  await stopProcess(vite, "Vite");
  await shutdownHost();
  cleanupRuntimeFiles();
  rmSync(fixture, { recursive: true, force: true });
}
