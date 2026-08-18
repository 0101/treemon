import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright";
import {
  defaultProcessController,
  sameProcessIdentity,
  terminateRetainedChild,
} from "./durable-terminal-host.mjs";

const repo = resolve(import.meta.dirname, "..");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const marker = "TREEMON_TTYD_RUNTIME_OK";

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

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
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${url}`);
}

export async function waitForIdentity(
  pid,
  processController,
  { timeoutMs = 5000, wait = delay, now = () => Date.now() } = {},
) {
  const deadline = now() + timeoutMs;
  const capture = async () => {
    const identity = await processController.inspect(pid);
    if (identity) return identity;
    if (now() >= deadline) {
      throw new Error(`Timed out capturing process identity for PID ${pid}`);
    }
    await wait(25);
    return capture();
  };
  return capture();
}

export async function cleanupOwnedTree(
  rootIdentity,
  processController,
  { timeoutMs = 5000, wait = delay, now = () => Date.now() } = {},
) {
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

  const deadline = now() + timeoutMs;
  const stop = async () => {
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
    if (now() >= deadline) {
      throw new Error("ttyd verification cleanup left an owned process running");
    }

    for (const identity of remaining) {
      await processController.terminate(identity);
    }
    await wait(50);
    return stop();
  };

  await stop();
}

export async function cleanupRuntimeResources(
  { child, childIdentity, browser },
  processController,
  terminateChild = terminateRetainedChild,
) {
  let browserError;
  let processError;
  try {
    if (browser) await browser.close();
  } catch (error) {
    browserError = error;
  }
  try {
    if (childIdentity) {
      await cleanupOwnedTree(childIdentity, processController);
    } else if (child) {
      await terminateChild(child);
    }
  } catch (error) {
    processError = error;
  }

  if (browserError && processError) {
    throw new Error(
      `Browser cleanup failed: ${browserError.message}; process cleanup failed: ${processError.message}`,
    );
  }
  if (browserError) throw browserError;
  if (processError) throw processError;
}

function terminalText() {
  const buffer = window.term.buffer.active;
  return Array.from(
    { length: buffer.length },
    (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
  ).join("\n");
}

export async function runTtydRuntimeVerification() {
  const fixture = join(
    repo,
    ".agents",
    "ttyd-runtime-verification",
    randomUUID(),
  );
  const processController = defaultProcessController();
  let child;
  let childIdentity;
  let browser;

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
    childIdentity = await waitForIdentity(child.pid, processController);

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
    let cleanupError;
    try {
      await cleanupRuntimeResources(
        { child, childIdentity, browser },
        processController,
      );
    } catch (error) {
      cleanupError = error;
    }
    rmSync(fixture, { recursive: true, force: true });
    if (cleanupError) throw cleanupError;
  }
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href
) {
  await runTtydRuntimeVerification();
}
