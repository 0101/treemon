import { randomUUID } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright";

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
      if (response.ok) return;
    } catch {}
    await delay(100);
  }
  throw new Error("Timed out waiting for the isolated terminal endpoint");
}

async function waitForProcessExit(closed, timeoutMs) {
  return await new Promise((resolveExit) => {
    const timeout = setTimeout(() => resolveExit(false), timeoutMs);
    closed.then(() => {
      clearTimeout(timeout);
      resolveExit(true);
    });
  });
}

async function waitForManifest(stateDirectory, child, processError) {
  const path = join(stateDirectory, "host.json");
  const deadline = Date.now() + 10_000;

  while (Date.now() < deadline) {
    if (existsSync(path)) {
      try {
        return JSON.parse(readFileSync(path, "utf8"));
      } catch {}
    }
    if (processError.value) {
      throw processError.value;
    }
    if (child.exitCode !== null) {
      throw new Error(`TerminalHost exited with code ${child.exitCode}`);
    }
    await delay(50);
  }

  throw new Error("Timed out waiting for the isolated TerminalHost manifest");
}

async function launchTerminalHost(executable, stateDirectory, worktreePath, port) {
  const child = spawn(
    executable,
    [
      "--port",
      String(port),
      "--state-dir",
      stateDirectory,
      "--ttyd",
      ttyd,
      "--shell",
      "pwsh",
    ],
    {
      cwd: worktreePath,
      windowsHide: true,
      stdio: ["ignore", "ignore", "pipe"],
    },
  );
  const closed = new Promise((resolveClosed) => child.once("close", resolveClosed));
  const processError = { value: undefined };
  let standardError = "";

  child.once("error", (error) => {
    processError.value = error;
  });
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", (chunk) => {
    standardError = `${standardError}${chunk}`.slice(-4096);
  });

  try {
    const manifest = await waitForManifest(stateDirectory, child, processError);

    return {
      manifest,
      terminate: async (timeoutMs) => {
        let shutdownError;

        try {
          const response = await fetch(
            new URL("/api/v2/shutdown", manifest.endpoint),
            {
              method: "POST",
              headers: { Authorization: "Bearer " + manifest.bearerToken },
            },
          );

          if (!response.ok) {
            shutdownError = new Error(
              `TerminalHost shutdown returned HTTP ${response.status}`,
            );
          }
        } catch (error) {
          shutdownError = error;
        }

        if (!(await waitForProcessExit(closed, timeoutMs))) {
          child.kill();

          if (!(await waitForProcessExit(closed, timeoutMs))) {
            throw new Error("The isolated TerminalHost did not exit");
          }

          shutdownError ??= new Error(
            "The isolated TerminalHost did not stop through its control API",
          );
        }

        if (shutdownError) throw shutdownError;
      },
    };
  } catch (error) {
    child.kill();
    const stopped = await waitForProcessExit(closed, 10_000);
    const detail = standardError.trim();
    const cleanupDetail = stopped ? "" : "; the isolated TerminalHost did not exit";
    throw new Error(
      detail
        ? `Could not start the isolated TerminalHost: ${error.message}; ${detail}${cleanupDetail}`
        : `Could not start the isolated TerminalHost: ${error.message}${cleanupDetail}`,
    );
  }
}

export async function cleanupRuntimeResources({ host, browser }) {
  let browserError;
  let hostError;
  try {
    if (browser) await browser.close();
  } catch (error) {
    browserError = error;
  }
  try {
    if (host) await host.terminate(10_000);
  } catch (error) {
    hostError = error;
  }

  if (browserError && hostError) {
    throw new Error(
      `Browser cleanup failed: ${browserError.message}; host cleanup failed: ${hostError.message}`,
    );
  }
  if (browserError) throw browserError;
  if (hostError) throw hostError;
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
  let host;
  let browser;

  try {
    assert(process.platform === "win32", "The pinned ttyd runtime requires Windows");
    assert(existsSync(ttyd), `Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
    mkdirSync(fixture, { recursive: true });
    const initialized = spawnSync("git", ["-C", fixture, "init", "--quiet"], {
      encoding: "utf8",
      windowsHide: true,
    });
    assert(
      initialized.status === 0,
      `Could not initialize the isolated worktree: ${initialized.stderr}`,
    );

    const hostExecutable = [
      join(repo, ".agents", "ci-publish", "terminal-host", "TerminalHost.exe"),
      join(
        repo,
        "src",
        "TerminalHost",
        "bin",
        "Release",
        "net10.0",
        "TerminalHost.exe",
      ),
      join(
        repo,
        "src",
        "TerminalHost",
        "bin",
        "Debug",
        "net10.0",
        "TerminalHost.exe",
      ),
    ].find(existsSync);
    assert(
      hostExecutable,
      "Missing TerminalHost.exe. Run 'dotnet build treemon.slnx --configuration Release'.",
    );

    const controlPort = await freePort();
    assert(controlPort !== 5000, "Runtime check selected production port 5000");
    const stateDirectory = join(fixture, "terminal-host-state");
    host = await launchTerminalHost(
      hostExecutable,
      stateDirectory,
      fixture,
      controlPort,
    );
    assert(
      new URL(host.manifest.endpoint).port !== "5000",
      "TerminalHost bound production port 5000",
    );

    const response = await fetch(
      new URL("/api/v2/terminals", host.manifest.endpoint),
      {
        method: "POST",
        headers: {
          Authorization: "Bearer " + host.manifest.bearerToken,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ worktreePath: fixture }),
      },
    );
    if (!response.ok) {
      throw new Error(
        `TerminalHost start returned HTTP ${response.status}: ${await response.text()}`,
      );
    }
    const snapshot = await response.json();
    assert(snapshot.terminals.length === 1, "TerminalHost did not start one terminal");
    const terminal = snapshot.terminals[0];
    assert(
      new URL(terminal.attachmentEndpoint).port !== "5000",
      "ttyd bound production port 5000",
    );

    await waitForUrl(terminal.attachmentEndpoint);

    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage();
    await page.goto(terminal.attachmentEndpoint);
    await page.waitForFunction(
      () => Boolean(window.term && document.querySelector(".xterm-helper-textarea")),
    );
    await page.evaluate(
      ({ encodedMarker }) => {
        window.term.paste(
          `1..120 | ForEach-Object { Write-Output ('scroll-line-' + $_) }; $pwd.Path; Write-Output ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encodedMarker}')))`,
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

    const viewport = await page.evaluate(() => {
      const element = document.querySelector(".xterm-viewport");
      const style = getComputedStyle(element);
      const maximumScrollTop = element.scrollHeight - element.clientHeight;
      element.scrollTop = 0;

      return {
        scrollbarWidth: style.scrollbarWidth,
        overflowY: style.overflowY,
        maximumScrollTop,
        scrollTop: element.scrollTop,
      };
    });
    assert(
      viewport.scrollbarWidth === "none",
      `xterm viewport scrollbar remained visible: ${viewport.scrollbarWidth}`,
    );
    assert(
      viewport.overflowY === "scroll" || viewport.overflowY === "auto",
      `xterm viewport stopped being scrollable: ${viewport.overflowY}`,
    );
    assert(
      viewport.maximumScrollTop > 0,
      "xterm viewport had no scrollback after writing 120 lines",
    );
    assert(
      viewport.scrollTop === 0,
      `xterm viewport did not accept scrolling: ${viewport.scrollTop}`,
    );
    await page.locator(".xterm-screen").hover();
    await page.mouse.wheel(0, 600);
    await page.waitForFunction(
      () => document.querySelector(".xterm-viewport").scrollTop > 0,
    );
    console.log(
      `PASS: stock ttyd accepted input with hidden scrollbar and working scrollback through TerminalHost session ${terminal.sessionId} in ${fixture}`,
    );
  } catch (error) {
    const bearerToken = host?.manifest?.bearerToken;
    const message = error instanceof Error ? error.message : String(error);

    throw new Error(
      bearerToken ? message.replaceAll(bearerToken, "[redacted]") : message,
    );
  } finally {
    let cleanupError;
    try {
      await cleanupRuntimeResources({ host, browser });
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
