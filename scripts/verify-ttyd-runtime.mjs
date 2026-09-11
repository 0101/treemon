import { randomInt, randomUUID } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
import { createServer as createNetServer } from "node:net";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright";

const repo = resolve(import.meta.dirname, "..");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const marker = "TREEMON_TTYD_RUNTIME_OK";
const terminalVisibleAction = "treemon-terminal-visible";

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function freePort() {
  return await new Promise((resolvePort, reject) => {
    const server = createNetServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

async function listenDashboard(server, remainingAttempts = 20) {
  const port = randomInt(49_152, 65_536);

  try {
    await new Promise((resolveListen, reject) => {
      const onError = (error) => {
        server.off("listening", onListening);
        reject(error);
      };
      const onListening = () => {
        server.off("error", onError);
        resolveListen();
      };
      server.once("error", onError);
      server.once("listening", onListening);
      server.listen(port, "127.0.0.1");
    });
    return port;
  } catch (error) {
    if (
      (error.code === "EADDRINUSE" || error.code === "EACCES") &&
      remainingAttempts > 1
    ) {
      return listenDashboard(server, remainingAttempts - 1);
    }
    throw error;
  }
}

export async function launchDashboardServer() {
  let terminalEndpoint;
  let expectedHost;

  const server = createHttpServer((request, response) => {
    if (request.headers.host !== expectedHost) {
      response.writeHead(400, { "Content-Type": "text/plain" });
      response.end("Invalid Host");
      return;
    }
    if (request.method !== "GET") {
      response.writeHead(405, {
        "Content-Type": "text/plain",
        Allow: "GET",
      });
      response.end("Method Not Allowed");
      return;
    }
    if (request.url !== "/") {
      response.writeHead(404, { "Content-Type": "text/plain" });
      response.end("Not Found");
      return;
    }
    if (!terminalEndpoint) {
      response.writeHead(503, { "Content-Type": "text/plain" });
      response.end("Terminal endpoint is not ready");
      return;
    }

    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    const endpoint = JSON.stringify(terminalEndpoint);
    const action = JSON.stringify(terminalVisibleAction);
    response.end(
      `<!doctype html><html><head><style>html,body,#terminal{width:100%;height:100%;margin:0;border:0}body{overflow:hidden}</style></head><body><iframe id="terminal"></iframe><script>(function(){var endpoint=${endpoint},frame=document.querySelector('#terminal');frame.addEventListener('load',function(){frame.contentWindow.postMessage({action:${action},active:true,loaded:true},new URL(endpoint).origin)});frame.src=endpoint})()</script></body></html>`,
    );
  });

  const port = await listenDashboard(server);
  expectedHost = `127.0.0.1:${port}`;

  return {
    origin: `http://127.0.0.1:${port}`,
    setTerminalEndpoint: (endpoint) => {
      terminalEndpoint = endpoint;
    },
    terminate: () =>
      new Promise((resolveClose, reject) =>
        server.close((error) => (error ? reject(error) : resolveClose())),
      ),
  };
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

async function launchTerminalHost(
  executable,
  stateDirectory,
  worktreePath,
  port,
  allowedOrigin,
) {
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
      "--allowed-origin",
      allowedOrigin,
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
      deleteTerminal: async (sessionId) => {
        const response = await fetch(
          new URL(
            `/api/v2/terminals/${encodeURIComponent(sessionId)}`,
            manifest.endpoint,
          ),
          {
            method: "DELETE",
            headers: { Authorization: "Bearer " + manifest.bearerToken },
          },
        );

        if (!response.ok) {
          throw new Error(
            `TerminalHost terminal DELETE returned HTTP ${response.status}`,
          );
        }

        return await response.json();
      },
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

async function cleanupFailure(description, operation) {
  try {
    await operation();
  } catch (error) {
    return `${description} failed: ${error.message}`;
  }
}

export async function cleanupRuntimeResources({
  host,
  browser,
  dashboard,
  terminalSessionIds = [],
}) {
  const browserFailure = browser
    ? await cleanupFailure("Browser cleanup", () => browser.close())
    : undefined;
  const dashboardFailure = dashboard
    ? await cleanupFailure("Dashboard cleanup", () => dashboard.terminate())
    : undefined;
  const terminalFailures = host
    ? await Promise.all(
        terminalSessionIds.map((sessionId) =>
          cleanupFailure(`Terminal ${sessionId} cleanup`, async () => {
            const snapshot = await host.deleteTerminal(sessionId);
            assert(
              Array.isArray(snapshot.terminals),
              "TerminalHost DELETE returned an invalid registry snapshot",
            );
            assert(
              !snapshot.terminals.some(
                (terminal) => terminal.sessionId === sessionId,
              ),
              `TerminalHost DELETE left terminal ${sessionId} in the registry`,
            );
          }),
        ),
      )
    : [];
  const hostFailure = host
    ? await cleanupFailure("Host cleanup", () => host.terminate(10_000))
    : undefined;
  const failures = [
    browserFailure,
    dashboardFailure,
    ...terminalFailures,
    hostFailure,
  ].filter(Boolean);

  if (failures.length) throw new Error(failures.join("; "));
}

function terminalText() {
  const buffer = window.term.buffer.active;
  return Array.from(
    { length: buffer.length },
    (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
  ).join("\n");
}

async function waitForTerminalText(frame, expectedText) {
  await frame.waitForFunction(
    (expected) => {
      const buffer = window.term?.buffer?.active;
      if (!buffer) return false;
      return Array.from(
        { length: buffer.length },
        (_, index) => buffer.getLine(index)?.translateToString(true) ?? "",
      )
        .join("\n")
        .includes(expected);
    },
    expectedText,
  );
}

async function postTerminalVisible(page, endpoint) {
  await page.evaluate(
    ({ action, origin }) => {
      document
        .querySelector("#terminal")
        .contentWindow.postMessage(
          { action, active: true, loaded: false },
          origin,
        );
    },
    {
      action: terminalVisibleAction,
      origin: new URL(endpoint).origin,
    },
  );
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
  let dashboard;
  let terminalSessionId;

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
    dashboard = await launchDashboardServer();
    const dashboardOrigin = dashboard.origin;
    assert(
      new URL(dashboardOrigin).port !== "5000",
      "Runtime check selected production port 5000",
    );
    const stateDirectory = join(fixture, "terminal-host-state");
    host = await launchTerminalHost(
      hostExecutable,
      stateDirectory,
      fixture,
      controlPort,
      dashboardOrigin,
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
    terminalSessionId = terminal.sessionId;
    assert(
      new URL(terminal.attachmentEndpoint).port !== "5000",
      "ttyd bound production port 5000",
    );
    dashboard.setTerminalEndpoint(terminal.attachmentEndpoint);

    await waitForUrl(terminal.attachmentEndpoint);

    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage();
    const browserDiagnostics = [];
    page.on("console", (message) =>
      browserDiagnostics.push(`console ${message.type()}: ${message.text()}`),
    );
    page.on("requestfailed", (request) =>
      browserDiagnostics.push(
        `request failed at ${new URL(request.url()).origin} (${request.failure()?.errorText ?? "unknown"})`,
      ),
    );
    await page.goto(dashboardOrigin);

    const terminalFrameLocator = page.frameLocator("#terminal");
    try {
      await terminalFrameLocator
        .locator(".xterm-helper-textarea")
        .waitFor({ state: "attached", timeout: 10_000 });
    } catch (error) {
      const frameUrls = page.frames().map((frame) => frame.url()).join(", ");
      throw new Error(
        `${error.message}; frames: ${frameUrls}; ${browserDiagnostics.join("; ")}`,
      );
    }
    const terminalFrame = page.frames().find((frame) =>
      frame.url().startsWith(terminal.attachmentEndpoint),
    );
    assert(terminalFrame, "Dashboard terminal iframe did not load");

    await terminalFrame.evaluate(
      ({ encodedMarker }) => {
        window.term.paste(
          `1..120 | ForEach-Object { Write-Output ('scroll-line-' + $_) }; $pwd.Path; Write-Output ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encodedMarker}')))`,
        );
        window.term.input("\r", true);
      },
      { encodedMarker: Buffer.from(marker, "utf8").toString("base64") },
    );

    await waitForTerminalText(terminalFrame, marker);
    const text = await terminalFrame.evaluate(terminalText);
    assert(
      text.includes(basename(fixture)),
      `Terminal cwd was not ${fixture}: ${text}`,
    );

    const viewport = await terminalFrame.evaluate(() => {
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
    await terminalFrameLocator.locator(".xterm-screen").hover();
    await page.mouse.wheel(0, 600);
    await terminalFrame.waitForFunction(
      () => document.querySelector(".xterm-viewport").scrollTop > 0,
    );

    const reconnectNavigation = terminalFrame.waitForNavigation({
      waitUntil: "load",
      timeout: 10_000,
    });
    const replacementPage = await browser.newPage();
    await postTerminalVisible(page, terminal.attachmentEndpoint);
    await replacementPage.goto(terminal.attachmentEndpoint);
    await replacementPage.waitForFunction(
      () => Boolean(window.term && document.querySelector(".xterm-helper-textarea")),
    );
    await reconnectNavigation;
    await waitForTerminalText(terminalFrame, marker);
    const replayedText = await terminalFrame.evaluate(terminalText);
    assert(
      replayedText.includes(marker),
      "Visible reconnect did not restore TerminalHost replay",
    );

    const unexpectedNavigation = terminalFrame
      .waitForNavigation({ timeout: 750 })
      .then(
        () => true,
        () => false,
      );
    await postTerminalVisible(page, terminal.attachmentEndpoint);
    assert(
      !(await unexpectedNavigation),
      "A healthy visible terminal reloaded without the reconnect overlay",
    );
    await replacementPage.close();

    console.log(
      `PASS: stock ttyd accepted input, preserved scrollback, and recovered an overlay-gated visible attachment through TerminalHost session ${terminal.sessionId} in ${fixture}`,
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
      await cleanupRuntimeResources({
        host,
        browser,
        dashboard,
        terminalSessionIds: terminalSessionId ? [terminalSessionId] : [],
      });
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
