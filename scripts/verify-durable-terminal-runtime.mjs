import { spawn } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const repo = resolve(import.meta.dirname, "..");
const hostScript = join(repo, "scripts", "durable-terminal-host.mjs");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const fixture = join(tmpdir(), `treemon-durable-terminal-${Date.now()}`);
const stateDirectory = join(fixture, "state");
const worktree = join(fixture, "worktree");
const statePath = join(stateDirectory, "host.json");
const firstMarker = `TREEMON_RECONNECT_A_${Date.now()}`;
const secondMarker = `TREEMON_RECONNECT_B_${Date.now()}`;
let host;
let hostState;
let session;
let browser;

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

async function waitFor(description, predicate, timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await delay(100);
  }

  throw new Error(`Timed out waiting for ${description}`);
}

async function control(path, method = "GET", body) {
  const response = await fetch(
    `http://127.0.0.1:${hostState.controlPort}${path}`,
    {
      method,
      headers: {
        authorization: `Bearer ${hostState.controlToken}`,
        ...(body ? { "content-type": "application/json" } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(method === "POST" ? 30_000 : 10_000),
    },
  );
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Control ${method} ${path} failed with HTTP ${response.status}: ${text}`);
  }
  return text ? JSON.parse(text) : {};
}

async function currentSession() {
  const response = await control("/sessions");
  return response.sessions.find((candidate) => candidate.id === session?.id);
}

function terminalText() {
  const buffer = window.term.buffer.active;
  return Array.from(
    { length: buffer.length },
    (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
  ).join("\n");
}

async function attachAndRun(marker, expectedPid) {
  const expectedOutput = `${marker}:${expectedPid}`;
  const page = await browser.newPage();
  await page.goto(session.endpoint);
  await page.waitForFunction(
    () => Boolean(window.term && document.querySelector(".xterm-helper-textarea")),
  );
  await page.evaluate(
    ({ encodedMarker }) => {
      window.term.paste(
        `Write-Output (([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encodedMarker}'))) + ':' + $PID)`,
      );
      window.term.input("\r", true);
    },
    { encodedMarker: Buffer.from(marker, "utf8").toString("base64") },
  );
  await page.waitForFunction(
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
  );
  return page;
}

async function stopHost() {
  if (!hostState) return;

  try {
    await control("/shutdown", "POST");
  } catch {
    if (host?.exitCode === null) host.kill();
  }

  await waitFor(
    "durable host to exit",
    () => host?.exitCode !== null || !processIsAlive(hostState.pid),
    10_000,
  );
}

try {
  assert(existsSync(ttyd), `Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
  mkdirSync(stateDirectory, { recursive: true });
  mkdirSync(worktree, { recursive: true });

  host = spawn(
    process.execPath,
    [
      hostScript,
      "--state-dir",
      stateDirectory,
      "--ttyd",
      ttyd,
      "--shell",
      "pwsh",
    ],
    {
      windowsHide: true,
      stdio: "ignore",
    },
  );

  hostState = await waitFor("durable host state", () => {
    if (!existsSync(statePath)) return null;
    return JSON.parse(readFileSync(statePath, "utf8"));
  });
  assert(hostState.pid === host.pid, "Host state did not identify the spawned host");
  assert(hostState.controlPort !== 5000, "Host selected production port 5000");

  const started = await control("/sessions", "POST", { worktreePath: worktree });
  assert(started.sessions.length === 1, "Expected one durable terminal session");
  session = started.sessions[0];
  assert(session.lifecycle === "running", `Terminal did not start: ${session.error}`);
  assert(new URL(session.endpoint).port !== "5000", "Terminal selected production port 5000");

  session = await waitFor("ttyd and PowerShell process identities", async () => {
    const current = await currentSession();
    return current?.ttydPid && current?.shellPid ? current : null;
  });

  browser = await chromium.launch({ headless: true });
  const firstPage = await attachAndRun(firstMarker, session.shellPid);
  const firstText = await firstPage.evaluate(terminalText);
  assert(
    firstText.includes(`${firstMarker}:${session.shellPid}`),
    "Terminal did not report the tracked PowerShell PID",
  );
  await firstPage.close();

  await waitFor("browser detachment", async () => {
    const current = await currentSession();
    return current?.browserAttachments === 0 ? current : null;
  });
  assert(processIsAlive(session.shellPid), "PowerShell exited when the browser detached");
  assert(processIsAlive(session.ttydPid), "ttyd exited when the browser detached");

  const secondPage = await attachAndRun(secondMarker, session.shellPid);
  const secondText = await secondPage.evaluate(terminalText);
  const afterReconnect = await currentSession();
  assert(afterReconnect.shellPid === session.shellPid, "PowerShell PID changed after browser reconnect");
  assert(afterReconnect.ttydPid === session.ttydPid, "ttyd PID changed after browser reconnect");
  assert(
    secondText.includes(`${secondMarker}:${session.shellPid}`),
    "Reconnected terminal did not report the original PowerShell PID",
  );
  await secondPage.close();

  await control(`/sessions/${encodeURIComponent(session.id)}`, "DELETE");
  await waitFor("PowerShell to exit after explicit close", () => !processIsAlive(session.shellPid));
  await waitFor("ttyd to exit after explicit close", () => !processIsAlive(session.ttydPid));

  const diagnosticsPath = join(stateDirectory, "diagnostics.jsonl");
  const diagnostics = readFileSync(diagnosticsPath, "utf8");
  assert(!diagnostics.includes(firstMarker), "Diagnostics captured terminal output");
  assert(!diagnostics.includes(secondMarker), "Diagnostics captured terminal output");
  assert(
    statSync(diagnosticsPath).size <= 1024 * 1024,
    "Diagnostics exceeded the configured bound",
  );

  const evidence = {
    hostPid: hostState.pid,
    controlPort: hostState.controlPort,
    sessionId: session.id,
    ttydPid: session.ttydPid,
    powershellPid: session.shellPid,
    browserReconnectPreservedProcess: true,
    explicitCloseStoppedTtyd: true,
    explicitCloseStoppedPowerShell: true,
    diagnosticsMetadataOnly: true,
    diagnosticsBytes: statSync(diagnosticsPath).size,
  };
  process.stdout.write(`${JSON.stringify(evidence, null, 2)}\n`);
} finally {
  if (browser) await browser.close();

  if (session && hostState && processIsAlive(hostState.pid)) {
    try {
      await control(`/sessions/${encodeURIComponent(session.id)}`, "DELETE");
    } catch (error) {
      process.stderr.write(`Terminal cleanup failed: ${error.message}\n`);
    }
  }

  if (hostState && processIsAlive(hostState.pid)) await stopHost();
  rmSync(fixture, { recursive: true, force: true });
}
