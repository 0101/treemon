import { chromium } from "playwright";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import {
  defaultProcessController,
  sameProcessIdentity,
} from "./durable-terminal-host.mjs";

const repo = resolve(import.meta.dirname, "..");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const fixture = join(tmpdir(), `treemon-ttyd-runtime-'${Date.now()}`);
const marker = "TREEMON_TTYD_RUNTIME_OK";
let child;
let childIdentity;
let browser;
const processController = defaultProcessController();

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function freePort() {
  return await new Promise((resolvePort, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

async function waitForUrl(url) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.status < 500) return;
    } catch {}
    await new Promise((resolveWait) => setTimeout(resolveWait, 100));
  }

  async function waitForIdentity(pid) {
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      const identity = await processController.inspect(pid);
      if (identity) return identity;
      await new Promise((resolveWait) => setTimeout(resolveWait, 25));
    }
    throw new Error(`Timed out capturing process identity for PID ${pid}`);
  }

  async function cleanupOwnedTree(rootIdentity) {
    const tracked = new Map([
      [`${rootIdentity.pid}:${rootIdentity.startIdentity}`, rootIdentity],
    ]);

    const discover = async () => {
      const before = tracked.size;
      const pending = [...tracked.values()];
      for (const parent of pending) {
        const actual = await processController.inspect(parent.pid);
        if (!sameProcessIdentity(actual, parent)) continue;
        const children = await processController.children(parent.pid);
        for (const candidate of children) {
          const verified = await processController.inspect(candidate.pid);
          if (sameProcessIdentity(candidate, verified)) {
            tracked.set(
              `${candidate.pid}:${candidate.startIdentity}`,
              candidate,
            );
          }
        }
      }
      if (tracked.size > before) await discover();
    };

    await discover();
    if (browser) await browser.close();
    browser = null;

    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
      await discover();
      const remaining = (
        await Promise.all(
          [...tracked.values()].map(async (identity) => ({
            identity,
            actual: await processController.inspect(identity.pid),
          })),
        )
      )
        .filter(({ identity, actual }) =>
          sameProcessIdentity(identity, actual),
        )
        .map(({ identity }) => identity);
      if (remaining.length === 0) return;

      for (const identity of remaining) {
        const actual = await processController.inspect(identity.pid);
        if (sameProcessIdentity(actual, identity)) {
          await processController.terminate(identity.pid);
        }
      }
      await new Promise((resolveWait) => setTimeout(resolveWait, 50));
    }

    throw new Error("ttyd verification cleanup left an owned process running");
  }
  throw new Error(`Timed out waiting for ${url}`);
}

function terminalText() {
  const buffer = window.term.buffer.active;
  return Array.from(
    { length: buffer.length },
    (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
  ).join("\n");
}

try {
  assert(existsSync(ttyd), `Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
  mkdirSync(fixture, { recursive: true });
  const port = await freePort();
  assert(port !== 5000, "Runtime check selected production port 5000");

  const script = "Set-Location -LiteralPath $env:TREEMON_TERMINAL_WORKTREE";
  const encoded = Buffer.from(script, "utf16le").toString("base64");
  const shellArgs = [
    "pwsh",
    "-WorkingDirectory",
    ".",
    "-NoExit",
    "-EncodedCommand",
    encoded,
  ];
  assert(
    Buffer.byteLength(shellArgs.join(" "), "utf8") < 256,
    "ttyd child command exceeds the stock 1.7.7 Windows buffer",
  );

  child = spawn(
    ttyd,
    [
      "-p",
      String(port),
      "-i",
      "127.0.0.1",
      "-W",
      "-O",
      "-w",
      fixture,
      ...shellArgs,
    ],
    {
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
      env: { ...process.env, TREEMON_TERMINAL_WORKTREE: fixture },
    },
  );
  childIdentity = await waitForIdentity(child.pid);

  let output = "";
  child.stdout.on("data", (data) => (output += data.toString()));
  child.stderr.on("data", (data) => (output += data.toString()));
  await waitForUrl(`http://127.0.0.1:${port}/`);

  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${port}/`);
  await page.waitForFunction(
    () => Boolean(window.term && document.querySelector(".xterm-helper-textarea")),
  );
  await page.evaluate(
    ({ encodedMarker }) => {
      window.term.paste(
        `$pwd.Path; Write-Output ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encodedMarker}')))`,
      );
      window.term.input("\r", true);
    },
    { encodedMarker: Buffer.from(marker, "utf8").toString("base64") },
  );

  await page.waitForFunction(
    (expectedMarker) => {
      const buffer = window.term.buffer.active;
      return Array.from(
        { length: buffer.length },
        (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
      )
        .join("\n")
        .includes(expectedMarker);
    },
    marker,
  );
  const text = await page.evaluate(terminalText);
  assert(
    text.includes(basename(fixture)),
    `Terminal cwd was not ${fixture}: ${text}`,
  );
  assert(child.exitCode === null, `ttyd exited early with ${child.exitCode}: ${output}`);
  console.log(`PASS: stock ttyd ${child.pid} accepted input in ${fixture}`);
} finally {
  if (childIdentity) await cleanupOwnedTree(childIdentity);
  else if (browser) await browser.close();
  rmSync(fixture, { recursive: true, force: true });
}
