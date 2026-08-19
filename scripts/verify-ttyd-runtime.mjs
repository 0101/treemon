import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright";
import { createTerminalJobSupervisor } from "./durable-terminal-host.mjs";

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

export async function cleanupRuntimeResources({ supervisor, browser }) {
  let browserError;
  let processError;
  try {
    if (browser) await browser.close();
  } catch (error) {
    browserError = error;
  }
  try {
    if (supervisor) await supervisor.terminate(10_000);
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
  let supervisor;
  let browser;

  try {
    assert(existsSync(ttyd), `Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
    mkdirSync(fixture, { recursive: true });
    const port = await freePort();
    assert(port !== 5000, "Runtime check selected production port 5000");

    const script = "Set-Location -LiteralPath $env:TREEMON_TERMINAL_WORKTREE";
    const shellArgs = [
      "pwsh",
      "-WorkingDirectory",
      ".",
      "-NoExit",
      "-Command",
      script,
    ];
    assert(
      Buffer.byteLength(shellArgs.join(" "), "utf8") < 256,
      "ttyd child command exceeds the stock 1.7.7 Windows buffer",
    );

    supervisor = createTerminalJobSupervisor();
    const sessionId = randomUUID();
    const ownership = await supervisor.start({
      sessionId,
      generation: "ttyd-runtime-verification",
      worktreePath: fixture,
      witnessPath: join(fixture, `${sessionId}.empty.json`),
      witnessNonce: randomUUID().replaceAll("-", ""),
      fileName: ttyd,
      argumentsList: [
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
      workingDirectory: fixture,
      environment: {
        TREEMON_TERMINAL_WORKTREE: fixture,
      },
      timeoutMs: 10_000,
    });
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
    assert(!supervisor.exited, "Terminal Job Object supervisor exited early");
    console.log(`PASS: stock ttyd ${ownership.ttydPid} accepted input in ${fixture}`);
  } finally {
    let cleanupError;
    try {
      await cleanupRuntimeResources({ supervisor, browser });
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
