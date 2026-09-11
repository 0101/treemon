import { randomUUID } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { createServer as createHttpServer } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { DatabaseSync } from "node:sqlite";
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync,
  rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const sourceRoot = resolve(import.meta.dirname, "..");
const protectedPorts = new Set([5000, 5001, 5002, 5174]);
const prompt =
  "Reply with exactly PRESENCE_READY. Do not use tools or modify files.";

/// Report kinds whose durable proof is the session_instances row; every other kind also appends an
/// activity_events row (SessionActivityIngestion.applyKnownReport).
const instanceOnlyKinds = new Set([
  "session_present",
  "heartbeat",
  "title_bootstrap",
  "usage_info",
  "session_closed",
]);
const knownStatuses = new Set(["working", "waiting_for_user", "idle"]);

const expectedOutcome = {
  promptsSubmitted: 1,
  retriedWhileOwnerUnavailable: true,
  distinctPresenceEventIds: 1,
  presenceRetriedAfterWithheldAcknowledgement: true,
  presenceAcknowledgementsForwardedAfterDurability: true,
  withheldPresenceAcknowledgements: 1,
  withheldEventAcknowledgements: 1,
  replayedPersistedEvents: true,
  durableInstances: 1,
  durableSessionIdPresent: true,
  durableStatusRecognized: true,
  duplicateEventRows: 0,
  failedDeliveries: 0,
  unexpectedEndpointRequests: 0,
  terminalRegistryCount: 0,
  survivors: [],
  hostManifestExists: false,
  fixtureRemoved: true,
};

const delay = (milliseconds) => new Promise(
  (resolveDelay) => setTimeout(resolveDelay, milliseconds),
);

const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};

const parseJson = (text) => {
  try {
    return text ? JSON.parse(text) : undefined;
  } catch {
    return undefined;
  }
};

/// Key-order independent JSON so evidence records compare by content.
const normalized = (value) =>
  JSON.stringify(value ?? null, (_, item) =>
    item && typeof item === "object" && !Array.isArray(item)
      ? Object.fromEntries(
        Object.entries(item).sort(([left], [right]) => left.localeCompare(right)),
      )
      : item);

const describe = (value) => (normalized(value) ?? "").slice(0, 2_000);

const assertExactIdentity = (label, actual, expected) =>
  assert(
    normalized(actual) === normalized(expected),
    `${label} mismatch.\n  actual:   ${normalized(actual)}\n  expected: ${normalized(expected)}`,
  );

async function expectEventually(label, collect, predicate, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  // Polling records the newest observation so a timeout can explain what it saw.
  let observed;
  let lastError;

  while (Date.now() < deadline) {
    try {
      observed = await collect();
      if (predicate(observed)) return observed;
    } catch (error) {
      lastError = error;
    }
    await delay(100);
  }

  throw new Error(
    `Timed out waiting for ${label}.`
    + (lastError ? ` Last error: ${lastError.message}.` : "")
    + ` Last observed: ${describe(observed)}`,
  );
}

const commandExists = (command) =>
  spawnSync("where.exe", [command], {
    stdio: "ignore",
    windowsHide: true,
  }).status === 0;

function run(fileName, arguments_, options = {}) {
  const result = spawnSync(fileName, arguments_, {
    cwd: options.cwd ?? sourceRoot,
    encoding: "utf8",
    windowsHide: true,
  });

  assert(
    result.status === 0,
    `${fileName} ${arguments_.join(" ")} failed with exit code ${result.status}: ${(result.stderr || result.stdout).trim()}`,
  );
}

const appendTail = (current, chunk, maximum = 32_768) =>
  `${current}${chunk}`.slice(-maximum);

function startProcess(fileName, arguments_, options) {
  const child = spawn(fileName, arguments_, {
    cwd: options.cwd,
    env: options.env,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  // Console output arrives asynchronously; a bounded tail is the smallest state a listener can keep.
  let output = { stdout: "", stderr: "", error: undefined };

  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    output = { ...output, stdout: appendTail(output.stdout, chunk) };
  });
  child.stderr.on("data", (chunk) => {
    output = { ...output, stderr: appendTail(output.stderr, chunk) };
  });
  child.once("error", (error) => {
    output = { ...output, error };
  });

  return {
    child,
    closed: new Promise((resolveClosed) => child.once("close", resolveClosed)),
    snapshot: () => output,
  };
}

function assertRunning(label, process_) {
  const { error, stdout, stderr } = process_.snapshot();

  if (error) throw error;
  assert(
    process_.child.exitCode === null,
    `${label} exited with code ${process_.child.exitCode}: ${(stderr || stdout).slice(-1_000)}`,
  );
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

async function allocatePort() {
  const port = await new Promise((resolvePort, reject) => {
    const server = createTcpServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port: bound } = server.address();
      server.close(() => resolvePort(bound));
    });
  });

  return protectedPorts.has(port) ? await allocatePort() : port;
}

async function allocateBoundServer(start) {
  const port = await allocatePort();

  try {
    return { port, server: await start(port) };
  } catch (error) {
    if (error?.code !== "EADDRINUSE") throw error;
    return await allocateBoundServer(start);
  }
}

async function listen(server, port) {
  await new Promise((resolveListen, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolveListen);
  });
  return server;
}

async function closeServer(server) {
  if (!server?.listening) return;
  await new Promise((resolveClose, reject) => {
    server.close((error) => (error ? reject(error) : resolveClose()));
  });
}

function isolatedEnvironment(overrides) {
  const excluded = new Set(
    [
      "COPILOT_HOME",
      "TREEMON_PORT",
      "TREEMON_CONFIG_DIR",
      "TREEMON_TERMINAL_HOST_STATE_DIR",
      "TREEMON_TERMINAL_HOST_EXECUTABLE",
      "TREEMON_TERMINAL_SESSION_ID",
    ].map((name) => name.toLowerCase()),
  );

  return {
    ...Object.fromEntries(
      Object.entries(process.env).filter(
        ([name]) => !excluded.has(name.toLowerCase()),
      ),
    ),
    ...overrides,
  };
}

function initializeWorktree(repository, worktree) {
  mkdirSync(repository, { recursive: true });
  run("git", ["init", "--quiet", "-b", "main", repository]);
  [
    ["user.name", "Treemon Verification"],
    ["user.email", "treemon-verification@example.invalid"],
    ["commit.gpgsign", "false"],
  ].forEach(([name, value]) =>
    run("git", ["config", name, value], { cwd: repository })
  );
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
  const destination = join(copilotHome, "extensions", "treemon-reporting");
  mkdirSync(destination, { recursive: true });

  readdirSync(source)
    .filter((name) => name.endsWith(".mjs") || name === "package.json")
    .forEach((name) => copyFileSync(join(source, name), join(destination, name)));
}

async function readRequestBody(request) {
  const chunks = [];
  let length = 0;

  for await (const chunk of request) {
    length += chunk.length;
    if (length > 1_048_576) {
      throw new Error("Verification request exceeded 1 MiB");
    }
    chunks.push(chunk);
  }

  return Buffer.concat(chunks).toString("utf8");
}

async function waitForManifest(stateDirectory, host) {
  const manifestPath = join(stateDirectory, "host.json");

  return await expectEventually(
    "the isolated TerminalHost manifest",
    () => {
      assertRunning("TerminalHost", host);
      return existsSync(manifestPath)
        ? parseJson(readFileSync(manifestPath, "utf8"))
        : undefined;
    },
    (manifest) => Boolean(manifest?.endpoint),
    15_000,
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

  return parseJson(text);
}

const ansiPattern =
  // OSC sequences, then CSI sequences, then two-character escapes.
  /\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b\[[0-9;?]*[\x20-\x2f]*[\x40-\x7e]|\u001b[\x20-\x2f]*[\x30-\x7e]/g;

/// Attaches to the isolated terminal over the ttyd data plane so the verification can read rendered
/// output and type into the real Copilot CLI without driving a browser.
async function attachTerminal(attachmentEndpoint) {
  const endpoint = new URL(attachmentEndpoint);
  endpoint.protocol = "ws:";
  endpoint.pathname = `${endpoint.pathname}ws`;

  const socket = new WebSocket(endpoint, "tty");
  socket.binaryType = "arraybuffer";
  // Terminal frames arrive asynchronously; the listener keeps a bounded tail of the output stream.
  let screen = "";

  socket.addEventListener("message", (event) => {
    const frame = Buffer.from(event.data);
    if (frame.length > 0 && frame[0] === 0x30) {
      screen = appendTail(screen, frame.subarray(1).toString("utf8"));
    }
  });

  await new Promise((resolveOpen, reject) => {
    const timeout = setTimeout(
      () => reject(new Error("Timed out opening the terminal attachment socket")),
      10_000,
    );
    const settle = (action) => {
      clearTimeout(timeout);
      action();
    };
    socket.addEventListener(
      "open",
      () => settle(resolveOpen),
      { once: true },
    );
    socket.addEventListener(
      "error",
      () => settle(() => reject(new Error("Could not attach to the isolated terminal"))),
      { once: true },
    );
  });
  socket.send(Buffer.from('{"AuthToken":"","columns":160,"rows":50}', "utf8"));

  return {
    screen: () => screen.replaceAll(ansiPattern, ""),
    type: (text) => socket.send(Buffer.from(`0${text}`, "utf8")),
    close: () => socket.close(1000, "Verification finished"),
  };
}

function filesBelow(directory, depth = 2) {
  if (!existsSync(directory) || depth < 0) return [];

  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? filesBelow(path, depth - 1) : [path];
  });
}

function tail(path, maximum = 4_096) {
  return existsSync(path) ? readFileSync(path, "utf8").slice(-maximum) : "";
}

function parseExtensionStartup(copilotHome) {
  const pattern =
    /\[treemon-reporting\] startup pid=(\d+) parentPid=(\d+) terminalOrigin=(present|absent) endpointPort=(\d+)/;
  const found = filesBelow(join(copilotHome, "logs", "extensions"), 1)
    .filter((path) => /treemon-reporting/i.test(path))
    .sort((left, right) => statSync(right).mtimeMs - statSync(left).mtimeMs)
    .map((path) => ({ path, match: pattern.exec(tail(path)) }))
    .find((log) => log.match);

  if (!found) return undefined;
  const [, extensionPid, parentPid, terminalOrigin, endpointPort] = found.match;
  return {
    path: found.path,
    extensionPid: Number(extensionPid),
    parentPid: Number(parentPid),
    terminalOrigin,
    endpointPort: Number(endpointPort),
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

/// Exact identities (PID plus start ticks) of the root process and every descendant, so cleanup can
/// prove those exact processes are gone rather than trusting a recycled PID.
function captureIdentities(rootPid, rows) {
  const identity = (row) => ({ pid: row.pid, startTicks: row.startTicks });
  const root = rows.find((row) => row.pid === rootPid);

  return [
    ...(root ? [identity(root)] : []),
    ...descendantRows(rootPid, rows)
      .filter((row) => row.startTicks !== "0")
      .map(identity),
  ];
}

function exactSurvivors(captured, currentRows) {
  const currentByPid = new Map(currentRows.map((row) => [row.pid, row]));
  return captured.filter((identity) =>
    currentByPid.get(identity.pid)?.startTicks === identity.startTicks
  );
}

function sqliteRows(database, sql, parameters = []) {
  if (!existsSync(database)) return [];
  const connection = new DatabaseSync(database, { readOnly: true });

  try {
    return connection.prepare(sql).all(...parameters);
  } finally {
    connection.close();
  }
}

/// Process start ticks stay strings because Int64 tick values exceed exact JS number precision.
function storeSnapshot(database, terminalSessionId) {
  return {
    instances: sqliteRows(
      database,
      `SELECT
         process_id AS processId,
         CAST(process_start_ticks AS TEXT) AS processStartTicks,
         session_id AS sessionId,
         worktree_path AS worktreePath,
         status,
         terminal_session_id AS terminalSessionId,
         closed_at AS closedAt
       FROM session_instances
       WHERE terminal_session_id = ?
       ORDER BY last_seen DESC;`,
      [terminalSessionId],
    ),
    eventIds: sqliteRows(
      database,
      "SELECT event_id AS eventId FROM activity_events ORDER BY rowid;",
    ).map((row) => row.eventId),
    duplicateEventRows: sqliteRows(
      database,
      `SELECT event_id AS eventId, COUNT(*) AS rowCount
       FROM activity_events
       GROUP BY process_id, process_start_ticks, event_id
       HAVING COUNT(*) > 1;`,
    ),
  };
}

function summarizeDeliveries(deliveries, snapshot) {
  const presence = deliveries.filter(
    (delivery) => delivery.kind === "session_present",
  );
  const deliveredEventIds = deliveries
    .map((delivery) => delivery.eventId)
    .filter(Boolean);

  return {
    presenceDeliveries: presence.length,
    distinctPresenceEventIds: new Set(
      presence.map((delivery) => delivery.eventId),
    ).size,
    presenceAcknowledgementsForwarded: presence.filter(
      (delivery) => delivery.acknowledged && delivery.action === "forwarded",
    ).length,
    withheldPresenceAcknowledgements: presence.filter(
      (delivery) => delivery.action === "withheld",
    ).length,
    withheldEventAcknowledgements: deliveries.filter(
      (delivery) =>
        delivery.action === "withheld" && delivery.kind !== "session_present",
    ).length,
    replayedPersistedEventIds: [...new Set(deliveredEventIds)].filter(
      (eventId) =>
        snapshot.eventIds.includes(eventId)
        && deliveredEventIds.filter((delivered) => delivered === eventId).length > 1,
    ).length,
    failedDeliveries: deliveries.filter((delivery) => delivery.action === "failed")
      .length,
    unexpectedEndpointRequests: deliveries.filter(
      (delivery) => delivery.unexpectedEndpoint,
    ).length,
    durableInstances: snapshot.instances.length,
    activityEvents: snapshot.eventIds.length,
    duplicateEventRows: snapshot.duplicateEventRows.length,
  };
}

/// Stands in for the owning Treemon endpoint: it accepts only the activity endpoint, forwards each
/// report to the isolated server, releases an acknowledgement only once the exact row is durably
/// readable, and withholds the first presence and the first event acknowledgement so the real
/// extension has to retry them.
async function startOwningProxy(
  port,
  { backendPort, database, terminalSessionId, deliveries },
) {
  // The handler cannot thread state between connections; the withheld counts are that state.
  let withheldPresence = 0;
  let withheldEvent = 0;

  const isDurable = (snapshot, report) =>
    snapshot.instances.length > 0
    && (instanceOnlyKinds.has(report.kind)
      || snapshot.eventIds.includes(report.eventId));

  const server = createHttpServer(async (request, response) => {
    const unexpectedEndpoint =
      request.method !== "POST" || request.url !== "/api/session/activity";

    try {
      assert(
        !unexpectedEndpoint,
        `Unexpected owning endpoint request ${request.method} ${request.url}`,
      );

      const report = JSON.parse(await readRequestBody(request));
      const backendResponse = await fetch(
        `http://127.0.0.1:${backendPort}${request.url}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(report),
        },
      );
      const backendText = await backendResponse.text();
      const acknowledgement = parseJson(backendText);
      const acknowledged = acknowledgement?.recorded === true;

      if (acknowledged) {
        await expectEventually(
          `the acknowledged ${report.kind} report to become durable`,
          () => storeSnapshot(database, terminalSessionId),
          (snapshot) => isDurable(snapshot, report),
          3_000,
        );
      }

      const withhold = acknowledged
        && (report.kind === "session_present"
          ? withheldPresence === 0
          : withheldEvent === 0 && !instanceOnlyKinds.has(report.kind));

      if (withhold && report.kind === "session_present") withheldPresence += 1;
      else if (withhold) withheldEvent += 1;

      deliveries.push({
        kind: report.kind,
        eventId: report.eventId,
        sessionId: report.sessionId,
        parentProcessId: report.parentProcessId,
        terminalSessionId: report.terminalSessionId,
        acknowledged,
        action: withhold ? "withheld" : "forwarded",
      });

      if (withhold) {
        response.destroy();
        return;
      }

      response.writeHead(backendResponse.status, {
        "Content-Type": backendResponse.headers.get("content-type")
          ?? "application/json",
      });
      response.end(backendText);
    } catch (error) {
      deliveries.push({
        action: "failed",
        unexpectedEndpoint,
        reason: error.message,
      });
      if (response.headersSent) {
        response.destroy();
      } else {
        response.writeHead(500, { "Content-Type": "application/json" });
        response.end(JSON.stringify({
          recorded: false,
          monitored: true,
          retryable: true,
          reason: "verification proxy failed",
        }));
      }
    }
  });

  return await listen(server, port);
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

  assert(response.ok, `killSession returned HTTP ${response.status}: ${text}`);
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

async function runRealCliReportingVerification() {
  assert(
    process.platform === "win32",
    "Real CLI reporting verification requires Windows",
  );
  assert(commandExists("copilot"), "GitHub Copilot CLI is not installed");

  const configuration = process.env.TREEMON_VERIFY_CONFIGURATION || "Release";
  const hostExecutable = join(
    sourceRoot, "src", "TerminalHost", "bin", configuration, "net10.0",
    "TerminalHost.exe",
  );
  const serverExecutable = join(
    sourceRoot, "src", "Server", "bin", configuration, "net10.0", "Treemon.exe",
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
  assert(ttydExecutable, "Missing ttyd.exe. Run '.\\treemon.ps1 setup-ttyd'.");

  const fixture = join(sourceRoot, ".agents", "real-cli-reporting", randomUUID());
  const repository = join(fixture, "repo");
  const worktree = join(fixture, "worktrees", "presence");
  const runtimeDirectory = join(fixture, "runtime");
  const configDirectory = join(fixture, "config");
  const hostStateDirectory = join(fixture, "terminal-host-state");
  const copilotHome = join(fixture, "copilot-home");
  const ownerConnections = [];
  const deliveries = [];
  const cleanupErrors = [];
  const teardown = {
    terminalRegistryCount: undefined,
    survivors: undefined,
    hostManifestExists: true,
    fixtureRemoved: false,
  };
  const cleanupStep = async (label, action) => {
    try {
      await action();
    } catch (error) {
      cleanupErrors.push(`${label}: ${error.message}`);
    }
  };
  // Scenario resources are discovered step by step and have to stay reachable from cleanup.
  let outageGate;
  let ownerProxy;
  let host;
  let manifest;
  let terminal;
  let terminalSocket;
  let server;
  let ownerPort;
  let backendPort;
  let capturedIdentities = [];
  let scenario;
  let scenarioError;

  try {
    [runtimeDirectory, configDirectory, hostStateDirectory].forEach(
      (directory) => mkdirSync(directory, { recursive: true }),
    );
    initializeWorktree(repository, worktree);
    installReportingExtension(copilotHome);

    const gate = await allocateBoundServer((port) =>
      listen(
        createTcpServer((socket) => {
          ownerConnections.push(Date.now());
          socket.destroy();
        }),
        port,
      )
    );
    ownerPort = gate.port;
    outageGate = gate.server;

    host = startProcess(
      hostExecutable,
      [
        "--state-dir", hostStateDirectory,
        "--port", "0",
        "--ttyd", ttydExecutable,
        "--shell", "pwsh",
        "--allowed-origin", `http://127.0.0.1:${ownerPort}`,
      ],
      {
        cwd: worktree,
        env: isolatedEnvironment({
          COPILOT_HOME: copilotHome,
          COPILOT_AUTO_UPDATE: "false",
          TREEMON_PORT: String(ownerPort),
          TREEMON_TERMINAL_HOST_STATE_DIR: hostStateDirectory,
        }),
      },
    );
    manifest = await waitForManifest(hostStateDirectory, host);
    const controlPort = Number(new URL(manifest.endpoint).port);

    const created = await hostRequest(manifest, "POST", "/api/v2/terminals", {
      worktreePath: worktree,
    });
    assert(
      created.terminals.length === 1,
      `Expected one isolated terminal, found ${created.terminals.length}`,
    );
    terminal = created.terminals[0];
    const attachmentPort = Number(new URL(terminal.attachmentEndpoint).port);

    terminalSocket = await attachTerminal(terminal.attachmentEndpoint);
    terminalSocket.type(
      `copilot --experimental --yolo --log-level all -i '${prompt.replaceAll("'", "''")}'\r`,
    );
    const promptsSubmitted = 1;

    await expectEventually(
      "the isolated Copilot folder-trust confirmation",
      () => terminalSocket.screen(),
      (screen) =>
        screen.includes("Confirm folder trust")
        && screen.includes("Do you trust the files in this folder?"),
      20_000,
    );
    terminalSocket.type("\r");

    const { startup } = await expectEventually(
      "the real Copilot reporting extension process",
      () => {
        assertRunning("TerminalHost", host);
        return {
          startup: parseExtensionStartup(copilotHome),
          screen: terminalSocket.screen().slice(-1_500),
        };
      },
      (observed) => Boolean(observed.startup),
      45_000,
    );

    await expectEventually(
      "the unavailable owning endpoint to receive a retry",
      () => ({
        connectionAttempts: ownerConnections.length,
        extensionLog: tail(startup.path, 1_500),
      }),
      (observed) => observed.connectionAttempts >= 2,
      15_000,
    );

    const startupProcesses = processRows({ rootPid: manifest.pid });
    const copilotProcess = startupProcesses.find(
      (row) => row.pid === startup.parentPid,
    );
    const extensionProcess = startupProcesses.find(
      (row) => row.pid === startup.extensionPid,
    );
    assert(copilotProcess, `Copilot PID ${startup.parentPid} was not observable`);
    assert(
      extensionProcess,
      `Reporting extension PID ${startup.extensionPid} was not observable`,
    );
    capturedIdentities = captureIdentities(manifest.pid, startupProcesses);
    assert(
      capturedIdentities.some((identity) => identity.pid === manifest.pid),
      `TerminalHost PID ${manifest.pid} was not observable`,
    );

    assertExactIdentity(
      "reporting extension origin",
      {
        terminalOrigin: startup.terminalOrigin,
        endpointPort: startup.endpointPort,
        extensionParentPid: extensionProcess.parentPid,
      },
      {
        terminalOrigin: "present",
        endpointPort: ownerPort,
        extensionParentPid: startup.parentPid,
      },
    );

    backendPort = await allocatePort();
    const ports = {
      owner: ownerPort,
      control: controlPort,
      attachment: attachmentPort,
      backend: backendPort,
    };
    const portValues = Object.values(ports);
    assertExactIdentity(
      "isolated non-production ports",
      {
        invalid: portValues.filter(
          (value) => !Number.isInteger(value) || value <= 0,
        ),
        protected: portValues.filter((value) => protectedPorts.has(value)),
        distinct: new Set(portValues).size,
      },
      { invalid: [], protected: [], distinct: portValues.length },
    );

    server = startProcess(
      serverExecutable,
      [repository, "--port", String(backendPort), "--no-canvas"],
      {
        cwd: runtimeDirectory,
        env: isolatedEnvironment({
          TREEMON_CONFIG_DIR: configDirectory,
          TREEMON_TERMINAL_HOST_STATE_DIR: hostStateDirectory,
          TREEMON_TERMINAL_HOST_EXECUTABLE: hostExecutable,
        }),
      },
    );
    await expectEventually(
      "the isolated Treemon server",
      async () => {
        assertRunning("Treemon", server);
        const response = await fetch(
          `http://127.0.0.1:${backendPort}/IWorktreeApi/getRoots`,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: "[null]",
          },
        );
        return response.ok;
      },
      (reachable) => reachable === true,
      45_000,
    );

    const database = join(
      runtimeDirectory, "data", `session-activity-${backendPort}.db`,
    );
    await closeServer(outageGate);
    outageGate = undefined;
    ownerProxy = await startOwningProxy(ownerPort, {
      backendPort,
      database,
      terminalSessionId: terminal.sessionId,
      deliveries,
    });

    await expectEventually(
      "presence and replayed reports to settle idempotently",
      () => {
        assertRunning("Treemon", server);
        return summarizeDeliveries(
          deliveries,
          storeSnapshot(database, terminal.sessionId),
        );
      },
      (summary) =>
        summary.withheldPresenceAcknowledgements === 1
        && summary.withheldEventAcknowledgements === 1
        && summary.presenceDeliveries >= 3
        && summary.presenceAcknowledgementsForwarded >= 2
        && summary.replayedPersistedEventIds >= 1
        && summary.durableInstances === 1
        && summary.activityEvents >= 1,
      60_000,
    );

    const snapshot = storeSnapshot(database, terminal.sessionId);
    const settled = summarizeDeliveries(deliveries, snapshot);
    const instance = snapshot.instances[0];
    const presence = deliveries.find(
      (delivery) => delivery.kind === "session_present",
    );

    assertExactIdentity(
      "durable exact process instance",
      {
        processId: instance.processId,
        processStartTicks: instance.processStartTicks,
        sessionId: instance.sessionId,
        terminalSessionId: instance.terminalSessionId,
        reportedParentProcessId: presence.parentProcessId,
        reportedTerminalSessionId: presence.terminalSessionId,
        closedAt: instance.closedAt,
      },
      {
        processId: startup.parentPid,
        processStartTicks: copilotProcess.startTicks,
        sessionId: presence.sessionId,
        terminalSessionId: terminal.sessionId,
        reportedParentProcessId: startup.parentPid,
        reportedTerminalSessionId: terminal.sessionId,
        closedAt: null,
      },
    );

    scenario = {
      outcome: {
        promptsSubmitted,
        retriedWhileOwnerUnavailable: ownerConnections.length >= 2,
        distinctPresenceEventIds: settled.distinctPresenceEventIds,
        presenceRetriedAfterWithheldAcknowledgement:
          settled.presenceDeliveries >= 3,
        presenceAcknowledgementsForwardedAfterDurability:
          settled.presenceAcknowledgementsForwarded >= 2,
        withheldPresenceAcknowledgements: settled.withheldPresenceAcknowledgements,
        withheldEventAcknowledgements: settled.withheldEventAcknowledgements,
        replayedPersistedEvents: settled.replayedPersistedEventIds >= 1,
        durableInstances: settled.durableInstances,
        durableSessionIdPresent: Boolean(instance.sessionId),
        durableStatusRecognized: knownStatuses.has(instance.status),
        duplicateEventRows: settled.duplicateEventRows,
        failedDeliveries: settled.failedDeliveries,
        unexpectedEndpointRequests: settled.unexpectedEndpointRequests,
      },
      detail: {
        fixture,
        database,
        extensionLog: startup.path,
        ports,
        processes: {
          terminalHostPid: manifest.pid,
          copilotPid: startup.parentPid,
          copilotStartTicks: copilotProcess.startTicks,
          extensionPid: startup.extensionPid,
          trackedProcesses: capturedIdentities.length,
        },
        instance,
        deliveries: settled,
        ownerConnectionAttempts: ownerConnections.length,
      },
    };
  } catch (error) {
    scenarioError = error;
  } finally {
    if (host && capturedIdentities.length === 0) {
      await cleanupStep("process capture", () => {
        capturedIdentities = captureIdentities(
          host.child.pid,
          processRows({ rootPid: host.child.pid }),
        );
      });
    }

    if (server && backendPort) {
      await cleanupStep("killSession", () =>
        callKillSession(backendPort, worktree)
      );
    }

    if (terminal && manifest) {
      await cleanupStep("terminal removal", async () => {
        await hostRequest(
          manifest,
          "DELETE",
          `/api/v2/terminals/${encodeURIComponent(terminal.sessionId)}`,
        );
        const registry = await expectEventually(
          "the isolated terminal registry to become empty",
          () => hostRequest(manifest, "GET", "/api/v2/terminals"),
          (observed) => observed.terminals.length === 0,
          20_000,
        );
        teardown.terminalRegistryCount = registry.terminals.length;
      });
    }

    await cleanupStep("terminal socket", () => terminalSocket?.close());
    await cleanupStep("owning proxy", () => closeServer(ownerProxy));
    await cleanupStep("server", () => stopProcess(server));
    await cleanupStep("host shutdown", async () => {
      await shutdownHost(host, manifest);
      teardown.hostManifestExists = existsSync(
        join(hostStateDirectory, "host.json"),
      );
    });
    await cleanupStep("outage gate", () => closeServer(outageGate));

    if (capturedIdentities.length > 0) {
      await cleanupStep("survivor verification", () => {
        teardown.survivors = exactSurvivors(
          capturedIdentities,
          processRows({
            processIds: capturedIdentities.map((identity) => identity.pid),
          }),
        );
      });
    }

    await cleanupStep("fixture removal", () => {
      rmSync(fixture, { recursive: true, force: true });
      teardown.fixtureRemoved = !existsSync(fixture);
    });
  }

  if (scenarioError) {
    throw new Error(
      `${scenarioError.message}\nTeardown: ${describe(teardown)}`
      + (cleanupErrors.length > 0
        ? `\nCleanup also failed:\n${cleanupErrors.join("\n")}`
        : ""),
      { cause: scenarioError },
    );
  }
  assert(
    cleanupErrors.length === 0,
    `Cleanup failed:\n${cleanupErrors.join("\n")}`,
  );

  const outcome = { ...scenario.outcome, ...teardown };
  assertExactIdentity(
    "real CLI reporting verification outcome",
    outcome,
    expectedOutcome,
  );
  console.log(
    "PASS: real Copilot reporting recovered after the owning endpoint outage without another prompt",
  );
  console.log(`EVIDENCE=${JSON.stringify({ outcome, ...scenario.detail })}`);
}

await runRealCliReportingVerification();
