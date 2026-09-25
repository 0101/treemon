import assertStrict from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
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

async function launchDashboardFixture() {
  const server = createHttpServer((_, response) => {
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end('<style>iframe{width:1000px;height:650px;border:0}</style><iframe id="terminal" sandbox="allow-scripts allow-same-origin" allow="clipboard-write"></iframe>');
  });
  await new Promise((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      resolveListen();
    });
  });
  const stop = () => new Promise((resolveStop, reject) =>
    server.close((error) => error ? reject(error) : resolveStop()));
  const port = server.address().port;
  if (port === 5000) {
    await stop();
    throw new Error("Dashboard fixture selected production port 5000");
  }
  return {
    origin: `http://127.0.0.1:${port}`,
    stop,
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

async function launchTerminalHost(executable, stateDirectory, worktreePath, port, dashboardOrigin) {
  const child = spawn(
    executable,
    [
      "--port",
      String(port),
      "--state-dir",
      stateDirectory,
      "--allowed-origin",
      dashboardOrigin,
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

async function verifyTerminalInputShortcuts(page) {
  const terminalInput = page.locator(".xterm-helper-textarea");
  const clipboardText = "# terminal-paste-first\n# terminal-paste-second\n";
  const normalizedPaste = clipboardText.replace(/\r?\n/g, "\r");
  const bracketedPaste = `\x1b[200~${normalizedPaste}\x1b[201~`;

  await page.context().grantPermissions(
    ["clipboard-read", "clipboard-write"],
    { origin: new URL(page.url()).origin },
  );
  await page.evaluate(async () => {
    window.__treemonPreviousClipboard = await navigator.clipboard.readText();
  });

  try {
    const clipboardSeeded = await page.evaluate(async (expectedText) => {
      await navigator.clipboard.writeText(expectedText);
      const clipboardText = await navigator.clipboard.readText();
      return clipboardText.replace(/\r\n/g, "\n") === expectedText;
    }, clipboardText);
    assert(clipboardSeeded, "Could not seed the isolated browser clipboard");

    await page.evaluate((expectedText) => {
      window.__treemonTerminalInput = [];
      window.__treemonTerminalInputSubscription = window.term.onData((data) => {
        window.__treemonTerminalInput.push(data);
      });
      window.__treemonClipboardMatched = null;
      window.__treemonPasteGuard = (event) => {
        const pastedText = event.clipboardData?.getData("text/plain") ?? "";
        window.__treemonClipboardMatched =
          pastedText.replace(/\r\n/g, "\n") === expectedText;

        if (!window.__treemonClipboardMatched) {
          event.preventDefault();
          event.stopImmediatePropagation();
        }
      };
      document.addEventListener("paste", window.__treemonPasteGuard, true);
    }, clipboardText);

    await terminalInput.focus();
    await terminalInput.press("Control+Enter");
    await page.waitForFunction(() => window.__treemonTerminalInput.length > 0);

    const ctrlEnterInput = await page.evaluate(() =>
      window.__treemonTerminalInput.splice(0),
    );
    assert(
      ctrlEnterInput.length === 1 && ctrlEnterInput[0] === "\n",
      `Ctrl+Enter emitted ${JSON.stringify(ctrlEnterInput)} instead of one line feed`,
    );

    const pasteWithMode = async (bracketedPasteMode, expectedInput) => {
      await page.evaluate(
        (enabled) =>
          new Promise((resolveWrite) => {
            window.term.write(
              enabled ? "\x1b[?2004h" : "\x1b[?2004l",
              resolveWrite,
            );
          }),
        bracketedPasteMode,
      );
      await page.evaluate(() => {
        window.__treemonTerminalInput = [];
        window.__treemonClipboardMatched = null;
      });

      await terminalInput.focus();
      await terminalInput.press("Control+V");
      await page.waitForFunction(
        () => window.__treemonClipboardMatched !== null,
      );

      const clipboardMatched = await page.evaluate(
        () => window.__treemonClipboardMatched,
      );
      assert(
        clipboardMatched,
        "Ctrl+V did not receive the seeded clipboard text",
      );
      await page.waitForFunction(() => window.__treemonTerminalInput.length > 0);

      const terminalInputData = await page.evaluate(() =>
        window.__treemonTerminalInput.splice(0),
      );
      assert(
        !terminalInputData.some((chunk) => chunk.includes("\x16")),
        "Ctrl+V reached xterm key handling as control byte 0x16",
      );
      assert(
        terminalInputData.length === 1 && terminalInputData[0] === expectedInput,
        bracketedPasteMode
          ? "Ctrl+V did not preserve bracketed-paste mode"
          : "Ctrl+V did not emit one normalized paste payload",
      );
    };

    await pasteWithMode(false, normalizedPaste);
    await pasteWithMode(true, bracketedPaste);
  } finally {
    await page.evaluate(async () => {
      if (window.__treemonPasteGuard) {
        document.removeEventListener("paste", window.__treemonPasteGuard, true);
      }
      window.__treemonTerminalInputSubscription?.dispose();
      const previousClipboard = window.__treemonPreviousClipboard;
      delete window.__treemonPreviousClipboard;
      delete window.__treemonTerminalInput;
      delete window.__treemonTerminalInputSubscription;
      delete window.__treemonClipboardMatched;
      delete window.__treemonPasteGuard;

      if (typeof previousClipboard === "string") {
        await navigator.clipboard.writeText(previousClipboard);
      }
    });
  }
}

async function verifyTerminalClipboard(page) {
  const copiedText = "clipboard check: café 😊\nsecond line";
  const oscCopy = `\x1b]52;c;${Buffer.from(copiedText, "utf8").toString("base64")}\x07`;
  const replayStart = "\x1b]777;treemon-clipboard-replay-start\x07";
  const replayEnd = "\x1b]777;treemon-clipboard-replay-end\x07";
  const terminalScreen = page.locator(".xterm-screen");

  await page.evaluate(() => {
    window.__treemonClipboardWrites = [];
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async (text) => {
          window.__treemonClipboardWrites.push(text);
        },
      },
    });
  });

  const write = (text) =>
    page.evaluate(
      (output) => new Promise((resolveWrite) => window.term.write(output, resolveWrite)),
      text,
    );
  const readWrites = () => page.evaluate(() => window.__treemonClipboardWrites);

  try {
    await terminalScreen.click({ button: "right" });
    await write(oscCopy);
    await page.waitForFunction(() => window.__treemonClipboardWrites.length === 1);
    assertStrict.deepEqual(await readWrites(), [copiedText]);

    await write(oscCopy);
    assertStrict.deepEqual(await readWrites(), [copiedText], "copy without a new gesture was accepted");

    await terminalScreen.click({ button: "right" });
    await write(replayStart + oscCopy + replayEnd);
    assertStrict.deepEqual(await readWrites(), [copiedText], "replayed OSC 52 changed the clipboard");

    await terminalScreen.click({ button: "right" });
    await write("\x1b]52;p;YWJj\x07" + "\x1b]52;c;?\x07");
    assertStrict.deepEqual(await readWrites(), [copiedText], "a primary-selection or invalid copy was accepted");
    const errorNotice = page.locator(".treemon-clipboard-error[role='alert']");
    await errorNotice.waitFor();
    assert(
      (await errorNotice.textContent()).includes("invalid or oversized"),
      "invalid clipboard data did not show an actionable failure",
    );
    await errorNotice.getByRole("button", { name: "Dismiss" }).click();

    await page.evaluate(() => {
      navigator.clipboard.writeText = async () => {
        throw new Error("simulated clipboard denial");
      };
    });
    await terminalScreen.click({ button: "right" });
    await write(oscCopy);
    await page.waitForFunction(() =>
      document.querySelector(".treemon-clipboard-error")?.textContent.includes("Check clipboard permissions"),
    );
    assertStrict.deepEqual(await readWrites(), [copiedText], "a denied write appeared to succeed");
    await errorNotice.getByRole("button", { name: "Dismiss" }).click();
    assert(await errorNotice.count() === 0, "dismiss did not remove the clipboard error");

    await page.evaluate(() => {
      navigator.clipboard.writeText = () => new Promise((resolveWrite) => {
        window.__treemonPendingWrite = resolveWrite;
      });
    });
    await terminalScreen.click({ button: "right" });
    await write(oscCopy);
    await terminalScreen.click({ button: "right" });
    await write("\x1b]52;c;?\x07");
    await errorNotice.waitFor();
    await page.evaluate(async () => {
      window.__treemonPendingWrite();
      await new Promise(queueMicrotask);
      delete window.__treemonPendingWrite;
    });
    assert(await errorNotice.count() === 1, "an older successful copy cleared a newer error");
    await errorNotice.getByRole("button", { name: "Dismiss" }).click();

    const maximumPayload = Buffer.alloc(196608, "x").toString("base64");
    const oversizedPayload = Buffer.alloc(196609, "x").toString("base64");
    assert(maximumPayload.length === 262144 && oversizedPayload.length > 262144, "test payload sizes drifted");
    await page.evaluate(() => {
      navigator.clipboard.writeText = async (text) => {
        window.__treemonClipboardWrites.push(text);
      };
    });
    await terminalScreen.click({ button: "right" });
    await write(`\x1b]52;c;${maximumPayload}\x07`);
    await page.waitForFunction(() => window.__treemonClipboardWrites.length === 2);
    assert(
      (await page.evaluate(() => window.__treemonClipboardWrites[1].length)) === 196608,
      "a valid clipboard payload at the size limit was truncated",
    );
    await terminalScreen.click({ button: "right" });
    await write(`\x1b]52;c;${oversizedPayload}\x07`);
    assert((await readWrites()).length === 2, "an oversized clipboard payload was accepted");
    await page.waitForFunction(() =>
      document.querySelector(".treemon-clipboard-error")?.textContent.includes("oversized"),
    );
    await page.locator(".xterm-helper-textarea").press("Control+c");
    await write(oscCopy);
    await page.waitForFunction(() => window.__treemonClipboardWrites.length === 3);
    assert((await readWrites())[2] === copiedText, "Ctrl+C did not copy the selected text");
  } finally {
    await page.evaluate(() => {
      delete navigator.clipboard;
      delete window.__treemonClipboardWrites;
    });
  }
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
  let dashboardFixture;

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
    dashboardFixture = await launchDashboardFixture();
    const stateDirectory = join(fixture, "terminal-host-state");
    host = await launchTerminalHost(
      hostExecutable,
      stateDirectory,
      fixture,
      controlPort,
      dashboardFixture.origin,
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
      ({ marker }) => {
        window.term.input(
          `1..120 | ForEach-Object { Write-Output ('scroll-line-' + $_) }; $pwd.Path; Write-Output '${marker}'`,
          true,
        );
        window.term.input("\r", true);
      },
      { marker },
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
    if (!process.argv.includes("--clipboard-only")) {
      await verifyTerminalInputShortcuts(page);
    }

    const dashboard = await browser.newPage();
    const frameErrors = [];
    dashboard.on("console", (message) => {
      if (message.type() === "error") {
        frameErrors.push(message.text().replaceAll(host.manifest.bearerToken, "[redacted]").slice(0, 200));
      }
    });
    dashboard.on("requestfailed", (request) => {
      frameErrors.push(`request failed: ${request.failure()?.errorText}`);
    });
    await dashboard.goto(dashboardFixture.origin);
    await dashboard.evaluate(
      (endpoint) => { document.getElementById("terminal").src = endpoint; },
      terminal.attachmentEndpoint,
    );
    try {
      await dashboard.frameLocator("#terminal").locator(".xterm-screen").waitFor({ timeout: 10000 });
    } catch {
      const frames = dashboard.frames().map((frame) =>
        frame === dashboard.mainFrame() ? "dashboard" :
        frame.url() === "about:blank" ? "blank" :
        frame.url().startsWith(terminal.attachmentEndpoint) ? "terminal" : "other",
      );
      throw new Error(`Embedded terminal iframe did not load (${frames.join(", ")}): ${frameErrors.join("; ")}`);
    }
    const terminalFrame = dashboard.frames().find((frame) => frame !== dashboard.mainFrame());
    assert(terminalFrame, "The isolated embedded terminal frame did not load");
    const clipboardAllowed = await terminalFrame.evaluate(() =>
      document.permissionsPolicy?.allowsFeature("clipboard-write") ??
      document.featurePolicy?.allowsFeature("clipboard-write"),
    );
    assert(clipboardAllowed, "The embedded terminal lacks clipboard-write permission");
    await verifyTerminalClipboard(terminalFrame);

    console.log(
      `PASS: Treemon bridged OSC 52 through stock ttyd, input, and scrollback in isolated terminal ${terminal.sessionId} at ${fixture}`,
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
    try {
      await dashboardFixture?.stop();
    } catch (error) {
      cleanupError = cleanupError
        ? new Error(`Runtime cleanup failed: ${cleanupError.message}; dashboard fixture: ${error.message}`)
        : error;
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
