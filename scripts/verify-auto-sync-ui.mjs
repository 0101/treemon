import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { chromium } from "playwright";

const repoRoot = resolve(import.meta.dirname, "..");
const fixturePath = resolve(repoRoot, "src", "Tests", "fixtures", "worktrees.json");
const serverProject = resolve(repoRoot, "src", "Server");
const viteCli = resolve(repoRoot, "node_modules", "vite", "bin", "vite.js");

function run(command, args, options = {}) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      env: process.env,
      stdio: ["ignore", "pipe", "pipe"],
      ...options,
    });
    let output = "";
    child.stdout.on("data", (chunk) => {
      output += chunk;
    });
    child.stderr.on("data", (chunk) => {
      output += chunk;
    });
    child.once("error", reject);
    child.once("exit", (code) => {
      if (code === 0) resolvePromise(output);
      else reject(new Error(`${command} exited ${code}${output ? `\n${output}` : ""}`));
    });
  });
}

async function reservePorts(count) {
  const servers = await Promise.all(
    Array.from({ length: count }, () =>
      new Promise((resolvePromise, reject) => {
        const server = createServer();
        server.listen(0, "127.0.0.1", () => resolvePromise(server));
        server.once("error", reject);
      })),
  );
  const ports = servers.map((server) => server.address().port);
  await Promise.all(
    servers.map((server) => new Promise((resolvePromise) => server.close(resolvePromise))),
  );
  return ports;
}

async function waitForUrl(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.status < 500) return;
    } catch {
      // Poll until the child process binds its port.
    }
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 100));
  }
  throw new Error(`Timed out waiting for ${url}`);
}

async function waitForCondition(predicate, description, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 10));
  }
  throw new Error(`Timed out waiting for ${description}`);
}

async function settleBrowserEvents(page) {
  await page.evaluate(
    () =>
      new Promise((resolvePromise) => {
        requestAnimationFrame(() => requestAnimationFrame(resolvePromise));
      }),
  );
}

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  child.kill("SIGTERM");
  await Promise.race([
    new Promise((resolvePromise) => child.once("exit", resolvePromise)),
    new Promise((resolvePromise) => setTimeout(resolvePromise, 2000)),
  ]);
  if (child.exitCode === null) child.kill("SIGKILL");
}

const [apiPort, canvasPort, vitePort] = await reservePorts(3);
let server;
let vite;
let browser;

try {
  await run("dotnet", [
    "fable",
    "src/Client",
    "--outDir",
    "src/Client/output",
    "--noCache",
  ]);

  server = spawn(
    "dotnet",
    [
      "run",
      "--project",
      serverProject,
      "--",
      repoRoot,
      "--port",
      String(apiPort),
      "--canvas-port",
      String(canvasPort),
      "--test-fixtures",
      fixturePath,
    ],
    {
      cwd: repoRoot,
      env: process.env,
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  await waitForUrl(`http://127.0.0.1:${apiPort}`, 30000);

  vite = spawn(
    process.execPath,
    [viteCli, "--host", "127.0.0.1", "--port", String(vitePort)],
    {
      cwd: repoRoot,
      env: {
        ...process.env,
        VITE_PORT: String(vitePort),
        API_PORT: String(apiPort),
        CANVAS_PORT: String(canvasPort),
      },
      stdio: ["ignore", "pipe", "pipe"],
    },
  );
  const dashboardUrl = `http://127.0.0.1:${vitePort}`;
  await waitForUrl(dashboardUrl, 15000);

  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  const routedRequests = [];

  await page.route("**/IWorktreeApi/toggleAutoSync", async (route) => {
    routedRequests.push(route);
  });
  await page.goto(dashboardUrl);
  await page.locator(".wt-card .branch-name").first().waitFor({ timeout: 15000 });

  const fullCards = page.locator(".wt-card:not(.compact)");
  const allToggles = fullCards.locator(".auto-sync-btn");
  const cardCount = await fullCards.count();
  const toggleCount = await allToggles.count();
  const behindToggleCount = await page
    .locator(".main-behind-row:has(.main-behind:not(.up-to-date)) .auto-sync-btn")
    .count();
  const upToDateToggleCount = await page
    .locator(".main-behind-row:has(.main-behind.up-to-date) .auto-sync-btn")
    .count();
  const dirtyToggleCount = await page
    .locator(".main-behind-row:has(.dirty-warning) .auto-sync-btn")
    .count();
  const cleanToggleCount = await page
    .locator(".main-behind-row:not(:has(.dirty-warning)) .auto-sync-btn")
    .count();

  assert.equal(toggleCount, cardCount);
  assert.ok(behindToggleCount > 0);
  assert.ok(upToDateToggleCount > 0);
  assert.ok(dirtyToggleCount > 0);
  assert.ok(cleanToggleCount > 0);

  const card = page
    .locator(".repo-section:has(.repo-name:text-is('treemon'))")
    .locator(".wt-card:has(.branch-name:text-is('multirepo'))");
  const toggle = card.locator(".auto-sync-btn");
  const secondCard = page
    .locator(".repo-section:has(.repo-name:text-is('treemon'))")
    .locator(".wt-card:has(.branch-name:text-is('test/add-health-endpoint'))");
  const secondToggle = secondCard.locator(".auto-sync-btn");
  await card.waitFor();
  await secondCard.waitFor();
  assert.equal(await toggle.getAttribute("aria-pressed"), "false");
  assert.equal(await secondToggle.getAttribute("aria-pressed"), "false");
  assert.equal(
    await toggle.locator("xpath=ancestor::*[contains(@class,'main-behind-row')]").count(),
    1,
  );

  await toggle.click();
  await waitForCondition(() => routedRequests.length >= 1, "first auto-sync request");
  assert.equal(await toggle.getAttribute("aria-pressed"), "true");
  assert.equal(await toggle.getAttribute("aria-disabled"), "true");
  assert.match(await toggle.getAttribute("class"), /\bactive\b/);
  assert.notEqual(
    await toggle.evaluate((element) => getComputedStyle(element).boxShadow),
    "none",
  );

  const requestsBeforeIgnoredMouse = routedRequests.length;
  await toggle.evaluate((element) => element.click());
  await settleBrowserEvents(page);
  const requestsAfterIgnoredMouse = routedRequests.length;

  await card.click();
  const requestsBeforeIgnoredKeyboard = routedRequests.length;
  await page.keyboard.press("s");
  await settleBrowserEvents(page);
  const requestsAfterIgnoredKeyboard = routedRequests.length;

  await secondToggle.click();
  await waitForCondition(() => routedRequests.length >= 2, "second worktree auto-sync request");
  const secondToggleAcceptedWhileFirstPending =
    (await secondToggle.getAttribute("aria-pressed")) === "true" &&
    (await secondToggle.getAttribute("aria-disabled")) === "true" &&
    (await toggle.getAttribute("aria-disabled")) === "true";

  await routedRequests[1].fulfill({
    contentType: "application/json",
    body: '{"Ok":null}',
  });
  await page.waitForFunction(
    (element) => !element.disabled,
    await secondToggle.elementHandle(),
  );
  const firstRemainedPendingAfterSecondCompleted =
    (await toggle.getAttribute("aria-disabled")) === "true" &&
    (await secondToggle.getAttribute("aria-disabled")) === "false";

  await routedRequests[0].fulfill({
    contentType: "application/json",
    body: '{"Ok":null}',
  });
  await toggle.waitFor({ state: "visible" });
  await page.waitForFunction((element) => !element.disabled, await toggle.elementHandle());

  await toggle.click();
  await waitForCondition(() => routedRequests.length >= 3, "third auto-sync request");
  assert.equal(await toggle.getAttribute("aria-pressed"), "false");
  assert.doesNotMatch(await toggle.getAttribute("class"), /\bactive\b/);
  await routedRequests[2].fulfill({
    contentType: "application/json",
    body: '{"Ok":null}',
  });
  await page.waitForFunction((element) => !element.disabled, await toggle.elementHandle());

  await page.evaluate(() => {
    window.__autoSyncErrorSurfaceObserved = Boolean(
      document.querySelector("#eye-shape"),
    );
    const observer = new MutationObserver(() => {
      if (document.querySelector("#eye-shape")) {
        window.__autoSyncErrorSurfaceObserved = true;
        observer.disconnect();
      }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
  });

  await card.click();
  await page.keyboard.press("s");
  await waitForCondition(() => routedRequests.length >= 4, "fourth auto-sync request");
  assert.equal(await toggle.getAttribute("aria-pressed"), "true");
  await routedRequests[3].fulfill({
    contentType: "application/json",
    body: '{"Error":"persist failed"}',
  });
  await page.waitForFunction(
    (element) => element.getAttribute("aria-pressed") === "false" && !element.disabled,
    await toggle.elementHandle(),
  );
  await page.waitForFunction(
    () => window.__autoSyncErrorSurfaceObserved === true,
    undefined,
    { timeout: 5000 },
  );

  const result = {
    togglePresence: {
      cardCount,
      toggleCount,
      behindToggleCount,
      upToDateToggleCount,
      dirtyToggleCount,
      cleanToggleCount,
    },
    pendingInputIsolation: {
      ignoredMouseRequestDelta:
        requestsAfterIgnoredMouse - requestsBeforeIgnoredMouse,
      ignoredKeyboardRequestDelta:
        requestsAfterIgnoredKeyboard - requestsBeforeIgnoredKeyboard,
      secondToggleAcceptedWhileFirstPending,
      firstRemainedPendingAfterSecondCompleted,
    },
    requestLifecycle: {
      toggleRequests: routedRequests.length,
      finalAriaPressed: await toggle.getAttribute("aria-pressed"),
      finalAriaDisabled: await toggle.getAttribute("aria-disabled"),
      errorSurfaceObserved:
        await page.evaluate(() => window.__autoSyncErrorSurfaceObserved),
      errorSurfaceEyeCount: await page.locator("#eye-shape").count(),
    },
  };

  console.log(JSON.stringify(result, null, 2));
  assert.equal(result.pendingInputIsolation.ignoredMouseRequestDelta, 0);
  assert.equal(result.pendingInputIsolation.ignoredKeyboardRequestDelta, 0);
  assert.equal(
    result.pendingInputIsolation.secondToggleAcceptedWhileFirstPending,
    true,
  );
  assert.equal(
    result.pendingInputIsolation.firstRemainedPendingAfterSecondCompleted,
    true,
  );
  assert.equal(
    result.requestLifecycle.errorSurfaceObserved,
    true,
    "Failed auto-sync request rolled back state but did not activate the dashboard's normal error surface",
  );
} finally {
  if (browser) await browser.close();
  await stop(vite);
  await stop(server);
}
