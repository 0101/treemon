import { randomUUID } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { createServer as createHttpServer } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { DatabaseSync } from "node:sqlite";
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync,
  rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { chromium } from "playwright";

const sourceRoot = resolve(import.meta.dirname, "..");
const protectedPorts = new Set([5000, 5001, 5002, 5174]);
const prompt =
  "Reply with exactly PRESENCE_READY. Do not use tools or modify files.";

const delay = (milliseconds) => new Promise(
  (resolveDelay) => setTimeout(resolveDelay, milliseconds),
);

const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};

const commandExists = (command) =>
  spawnSync("where.exe", [command], {
    stdio: "ignore",
    windowsHide: true,
  }).status === 0;

function run(fileName, arguments_, options = {}) {
  const result = spawnSync(fileName, arguments_, {
    cwd: options.cwd ?? sourceRoot,
    env: options.env ?? process.env,
    encoding: "utf8",
    windowsHide: true,
  });

  assert(
    result.status === 0,
    `${fileName} ${arguments_.join(" ")} failed with exit code ${result.status}: ${(result.stderr || result.stdout).trim()}`,
  );

}

const appendTail = (current, chunk, maximum = 16_384) =>
  `${current}${chunk}`.slice(-maximum);

function startProcess(fileName, arguments_, options) {
  const child = spawn(fileName, arguments_, {
    cwd: options.cwd,
    env: options.env,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  let stdout = "";
  let stderr = "";
  let processError;

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    stdout = appendTail(stdout, chunk);
  });
  child.stderr.on("data", (chunk) => {
    stderr = appendTail(stderr, chunk);
  });
  child.once("error", (error) => {
    processError = error;
  });

  const closed = new Promise((resolveClosed) => child.once("close", resolveClosed));

  return {
    child,
    closed,
    failure: () => processError,
    output: () => ({ stdout, stderr }),
  };
}

async function waitForExit(process_, timeoutMs) {
  return Promise.race([
    process_.closed.then(() => true),
    delay(timeoutMs).then(() => false),
  ]);
}

async function stopProcess(process_, timeoutMs = 10_000) {
  if (!process_ || process_.child.exitCode !== null) return;
  process_.child.kill();
  if (await waitForExit(process_, timeoutMs)) return;

  process_.child.kill("SIGKILL");
  assert(
    await waitForExit(process_, timeoutMs),
    `Process ${process_.child.pid} did not exit`,
  );
}

async function waitForValue(description, timeoutMs, poll, evidence = () => "") {
  const deadline = Date.now() + timeoutMs;
  let lastError;

  while (Date.now() < deadline) {
    try {
      const value = await poll();
      if (value !== undefined) return value;
    } catch (error) {
      lastError = error;
    }
    await delay(100);
  }

  const detail = lastError ? ` Last error: ${lastError.message}` : "";
  const diagnostic = evidence();
  throw new Error(
    `Timed out waiting for ${description}.${detail}${diagnostic ? ` Evidence: ${diagnostic}` : ""}`,
  );
}

async function allocatePort() {
  return await new Promise((resolvePort, reject) => {
    const server = createTcpServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => {
        if (protectedPorts.has(port)) {
          allocatePort().then(resolvePort, reject);
        } else {
          resolvePort(port);
        }
      });
    });
  });
}

function withoutEnvironmentVariables(names) {
  const excluded = new Set(names.map((name) => name.toLowerCase()));
  return Object.fromEntries(
    Object.entries(process.env).filter(
      ([name]) => !excluded.has(name.toLowerCase()),
    ),
  );
}

function isolatedEnvironment(overrides) {
  return {
    ...withoutEnvironmentVariables([
      "COPILOT_HOME",
      "TREEMON_PORT",
      "TREEMON_PORTS",
      "TREEMON_CONFIG_DIR",
      "TREEMON_TERMINAL_HOST_STATE_DIR",
      "TREEMON_TERMINAL_HOST_EXECUTABLE",
      "TREEMON_TERMINAL_SESSION_ID",
    ]),
    ...overrides,
  };
}

function initializeWorktree(repository, worktree) {
  mkdirSync(repository, { recursive: true });
  run("git", ["init", "--quiet", "-b", "main", repository]);
  run("git", ["config", "user.name", "Treemon Verification"], {
    cwd: repository,
  });
  run(
    "git",
    ["config", "user.email", "treemon-verification@example.invalid"],
    { cwd: repository },
  );
  run("git", ["config", "commit.gpgsign", "false"], { cwd: repository });
  writeFileSync(
    join(repository, "README.md"),
    "# Real CLI reporting verification\n",
    "utf8",
  );
  run("git", ["add", "README.md"], { cwd: repository });
  run("git", ["commit", "--quiet", "-m", "Create verification fixture"], {
    cwd: repository,
  });
  mkdirSync(dirname(worktree), { recursive: true });
  run("git", ["worktree", "add", "--quiet", "-b", "presence-recovery", worktree], {
    cwd: repository,
  });
}

function installReportingExtension(copilotHome) {
  const source = join(sourceRoot, "src", "Extension", "reporting");
  const destination = join(
    copilotHome,
    "extensions",
    "treemon-reporting",
  );
  mkdirSync(destination, { recursive: true });

  readdirSync(source)
    .filter((name) => name.endsWith(".mjs") || name === "package.json")
    .forEach((name) => {
      copyFileSync(join(source, name), join(destination, name));
    });

  return destination;
}

async function closeServer(server) {
  if (!server?.listening) return;
  await new Promise((resolveClose, reject) => {
    server.close((error) => (error ? reject(error) : resolveClose()));
  });
}

async function startOutageGate(port, connectionTimes) {
  const server = createTcpServer((socket) => {
    connectionTimes.push(Date.now());
    socket.destroy();
  });
  await new Promise((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolveListen);
  });
  return server;
}

async function readRequestBody(request) {
  const chunks = [];
  let length = 0;

  for await (const chunk of request) {
    length += chunk.length;
    if (length > 1_048_576) {
      throw new Error("Unmonitored verification request exceeded 1 MiB");
    }
    chunks.push(chunk);
  }

  return Buffer.concat(chunks).toString("utf8");
}

async function startUnmonitoredServer(port, requests) {
  const server = createHttpServer(async (request, response) => {
    try {
      const raw = await readRequestBody(request);
      requests.push({
        at: new Date().toISOString(),
        method: request.method,
        path: request.url,
        body: JSON.parse(raw),
      });
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(
        JSON.stringify({
          recorded: false,
          monitored: false,
          retryable: false,
          reason: "verification unmonitored endpoint",
        }),
      );
    } catch (error) {
      response.writeHead(400, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ error: error.message }));
    }
  });

  await new Promise((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolveListen);
  });
  return server;
}

async function waitForManifest(stateDirectory, host) {
  const manifestPath = join(stateDirectory, "host.json");

  return await waitForValue(
    "the isolated TerminalHost manifest",
    15_000,
    () => {
      if (host.failure()) throw host.failure();
      if (host.child.exitCode !== null) {
        const output = host.output();
        throw new Error(
          `TerminalHost exited with code ${host.child.exitCode}: ${output.stderr || output.stdout}`,
        );
      }
      if (!existsSync(manifestPath)) return undefined;

      try {
        return JSON.parse(readFileSync(manifestPath, "utf8"));
      } catch {
        return undefined;
      }
    },
    () => JSON.stringify(host.output()),
  );
}

async function hostRequest(manifest, method, path, body) {
  const response = await fetch(new URL(path, manifest.endpoint), {
    method,
    headers: {
      Authorization: `Bearer ${manifest.bearerToken}`,
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const text = await response.text();

  assert(
    response.ok,
    `TerminalHost ${method} ${path} returned HTTP ${response.status}`,
  );

  return text ? JSON.parse(text) : undefined;
}

async function sendTerminalCommand(attachmentEndpoint, command) {
  const endpoint = new URL(attachmentEndpoint);
  endpoint.protocol = "ws:";
  endpoint.pathname = `${endpoint.pathname}ws`;

  const socket = new WebSocket(endpoint, "treemon-command");
  await new Promise((resolveOpen, reject) => {
    const timeout = setTimeout(
      () => reject(new Error("Timed out opening the terminal command socket")),
      5_000,
    );
    socket.addEventListener("open", () => {
      clearTimeout(timeout);
      resolveOpen();
    }, { once: true });
    socket.addEventListener("error", () => {
      clearTimeout(timeout);
      reject(new Error("Could not open the terminal command socket"));
    }, { once: true });
  });

  socket.send(Buffer.from('{"AuthToken":"","columns":160,"rows":50}', "utf8"));
  socket.send(Buffer.from(`0${command}\r`, "utf8"));

  await waitForValue(
    "the terminal command socket to flush",
    5_000,
    () => socket.bufferedAmount === 0 ? true : undefined,
  );
  socket.close(1000, "Terminal command submitted");
}

function filesBelow(directory, depth = 2) {
  if (!existsSync(directory) || depth < 0) return [];

  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    return entry.isDirectory()
      ? filesBelow(path, depth - 1)
      : [path];
  });
}

function tail(path, maximum = 8_192) {
  return existsSync(path)
    ? readFileSync(path, "utf8").slice(-maximum)
    : "";
}

function parseExtensionStartup(copilotHome) {
  const logDirectory = join(copilotHome, "logs", "extensions");
  const logs = filesBelow(logDirectory, 1)
    .filter((path) => /treemon-reporting/i.test(path))
    .sort((left, right) => statSync(right).mtimeMs - statSync(left).mtimeMs);
  const pattern =
    /\[treemon-reporting\] startup pid=(\d+) parentPid=(\d+) terminalOrigin=(present|absent) endpoints=(\d+)/;
  const found = logs
    .map((path) => ({ path, match: pattern.exec(tail(path)) }))
    .find((log) => log.match);

  if (!found) return undefined;
  const [, extensionPid, parentPid, terminalOrigin, endpointCount] = found.match;
  return {
    path: found.path,
    extensionPid: Number(extensionPid),
    parentPid: Number(parentPid),
    terminalOrigin,
    endpointCount: Number(endpointCount),
  };
}

function processRows({ rootPid, processIds } = {}) {
  const scope = rootPid
    ? ["-RootPid", String(rootPid)]
    : ["-ProcessIds", (processIds ?? []).join(",")];
  const result = spawnSync(
    "pwsh.exe",
    [
      "-NoLogo",
      "-NoProfile",
      "-NonInteractive",
      "-File",
      join(sourceRoot, "scripts", "capture-process-snapshot.ps1"),
      ...scope,
    ],
    { encoding: "utf8", windowsHide: true },
  );

  assert(
    result.status === 0,
    `Could not capture process evidence: ${result.stderr.trim()}`,
  );
  return JSON.parse(result.stdout);
}

function descendantRows(rootPid, rows) {
  const collect = (parents, remaining, collected) => {
    const children = remaining.filter((row) => parents.has(row.parentPid));
    if (children.length === 0) return collected;

    const childIds = new Set(children.map((row) => row.pid));
    return collect(
      childIds,
      remaining.filter((row) => !childIds.has(row.pid)),
      [...collected, ...children],
    );
  };

  return collect(new Set([rootPid]), rows, []);
}

function loaderEvidence(copilotHome, extensionDirectory, hostPid) {
  const logFiles = filesBelow(join(copilotHome, "logs"), 3);
  const loaderLines = [
    ...new Set(
      logFiles.flatMap((path) =>
        readFileSync(path, "utf8")
          .split(/\r?\n/)
          .filter((line) =>
            /extension|experimental|trust|workspace initialized|starting copilot cli|welcome/i.test(
              line,
            ),
          ),
      ),
    ),
  ].slice(-80);
  let descendants = [];
  let processEvidenceError;

  try {
    descendants = descendantRows(
      hostPid,
      processRows({ rootPid: hostPid }),
    ).map((row) => ({
      pid: row.pid,
      parentPid: row.parentPid,
      startTicks: row.startTicks,
      name: row.name,
      commandLine: String(row.commandLine || "").slice(0, 320),
    }));
  } catch (error) {
    processEvidenceError = error.message;
  }

  return JSON.stringify({
    installedEntry: join(extensionDirectory, "extension.mjs"),
    installedEntryExists: existsSync(
      join(extensionDirectory, "extension.mjs"),
    ),
    extensionLogs: logFiles.filter((path) => /extension/i.test(path)),
    loaderLines,
    settings: tail(join(copilotHome, "settings.json"), 2_048),
    descendants,
    processEvidenceError,
  });
}

async function terminalText(page) {
  return await page.evaluate(() => {
    const buffer = window.term.buffer.active;
    return Array.from(
      { length: buffer.length },
      (_, index) =>
        buffer.getLine(index)?.translateToString(true) ?? "",
    ).join("\n");
  });
}

async function openTerminalAttachment(attachmentEndpoint) {
  const browser = await chromium.launch({ headless: true });

  try {
    const page = await browser.newPage();
    await page.goto(attachmentEndpoint);
    await page.waitForFunction(
      () => Boolean(window.term),
      undefined,
      { timeout: 10_000 },
    );
    return { browser, page };
  } catch (error) {
    await browser.close();
    throw error;
  }
}

async function acceptDisposableFolderTrust(page) {
  try {
    await waitForValue(
      "the isolated Copilot folder-trust confirmation",
      20_000,
      async () => {
        const text = await terminalText(page);
        return text.includes("Confirm folder trust")
          && text.includes("Do you trust the files in this folder?")
          ? text
          : undefined;
      },
    );
  } catch (error) {
    const replay = await terminalReplay(page);
    throw new Error(
      `${error.message} Terminal replay: ${JSON.stringify(replay)}`,
      { cause: error },
    );
  }

  await page.evaluate(() => window.term.input("\r", true));
}

async function terminalReplay(page) {
  try {
    return (await terminalText(page)).slice(-8_192);
  } catch (error) {
    return `Could not capture terminal replay: ${error.message}`;
  }
}

async function waitForExtensionStartup(
  copilotHome,
  extensionDirectory,
  host,
  terminalPage,
) {
  try {
    return await waitForValue(
      "the real Copilot reporting extension process",
      45_000,
      () => {
        if (host.child.exitCode !== null) {
          throw new Error(`TerminalHost exited with code ${host.child.exitCode}`);
        }
        return parseExtensionStartup(copilotHome);
      },
      () => loaderEvidence(
        copilotHome,
        extensionDirectory,
        host.child.pid,
      ),
    );
  } catch (error) {
    const replay = await terminalReplay(terminalPage);
    throw new Error(
      `${error.message} Terminal replay: ${JSON.stringify(replay)}`,
      { cause: error },
    );
  }
}

async function waitForServer(port, serverProcess) {
  return await waitForValue(
    "the isolated Treemon server",
    45_000,
    async () => {
      if (serverProcess.failure()) throw serverProcess.failure();
      if (serverProcess.child.exitCode !== null) {
        const output = serverProcess.output();
        throw new Error(
          `Treemon exited with code ${serverProcess.child.exitCode}: ${output.stderr || output.stdout}`,
        );
      }

      try {
        const response = await fetch(
          `http://127.0.0.1:${port}/IWorktreeApi/getRoots`,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: "[null]",
          },
        );
        return response.ok ? await response.json() : undefined;
      } catch {
        return undefined;
      }
    },
    () => JSON.stringify(serverProcess.output()),
  );
}

function sqliteRows(database, sql) {
  if (!existsSync(database)) return [];
  const connection = new DatabaseSync(database, { readOnly: true });

  try {
    return connection.prepare(sql).all();
  } finally {
    connection.close();
  }
}

async function waitForObservedInstance(database, terminalSessionId, server) {
  const sql = `
SELECT
  process_id AS processId,
  CAST(process_start_ticks AS TEXT) AS processStartTicks,
  session_id AS sessionId,
  worktree_path AS worktreePath,
  status,
  terminal_session_id AS terminalSessionId,
  last_seen AS lastSeen,
  closed_at AS closedAt
FROM session_instances
WHERE terminal_session_id = '${terminalSessionId}'
ORDER BY last_seen DESC;`;

  return await waitForValue(
    "the real CLI process instance to become observable",
    30_000,
    () => {
      if (server.child.exitCode !== null) {
        throw new Error(`Treemon exited with code ${server.child.exitCode}`);
      }
      const rows = sqliteRows(database, sql);
      return rows.length > 0 ? rows[0] : undefined;
    },
    () => JSON.stringify({
      database,
      databaseExists: existsSync(database),
      serverOutput: server.output(),
    }),
  );
}

async function callKillSession(port, worktree) {
  const response = await fetch(
    `http://127.0.0.1:${port}/IWorktreeApi/killSession`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify([{ WorktreePath: worktree }]),
    },
  );
  const text = await response.text();

  assert(
    response.ok,
    `killSession returned HTTP ${response.status}: ${text}`,
  );
  return text ? JSON.parse(text) : undefined;
}

async function shutdownHost(host, manifest) {
  if (!host || host.child.exitCode !== null) return;

  try {
    await hostRequest(manifest, "POST", "/api/v2/shutdown");
  } catch {
    host.child.kill();
  }

  assert(
    await waitForExit(host, 15_000),
    `TerminalHost PID ${host.child.pid} survived shutdown`,
  );
}

function exactSurvivors(captured, currentRows) {
  const currentByPid = new Map(currentRows.map((row) => [row.pid, row]));
  return captured.filter((identity) =>
    currentByPid.get(identity.pid)?.startTicks === identity.startTicks
  );
}

async function runRealCliReportingVerification() {
  assert(process.platform === "win32", "Real CLI reporting verification requires Windows");
  assert(commandExists("copilot"), "GitHub Copilot CLI is not installed");

  const configuration = process.env.TREEMON_VERIFY_CONFIGURATION || "Release";
  const hostExecutable = join(
    sourceRoot,
    "src",
    "TerminalHost",
    "bin",
    configuration,
    "net10.0",
    "TerminalHost.exe",
  );
  const serverExecutable = join(
    sourceRoot,
    "src",
    "Server",
    "bin",
    configuration,
    "net10.0",
    "Treemon.exe",
  );
  const ttydExecutable = [
    join(dirname(hostExecutable), "ttyd.exe"),
    join(sourceRoot, ".tools", "ttyd", "1.7.7", "ttyd.exe"),
  ].find(existsSync);

  assert(
    existsSync(hostExecutable),
    `Missing ${hostExecutable}. Build TerminalHost first.`,
  );
  assert(
    existsSync(serverExecutable),
    `Missing ${serverExecutable}. Build Treemon first.`,
  );
  assert(
    ttydExecutable,
    "Missing ttyd.exe. Run '.\\treemon.ps1 setup-ttyd'.",
  );

  const fixture = join(
    sourceRoot,
    ".agents",
    "real-cli-reporting",
    randomUUID(),
  );
  const repository = join(fixture, "repo");
  const worktree = join(fixture, "worktrees", "presence");
  const runtimeDirectory = join(fixture, "runtime");
  const configDirectory = join(fixture, "config");
  const hostStateDirectory = join(fixture, "terminal-host-state");
  const copilotHome = join(fixture, "copilot-home");
  const ownerConnections = [];
  const unmonitoredRequests = [];
  let outageGate;
  let unmonitoredServer;
  let host;
  let manifest;
  let terminal;
  let server;
  let serverPort;
  let capturedIdentities = [];
  let terminalBrowser;
  let terminalPage;
  let killSessionCompleted = false;
  let scenarioError;
  const cleanupErrors = [];

  try {
    mkdirSync(runtimeDirectory, { recursive: true });
    mkdirSync(configDirectory, { recursive: true });
    mkdirSync(hostStateDirectory, { recursive: true });
    initializeWorktree(repository, worktree);
    const extensionDirectory = installReportingExtension(copilotHome);

    serverPort = await allocatePort();
    const unmonitoredPort = await allocatePort();
    const controlPort = await allocatePort();
    [serverPort, unmonitoredPort, controlPort].forEach((port) => {
      assert(!protectedPorts.has(port), `Selected protected port ${port}`);
    });

    outageGate = await startOutageGate(serverPort, ownerConnections);
    unmonitoredServer = await startUnmonitoredServer(
      unmonitoredPort,
      unmonitoredRequests,
    );

    host = startProcess(
      hostExecutable,
      [
        "--state-dir",
        hostStateDirectory,
        "--port",
        String(controlPort),
        "--ttyd",
        ttydExecutable,
        "--shell",
        "pwsh",
        "--allowed-origin",
        `http://127.0.0.1:${serverPort}`,
      ],
      {
        cwd: worktree,
        env: isolatedEnvironment({
          COPILOT_HOME: copilotHome,
          COPILOT_AUTO_UPDATE: "false",
          TREEMON_PORTS: `${serverPort},${unmonitoredPort}`,
          TREEMON_TERMINAL_HOST_STATE_DIR: hostStateDirectory,
        }),
      },
    );
    manifest = await waitForManifest(hostStateDirectory, host);
    assert(
      new URL(manifest.endpoint).port !== "5000",
      "Isolated TerminalHost bound production port 5000",
    );

    const snapshot = await hostRequest(
      manifest,
      "POST",
      "/api/v2/terminals",
      { worktreePath: worktree },
    );
    assert(
      snapshot.terminals.length === 1,
      `Expected one isolated terminal, found ${snapshot.terminals.length}`,
    );
    terminal = snapshot.terminals[0];
    assert(
      new URL(terminal.attachmentEndpoint).port !== "5000",
      "Isolated terminal attachment bound production port 5000",
    );

    const command =
      `copilot --experimental --yolo --log-level all -i '${prompt.replaceAll("'", "''")}'`;
    await sendTerminalCommand(terminal.attachmentEndpoint, command);
    const attachment = await openTerminalAttachment(
      terminal.attachmentEndpoint,
    );
    terminalBrowser = attachment.browser;
    terminalPage = attachment.page;
    await acceptDisposableFolderTrust(terminalPage);

    const startup = await waitForExtensionStartup(
      copilotHome,
      extensionDirectory,
      host,
      terminalPage,
    );
    assert(
      startup.terminalOrigin === "present",
      `Reporting extension did not inherit TREEMON_TERMINAL_SESSION_ID: ${JSON.stringify(startup)}`,
    );
    assert(
      startup.endpointCount === 2,
      `Reporting extension did not inherit both TREEMON_PORTS destinations: ${JSON.stringify(startup)}`,
    );

    const unmonitoredPresence = await waitForValue(
      "the unmonitored endpoint's real session_present report",
      15_000,
      () => unmonitoredRequests.find(
        (request) => request.body?.kind === "session_present",
      ),
      () => JSON.stringify({
        requests: unmonitoredRequests,
        extensionLog: tail(startup.path),
      }),
    );
    assert(
      unmonitoredPresence.body.terminalSessionId === terminal.sessionId,
      "The real report did not carry the TerminalHost terminal origin",
    );
    assert(
      unmonitoredPresence.body.parentProcessId === startup.parentPid,
      "The startup process evidence and real report identified different Copilot parents",
    );
    assert(
      unmonitoredPresence.method === "POST"
      && unmonitoredPresence.path === "/api/session/activity",
      "The real reporting extension used an unexpected unmonitored endpoint request",
    );

    await waitForValue(
      "the unavailable owning endpoint to receive a retry",
      15_000,
      () => ownerConnections.length >= 2 ? [...ownerConnections] : undefined,
      () => JSON.stringify({
        ownerConnections,
        extensionLog: tail(startup.path),
      }),
    );
    assert(
      unmonitoredRequests.length === 1,
      `The terminal unmonitored endpoint received ${unmonitoredRequests.length} requests before recovery`,
    );

    const startupProcesses = processRows({ rootPid: manifest.pid });
    const extensionProcess = startupProcesses.find(
      (row) => row.pid === startup.extensionPid,
    );
    assert(
      extensionProcess?.parentPid === startup.parentPid,
      `Reporting extension PID ${startup.extensionPid} was not a live child of Copilot PID ${startup.parentPid}`,
    );
    capturedIdentities = [
      {
        pid: manifest.pid,
        startTicks: manifest.processStartTimeUtcTicks,
      },
      ...descendantRows(manifest.pid, startupProcesses)
        .filter((row) => row.startTicks > 0)
        .map((row) => ({ pid: row.pid, startTicks: row.startTicks })),
    ];

    await closeServer(outageGate);
    outageGate = undefined;

    server = startProcess(
      serverExecutable,
      [repository, "--port", String(serverPort), "--no-canvas"],
      {
        cwd: runtimeDirectory,
        env: isolatedEnvironment({
          TREEMON_CONFIG_DIR: configDirectory,
          TREEMON_TERMINAL_HOST_STATE_DIR: hostStateDirectory,
          TREEMON_TERMINAL_HOST_EXECUTABLE: hostExecutable,
        }),
      },
    );
    await waitForServer(serverPort, server);

    const database = join(
      runtimeDirectory,
      "data",
      `session-activity-${serverPort}.db`,
    );
    const observed = await waitForObservedInstance(
      database,
      terminal.sessionId,
      server,
    );
    assert(
      observed.processId === startup.parentPid,
      `Persisted process ${observed.processId} did not match real Copilot PID ${startup.parentPid}`,
    );
    assert(
      observed.terminalSessionId === terminal.sessionId,
      "Persisted process instance lost its exact terminal origin",
    );
    assert(
      observed.closedAt === null,
      "Recovered real CLI process instance was already closed",
    );

    const eventCount = sqliteRows(
      database,
      "SELECT COUNT(*) AS count FROM activity_events;",
    )[0]?.count;
    assert(
      Number(eventCount) > 0,
      "The recovered real CLI did not replay any activity events",
    );
    await delay(1_000);
    assert(
      unmonitoredRequests.length === 1,
      `The terminal unmonitored endpoint retried after its definitive rejection (${unmonitoredRequests.length} requests)`,
    );

    const killSessionResult = await callKillSession(serverPort, worktree);
    killSessionCompleted = true;
    await hostRequest(
      manifest,
      "DELETE",
      `/api/v2/terminals/${encodeURIComponent(terminal.sessionId)}`,
    );
    await waitForValue(
      "the isolated terminal registry to become empty",
      20_000,
      async () => {
        const registry = await hostRequest(
          manifest,
          "GET",
          "/api/v2/terminals",
        );
        return registry.terminals.length === 0 ? registry : undefined;
      },
    );
    terminal = undefined;

    console.log(
      `PASS: real Copilot reporting recovered after the owning endpoint outage without another prompt`,
    );
    console.log(
      `EVIDENCE=${JSON.stringify({
        fixture,
        copilotHome,
        extensionLog: startup.path,
        extensionPid: startup.extensionPid,
        copilotPid: startup.parentPid,
        terminalOrigin: startup.terminalOrigin,
        extensionEndpointCount: startup.endpointCount,
        copilotPromptCount: 1,
        terminalSessionId: observed.terminalSessionId,
        processStartTicks: observed.processStartTicks,
        sessionId: observed.sessionId,
        ownerPort: serverPort,
        unmonitoredPort,
        controlPort,
        ownerConnectionAttempts: ownerConnections.length,
        unmonitoredRequestCount: unmonitoredRequests.length,
        activityEventCount: Number(eventCount),
        killSessionResult,
      })}`,
    );
  } catch (error) {
    scenarioError = error;
  } finally {
    if (host && capturedIdentities.length === 0) {
      try {
        const rows = processRows({ rootPid: host.child.pid });
        capturedIdentities = [
          {
            pid: manifest?.pid ?? host.child.pid,
            startTicks:
              manifest?.processStartTimeUtcTicks
              ?? rows.find((row) => row.pid === host.child.pid)?.startTicks
              ?? 0,
          },
          ...descendantRows(host.child.pid, rows)
            .filter((row) => row.startTicks > 0)
            .map((row) => ({ pid: row.pid, startTicks: row.startTicks })),
        ].filter((identity) => identity.startTicks > 0);
      } catch (error) {
        cleanupErrors.push(
          `pre-cleanup process capture failed: ${error.message}`,
        );
      }
    }

    if (server && serverPort && !killSessionCompleted) {
      try {
        await callKillSession(serverPort, worktree);
      } catch (error) {
        cleanupErrors.push(`killSession cleanup failed: ${error.message}`);
      }
    }

    if (terminal && manifest) {
      try {
        await hostRequest(
          manifest,
          "DELETE",
          `/api/v2/terminals/${encodeURIComponent(terminal.sessionId)}`,
        );
      } catch (error) {
        cleanupErrors.push(`terminal cleanup failed: ${error.message}`);
      }
    }

    try {
      await terminalBrowser?.close();
    } catch (error) {
      cleanupErrors.push(`terminal browser cleanup failed: ${error.message}`);
    }

    try {
      await stopProcess(server);
    } catch (error) {
      cleanupErrors.push(`server cleanup failed: ${error.message}`);
    }

    try {
      await shutdownHost(host, manifest);
    } catch (error) {
      cleanupErrors.push(`host cleanup failed: ${error.message}`);
    }

    try {
      await closeServer(outageGate);
    } catch (error) {
      cleanupErrors.push(`outage gate cleanup failed: ${error.message}`);
    }

    try {
      await closeServer(unmonitoredServer);
    } catch (error) {
      cleanupErrors.push(`unmonitored endpoint cleanup failed: ${error.message}`);
    }

    if (capturedIdentities.length > 0) {
      try {
        const survivors = exactSurvivors(
          capturedIdentities,
          processRows({
            processIds: capturedIdentities.map((identity) => identity.pid),
          }),
        );
        if (survivors.length > 0) {
          cleanupErrors.push(
            `captured exact process identities survived: ${JSON.stringify(survivors)}`,
          );
        }
      } catch (error) {
        cleanupErrors.push(`survivor verification failed: ${error.message}`);
      }
    }

    try {
      rmSync(fixture, { recursive: true, force: true });
    } catch (error) {
      cleanupErrors.push(`fixture cleanup failed: ${error.message}`);
    }
  }

  if (scenarioError && cleanupErrors.length > 0) {
    throw new Error(
      `Scenario failed: ${scenarioError.message}\nCleanup also failed:\n${cleanupErrors.join("\n")}`,
      { cause: scenarioError },
    );
  }
  if (scenarioError) throw scenarioError;
  if (cleanupErrors.length > 0) {
    throw new Error(`Cleanup failed:\n${cleanupErrors.join("\n")}`);
  }
}

await runRealCliReportingVerification();
