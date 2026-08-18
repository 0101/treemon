import { spawn } from "node:child_process";
import { randomBytes, timingSafeEqual } from "node:crypto";
import { once } from "node:events";
import {
  appendFileSync,
  existsSync,
  linkSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { createServer as createHttpServer, request as httpRequest } from "node:http";
import { createServer as createNetServer } from "node:net";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { pathToFileURL } from "node:url";
import { WebSocket, WebSocketServer } from "ws";

export const hostProtocolVersion = 2;
export const defaultReplayBytes = 1024 * 1024;
export const defaultDiagnosticBytes = 1024 * 1024;

const defaultColumns = 120;
const defaultRows = 30;
const maxColumns = 1000;
const maxRows = 500;
const maxControlBodyBytes = 64 * 1024;
const maxBrowserFrameBytes = 64 * 1024;
const pingIntervalMs = 30_000;
const heartbeatIntervalMs = 60_000;
const heartbeatFailureMs = 90_000;
const gracefulProcessExitMs = 5000;
const forcedProcessExitMs = 2000;
const processForceCommandMs = 5000;
const reservationLeaseMs = 5 * 60_000;
const maximumOwnedProcesses = 1024;
const unixEpochTicks = 621355968000000000n;

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const timestamp = () => new Date().toISOString();

const randomToken = (bytes = 24) => randomBytes(bytes).toString("base64url");

const currentProcessStartTicks = () =>
  (
    unixEpochTicks +
    BigInt(Math.round(Date.now() - process.uptime() * 1000)) * 10_000n
  ).toString();

const safeInteger = (value, fallback, maximum) =>
  Number.isInteger(value) && value > 0 && value <= maximum ? value : fallback;

export function terminalSize(value) {
  return {
    columns: safeInteger(value?.columns, defaultColumns, maxColumns),
    rows: safeInteger(value?.rows, defaultRows, maxRows),
  };
}

export function parseInitialHandshake(data) {
  const text = Buffer.from(data).toString("utf8");
  const parsed = JSON.parse(text);

  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("Terminal handshake must be a JSON object");
  }

  return terminalSize(parsed);
}

export function parseResizeFrame(data) {
  const frame = Buffer.from(data);

  if (frame[0] !== "1".charCodeAt(0)) {
    throw new Error("Terminal resize frame must start with command 1");
  }

  return terminalSize(JSON.parse(frame.subarray(1).toString("utf8")));
}

export function resizeFrame(size) {
  const normalized = terminalSize(size);
  return Buffer.from(`1${JSON.stringify(normalized)}`, "utf8");
}

export function emptyReplayBuffer() {
  return { frames: [], bytes: 0, nextSequence: 0, droppedBytes: 0 };
}

const trimReplayFrames = (frames, bytes, maximumBytes, droppedBytes) => {
  if (bytes <= maximumBytes || frames.length <= 1) {
    return { frames, bytes, droppedBytes };
  }

  const [first, ...remaining] = frames;
  return trimReplayFrames(
    remaining,
    bytes - first.data.length,
    maximumBytes,
    droppedBytes + first.data.length,
  );
};

export function appendReplayFrame(replay, data, maximumBytes = defaultReplayBytes) {
  const copied = Buffer.from(data);
  const sequence = replay.nextSequence;
  const bounded =
    copied.length <= maximumBytes
      ? copied
      : Buffer.concat([
          Buffer.from("0"),
          copied.subarray(copied.length - Math.max(0, maximumBytes - 1)),
        ]);
  const droppedOversizedBytes = copied.length - bounded.length;
  const appended = [
    ...replay.frames,
    { sequence, data: bounded },
  ];
  const trimmed = trimReplayFrames(
    appended,
    replay.bytes + bounded.length,
    maximumBytes,
    replay.droppedBytes + droppedOversizedBytes,
  );

  return {
    ...trimmed,
    nextSequence: sequence + 1,
  };
}

export function replayFramesFrom(replay, sequence) {
  return replay.frames.filter((frame) => frame.sequence >= sequence);
}

export function sanitizeMetadataText(value, maximumLength = 160) {
  return String(value ?? "")
    .replace(/[\u0000-\u001f\u007f]/g, " ")
    .slice(0, maximumLength);
}

export function sessionCookieName(sessionId) {
  return `treemon-terminal-${sessionId}`;
}

export function publicDiagnosticSession(session) {
  return {
    id: session.id,
    state: session.state,
    order: session.order,
    ttydPid: session.ttydPid ?? null,
    shellPid: session.shellPid ?? null,
    publicPort: session.publicPort ?? null,
    ttydPort: session.ttydPort ?? null,
    browserAttachments: session.attachment ? 1 : 0,
    upstreamOpenedAt: session.upstreamOpenedAt ?? null,
    upstreamClosedAt: session.upstreamClosedAt ?? null,
    upstreamCloseCode: session.upstreamCloseCode ?? null,
    upstreamCloseReason: session.upstreamCloseReason ?? null,
    lastPongAt: session.lastPongAt ?? null,
    replayBytes: session.replay?.bytes ?? 0,
    replayDroppedBytes: session.replay?.droppedBytes ?? 0,
  };
}

function atomicWriteJson(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  const temporaryPath = `${path}.${process.pid}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  renameSync(temporaryPath, path);
}

export function manifestOwnership(value) {
  const generation =
    typeof value?.generation === "string" && value.generation
      ? value.generation
      : null;
  const pid = Number.isInteger(value?.pid) && value.pid > 0 ? value.pid : null;
  const processStartTicks =
    typeof value?.processStartTicks === "string" &&
    /^\d+$/.test(value.processStartTicks)
      ? value.processStartTicks
      : null;

  return generation && pid && processStartTicks
    ? { generation, pid, processStartTicks }
    : null;
}

export function sameManifestOwner(left, right) {
  return (
    left?.generation === right?.generation &&
    left?.pid === right?.pid &&
    left?.processStartTicks === right?.processStartTicks
  );
}

export function removeManifestIfOwned(path, owner) {
  if (!existsSync(path)) return true;

  const claimedPath = `${path}.${owner.generation}.${process.pid}.${randomToken(6)}.reclaim`;
  try {
    renameSync(path, claimedPath);
  } catch (error) {
    if (error?.code === "ENOENT") return true;
    return false;
  }

  try {
    const current = JSON.parse(readFileSync(claimedPath, "utf8"));
    if (!sameManifestOwner(manifestOwnership(current), owner)) {
      if (!existsSync(path)) renameSync(claimedPath, path);
      return false;
    }
    rmSync(claimedPath);
    return true;
  } catch {
    if (existsSync(claimedPath) && !existsSync(path)) {
      try {
        renameSync(claimedPath, path);
      } catch {
        return false;
      }
    }
    return false;
  }
}

function writeManifestIfUnowned(path, manifest) {
  const owner = manifestOwnership(manifest);
  const candidatePath = `${path}.${owner.generation}.${process.pid}.owner`;

  try {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(
      candidatePath,
      `${JSON.stringify(manifest, null, 2)}\n`,
      "utf8",
    );
    linkSync(candidatePath, path);
    return;
  } catch (error) {
    if (error?.code !== "EEXIST") throw error;
  } finally {
    rmSync(candidatePath, { force: true });
  }

  let currentOwner = null;
  try {
    currentOwner = manifestOwnership(JSON.parse(readFileSync(path, "utf8")));
  } catch {
    // A malformed manifest is never safe for a new host to replace.
  }

  if (!sameManifestOwner(currentOwner, owner)) {
    throw new Error("Durable terminal host state is already owned by another generation");
  }

  atomicWriteJson(path, manifest);
}

class DiagnosticLog {
  constructor(path, maximumBytes) {
    this.path = path;
    this.maximumBytes = maximumBytes;
    mkdirSync(dirname(path), { recursive: true });
  }

  append(event) {
    appendFileSync(this.path, `${JSON.stringify(event)}\n`, "utf8");

    if (statSync(this.path).size <= this.maximumBytes) return;

    const content = readFileSync(this.path);
    const targetStart = Math.max(0, content.length - Math.floor(this.maximumBytes / 2));
    const firstNewline = content.indexOf(10, targetStart);
    const retained =
      firstNewline < 0 ? Buffer.alloc(0) : content.subarray(firstNewline + 1);
    const temporaryPath = `${this.path}.${process.pid}.tmp`;
    writeFileSync(temporaryPath, retained);
    renameSync(temporaryPath, this.path);
  }
}

function secureEqual(left, right) {
  const leftBuffer = Buffer.from(String(left ?? ""), "utf8");
  const rightBuffer = Buffer.from(String(right ?? ""), "utf8");

  return (
    leftBuffer.length === rightBuffer.length &&
    timingSafeEqual(leftBuffer, rightBuffer)
  );
}

function canonicalWorktreePath(path) {
  if (typeof path !== "string" || !isAbsolute(path)) {
    throw new Error("worktreePath must be an absolute path");
  }

  const canonical = resolve(path).replace(/[\\/]+$/, "");

  if (!existsSync(canonical) || !statSync(canonical).isDirectory()) {
    throw new Error("worktreePath must name an existing directory");
  }

  return canonical;
}

const worktreeKey = (path) =>
  process.platform === "win32" ? path.toLowerCase() : path;

async function freeLoopbackPort() {
  const server = createNetServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const { port } = server.address();
  await new Promise((resolveClose, rejectClose) =>
    server.close((error) => (error ? rejectClose(error) : resolveClose())),
  );
  return port;
}

async function listenLoopback(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

async function closeServer(server) {
  if (!server?.listening) return;
  await new Promise((resolveClose) => server.close(() => resolveClose()));
}

async function waitForTtyd(endpoint, child, spawnFailure, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  const probe = async () => {
    if (spawnFailure.error) throw spawnFailure.error;
    if (child.exitCode !== null) {
      throw new Error(`ttyd exited with code ${child.exitCode}`);
    }
    if (Date.now() >= deadline) {
      throw new Error("Timed out waiting for ttyd to become ready");
    }

    try {
      const response = await fetch(endpoint, {
        signal: AbortSignal.timeout(1000),
      });
      if (response.status < 500) return;
    } catch {
      if (spawnFailure.error) throw spawnFailure.error;
    }

    await delay(100);
    return probe();
  };

  return probe();
}

async function openUpstream(url, origin) {
  const socket = new WebSocket(url, ["tty"], {
    origin,
    maxPayload: maxBrowserFrameBytes,
  });

  try {
    await Promise.race([
      once(socket, "open"),
      once(socket, "error").then(([error]) => Promise.reject(error)),
      delay(5000).then(() => Promise.reject(new Error("Timed out opening ttyd WebSocket"))),
    ]);
  } catch (error) {
    socket.terminate();
    throw error;
  }

  return socket;
}

function readCookie(request, name) {
  return String(request.headers.cookie ?? "")
    .split(";")
    .map((part) => part.trim().split("="))
    .find(([cookieName]) => cookieName === name)?.[1];
}

function authorizedSessionRequest(request, session) {
  const url = new URL(request.url, `http://127.0.0.1:${session.publicPort}`);
  const queryCapability = url.searchParams.get("cap");
  const cookieCapability = readCookie(request, session.cookieName);
  const authorized =
    secureEqual(queryCapability, session.capability) ||
    secureEqual(cookieCapability, session.capability);

  return { authorized, url, setCookie: secureEqual(queryCapability, session.capability) };
}

function rejectSocket(socket, status = "404 Not Found") {
  socket.end(`HTTP/1.1 ${status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
}

function proxyHeaders(headers, ttydPort) {
  const forwarded = { ...headers, host: `127.0.0.1:${ttydPort}` };
  delete forwarded.authorization;
  delete forwarded.cookie;
  delete forwarded.origin;
  delete forwarded.referer;
  return forwarded;
}

function proxyHttpRequest(request, response, session, setCookie, targetUrl) {
  targetUrl.searchParams.delete("cap");
  const upstream = httpRequest(
    {
      host: "127.0.0.1",
      port: session.ttydPort,
      method: request.method,
      path: `${targetUrl.pathname}${targetUrl.search}`,
      headers: proxyHeaders(request.headers, session.ttydPort),
    },
    (upstreamResponse) => {
      const headers = { ...upstreamResponse.headers };
      if (setCookie) {
        headers["set-cookie"] = [
          `${session.cookieName}=${session.capability}; HttpOnly; SameSite=Strict; Path=/`,
        ];
      }
      response.writeHead(upstreamResponse.statusCode ?? 502, headers);
      upstreamResponse.pipe(response);
    },
  );

  upstream.on("error", () => {
    if (!response.headersSent) {
      response.writeHead(502, { "content-type": "text/plain; charset=utf-8" });
    }
    response.end("Terminal proxy could not reach ttyd.");
  });
  request.pipe(upstream);
}

function readJsonBody(request) {
  return new Promise((resolveBody, rejectBody) => {
    const chunks = [];
    let bytes = 0;
    let overLimit = false;

    request.on("data", (chunk) => {
      bytes += chunk.length;
      overLimit ||= bytes > maxControlBodyBytes;
      if (!overLimit) chunks.push(chunk);
    });
    request.on("end", () => {
      if (overLimit) {
        rejectBody(new Error("Request body exceeded the control limit"));
        return;
      }

      try {
        const text = Buffer.concat(chunks).toString("utf8");
        resolveBody(text ? JSON.parse(text) : {});
      } catch {
        rejectBody(new Error("Request body was not valid JSON"));
      }
    });
    request.on("error", rejectBody);
  });
}

function sendJson(response, statusCode, value) {
  const body = Buffer.from(JSON.stringify(value), "utf8");
  response.writeHead(statusCode, {
    "content-type": "application/json; charset=utf-8",
    "content-length": body.length,
    "cache-control": "no-store",
  });
  response.end(body);
}

class ControlError extends Error {
  constructor(statusCode, message) {
    super(message);
    this.statusCode = statusCode;
  }
}

const validPid = (pid) => Number.isInteger(pid) && pid > 0;

function isPidAlive(pid) {
  if (!validPid(pid)) return null;

  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code === "ESRCH" ? false : null;
  }
}

const runProcessQuery = (fileName, argumentsList) =>
  new Promise((resolveCommand, rejectCommand) => {
    const child = spawn(fileName, argumentsList, {
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    const stdout = [];
    const stderr = [];
    let outputBytes = 0;
    let settled = false;
    const finish = (result, error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      if (error) rejectCommand(error);
      else resolveCommand(result);
    };
    const collect = (target) => (chunk) => {
      outputBytes += chunk.length;
      if (outputBytes <= maxControlBodyBytes) target.push(chunk);
    };
    child.stdout.on("data", collect(stdout));
    child.stderr.on("data", collect(stderr));
    const timeout = setTimeout(() => {
      if (child.exitCode === null) child.kill();
      finish(null, new Error(`Timed out querying process ownership with ${fileName}`));
    }, processForceCommandMs);
    child.once("error", (error) => finish(null, error));
    child.once("exit", (code) => {
      if (outputBytes > maxControlBodyBytes) {
        finish(null, new Error("Process ownership query exceeded the output limit"));
      } else if (code === 0) {
        finish(Buffer.concat(stdout).toString("utf8"));
      } else {
        finish(
          null,
          new Error(
            `Process ownership query failed with code ${code}: ${sanitizeMetadataText(Buffer.concat(stderr).toString("utf8"), 240)}`,
          ),
        );
      }
    });
  });

const windowsProcessQuery = async (filter) => {
  const script = [
    "$ErrorActionPreference = 'Stop'",
    `$items = @(Get-CimInstance Win32_Process -Filter '${filter}')`,
    "$items | ForEach-Object {",
    "  '{0}|{1}|{2}' -f $_.ProcessId, $_.ParentProcessId, $_.CreationDate.ToUniversalTime().Ticks",
    "}",
  ].join("; ");
  const output = await runProcessQuery("powershell.exe", [
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    script,
  ]);

  return output
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      const [pidText, parentText, startText] = line.trim().split("|");
      const pid = Number.parseInt(pidText, 10);
      const parentPid = Number.parseInt(parentText, 10);
      if (!validPid(pid) || !Number.isInteger(parentPid) || !/^\d+$/.test(startText)) {
        throw new Error("Process ownership query returned an invalid identity");
      }
      return {
        pid,
        parentPid,
        startIdentity: `windows:${startText}`,
      };
    });
};

const linuxProcessIdentity = (pid) => {
  try {
    const stat = readFileSync(`/proc/${pid}/stat`, "utf8");
    const commandEnd = stat.lastIndexOf(")");
    if (commandEnd < 0) throw new Error("Linux process stat omitted its command");
    const fields = stat.slice(commandEnd + 2).trim().split(/\s+/);
    const parentPid = Number.parseInt(fields[1], 10);
    const startTime = fields[19];
    if (!Number.isInteger(parentPid) || !/^\d+$/.test(startTime)) {
      throw new Error("Linux process stat returned an invalid identity");
    }
    return {
      pid,
      parentPid,
      startIdentity: `linux:${startTime}`,
    };
  } catch (error) {
    if (error?.code === "ENOENT" || error?.code === "ESRCH") return null;
    throw error;
  }
};

export function sameProcessIdentity(left, right) {
  return Boolean(
    left &&
    right &&
    left?.pid === right?.pid &&
    left?.startIdentity === right?.startIdentity
  );
}

export function defaultProcessController() {
  const inspect =
    process.platform === "win32"
      ? async (pid) =>
          validPid(pid)
            ? (await windowsProcessQuery(`ProcessId = ${pid}`))[0] ?? null
            : null
      : process.platform === "linux"
        ? async (pid) => (validPid(pid) ? linuxProcessIdentity(pid) : null)
        : async () => {
            throw new Error(
              `Durable terminal ownership inspection is unsupported on ${process.platform}`,
            );
          };
  const children =
    process.platform === "win32"
      ? async (pid) =>
          validPid(pid)
            ? windowsProcessQuery(`ParentProcessId = ${pid}`)
            : []
      : process.platform === "linux"
        ? async (pid) => {
            if (!validPid(pid)) return [];
            try {
              return readFileSync(`/proc/${pid}/task/${pid}/children`, "utf8")
                .trim()
                .split(/\s+/)
                .filter(Boolean)
                .map((value) => Number.parseInt(value, 10))
                .filter(validPid)
                .map(linuxProcessIdentity)
                .filter(Boolean)
                .filter((identity) => identity.parentPid === pid);
            } catch (error) {
              if (error?.code === "ENOENT" || error?.code === "ESRCH") return [];
              throw error;
            }
          }
        : async () => {
            throw new Error(
              `Durable terminal descendant discovery is unsupported on ${process.platform}`,
            );
          };

  return {
    inspect,
    children,
    terminate: async (pid) => {
      if (!validPid(pid)) return;
      try {
        process.kill(pid, "SIGKILL");
      } catch (error) {
        if (error?.code !== "ESRCH") throw error;
      }
    },
  };
}

const processIdentityKey = (identity) =>
  `${identity.pid}:${identity.startIdentity}`;

const ownedProcesses = (session) =>
  session.ownedProcesses ?? new Map();

const trackedOwnedProcessIds = (session) =>
  [...ownedProcesses(session).values()].map(({ identity }) => identity.pid);

const trackOwnedProcess = (session, identity, depth) => {
  session.ownedProcesses ??= new Map();
  const key = processIdentityKey(identity);
  if (session.ownedProcesses.has(key)) return false;
  if (session.ownedProcesses.size >= maximumOwnedProcesses) {
    throw new Error("Owned terminal process set exceeded its safety bound");
  }
  session.ownedProcesses.set(key, { identity, depth });
  return true;
};

async function discoverOwnedDescendants(session, processController) {
  const discover = async (pending, visited) => {
    const [tracked, ...remaining] = pending;
    if (!tracked) return;
    const key = processIdentityKey(tracked.identity);
    if (visited.has(key)) return discover(remaining, visited);

    const actual = await processController.inspect(tracked.identity.pid);
    if (!sameProcessIdentity(actual, tracked.identity)) {
      return discover(remaining, new Set([...visited, key]));
    }

    const children = await processController.children(tracked.identity.pid);
    const captured = [];
    for (const candidate of children) {
      const verified = await processController.inspect(candidate.pid);
      if (
        sameProcessIdentity(candidate, verified) &&
        trackOwnedProcess(session, candidate, tracked.depth + 1)
      ) {
        captured.push({ identity: candidate, depth: tracked.depth + 1 });
      }
    }

    return discover(
      [...remaining, ...captured],
      new Set([...visited, key]),
    );
  };

  await discover([...ownedProcesses(session).values()], new Set());
}

async function remainingOwnedProcesses(session, processController) {
  const inspected = await Promise.all(
    [...ownedProcesses(session).values()].map(async (tracked) => ({
      tracked,
      actual: await processController.inspect(tracked.identity.pid),
    })),
  );
  return inspected
    .filter(({ tracked, actual }) =>
      sameProcessIdentity(tracked.identity, actual),
    )
    .map(({ tracked }) => tracked);
}

async function waitForOwnedProcessExit(
  session,
  processController,
  timeoutMs,
  wait,
) {
  const deadline = Date.now() + timeoutMs;

  const check = async () => {
    await discoverOwnedDescendants(session, processController);
    const remaining = await remainingOwnedProcesses(session, processController);
    if (remaining.length === 0 || Date.now() >= deadline) return remaining;
    await wait(Math.min(50, Math.max(1, deadline - Date.now())));
    return check();
  };

  return check();
}

async function forceOwnedProcessExit(
  session,
  processController,
  timeoutMs,
  wait,
) {
  const deadline = Date.now() + timeoutMs;

  const force = async (attempted) => {
    await discoverOwnedDescendants(session, processController);
    const remaining = await remainingOwnedProcesses(session, processController);
    if (remaining.length === 0 || (attempted && Date.now() >= deadline)) {
      return remaining;
    }

    for (const tracked of remaining.sort(
      (left, right) => left.depth - right.depth,
    )) {
      await discoverOwnedDescendants(session, processController);
      const actual = await processController.inspect(tracked.identity.pid);
      if (sameProcessIdentity(actual, tracked.identity)) {
        await processController.terminate(tracked.identity.pid);
      }
    }

    if (Date.now() < deadline) {
      await wait(Math.min(50, Math.max(1, deadline - Date.now())));
    }
    return force(true);
  };

  return force(false);
}

export class DurableTerminalHost {
  constructor(options) {
    this.options = options;
    this.startedAt = timestamp();
    if (
      options.generation &&
      !/^[A-Za-z0-9_-]{1,128}$/.test(options.generation)
    ) {
      throw new Error("Durable terminal host generation is invalid");
    }
    this.generation = options.generation ?? randomToken(16);
    this.processStartTicks =
      options.processStartTicks ?? currentProcessStartTicks();
    this.controlToken = randomToken();
    this.sessions = new Map();
    this.nextOrder = 0;
    this.shuttingDown = false;
    this.shutdownPromise = null;
    this.inFlightStarts = new Set();
    this.keyOperations = new Map();
    this.reservations = new Map();
    this.processController =
      options.processController ?? defaultProcessController();
    this.wait = options.wait ?? delay;
    this.now = options.now ?? (() => Date.now());
    this.reservationLeaseMs =
      options.reservationLeaseMs ?? reservationLeaseMs;
    this.cleanupTimeouts = {
      graceful: options.cleanupTimeouts?.graceful ?? gracefulProcessExitMs,
      forced: options.cleanupTimeouts?.forced ?? forcedProcessExitMs,
    };
    this.exitProcess = options.exitProcess ?? ((code) => process.exit(code));
    this.statePath = join(options.stateDirectory, "host.json");
    this.statusPath = join(options.stateDirectory, "status.json");
    this.lockPath = join(options.stateDirectory, "host.lock");
    this.diagnostics = new DiagnosticLog(
      join(options.stateDirectory, "diagnostics.jsonl"),
      options.diagnosticBytes,
    );
    this.controlServer = createHttpServer((request, response) => {
      this.handleControlRequest(request, response).catch((error) => {
        this.record("control-error", null, {
          errorType: sanitizeMetadataText(error?.name || "Error", 80),
        });
        if (!response.headersSent) {
          sendJson(response, error?.statusCode ?? 500, {
            error: error.message,
          });
        } else {
          response.end();
        }
      });
    });
  }

  manifestOwner() {
    return {
      generation: this.generation,
      pid: process.pid,
      processStartTicks: this.processStartTicks,
    };
  }

  manifest() {
    return {
      version: hostProtocolVersion,
      generation: this.generation,
      pid: process.pid,
      processStartTicks: this.processStartTicks,
      processStartExact: Boolean(this.options.generation),
      controlPort: this.controlPort,
      controlToken: this.controlToken,
      startedAt: this.startedAt,
    };
  }

  async acceptStartupClaim() {
    if (!this.options.generation) return;

    const deadline = Date.now() + 5000;
    const read = async () => {
      let claim;
      try {
        claim = JSON.parse(readFileSync(this.lockPath, "utf8"));
      } catch {
        claim = null;
      }

      if (claim?.generation && claim.generation !== this.generation) {
        throw new Error("Durable terminal startup ownership changed before host launch");
      }
      if (
        claim.hostPid === process.pid &&
        typeof claim.hostProcessStartTicks === "string" &&
        /^\d+$/.test(claim.hostProcessStartTicks)
      ) {
        this.processStartTicks = claim.hostProcessStartTicks;
        return;
      }
      if (Date.now() >= deadline) {
        throw new Error("Durable terminal startup process identity was not published");
      }

      await this.wait(10);
      return read();
    };

    return read();
  }

  record(kind, session, details = {}) {
    this.diagnostics.append({
      timestamp: timestamp(),
      kind,
      hostPid: process.pid,
      sessionId: session?.id ?? null,
      ...details,
    });
  }

  publicSessions() {
    return [...this.sessions.values()]
      .sort((left, right) => left.order - right.order)
      .map((session) => ({
        id: session.id,
        worktreePath: session.worktreePath,
        lifecycle: session.state,
        endpoint:
          session.state === "running"
            ? `http://127.0.0.1:${session.publicPort}/?cap=${session.capability}`
            : null,
        error: session.error ?? null,
        order: session.order,
        ttydPid: session.ttydPid ?? null,
        shellPid: session.shellPid ?? null,
        upstreamOpenedAt: session.upstreamOpenedAt ?? null,
        upstreamClosedAt: session.upstreamClosedAt ?? null,
        upstreamCloseCode: session.upstreamCloseCode ?? null,
        upstreamCloseReason: session.upstreamCloseReason ?? null,
        browserAttachments: session.attachment ? 1 : 0,
        lastPongAt: session.lastPongAt ?? null,
      }));
  }

  persistStatus() {
    atomicWriteJson(this.statusPath, {
      version: hostProtocolVersion,
      generation: this.generation,
      hostPid: process.pid,
      processStartTicks: this.processStartTicks,
      processStartExact: Boolean(this.options.generation),
      controlPort: this.controlPort,
      startedAt: this.startedAt,
      observedAt: timestamp(),
      sessions: [...this.sessions.values()]
        .sort((left, right) => left.order - right.order)
        .map(publicDiagnosticSession),
    });
  }

  async start() {
    mkdirSync(this.options.stateDirectory, { recursive: true });
    try {
      this.controlPort = await listenLoopback(this.controlServer);
      await this.acceptStartupClaim();
      writeManifestIfUnowned(this.statePath, this.manifest());
      this.persistStatus();
      this.record("host-started", null, {
        controlPort: this.controlPort,
        generation: this.generation,
      });
      this.startTimers();
    } catch (error) {
      await closeServer(this.controlServer);
      throw error;
    }

    process.once("SIGINT", () => void this.shutdown("sigint"));
    process.once("SIGTERM", () => void this.shutdown("sigterm"));
  }

  startTimers() {
    this.pingTimer = setInterval(() => this.pingSessions(), pingIntervalMs);
    this.heartbeatTimer = setInterval(
      () => this.recordHeartbeats(),
      heartbeatIntervalMs,
    );
  }

  stopTimers() {
    clearInterval(this.pingTimer);
    clearInterval(this.heartbeatTimer);
  }

  controlAuthorized(request) {
    const authorization = String(request.headers.authorization ?? "");
    return secureEqual(authorization, `Bearer ${this.controlToken}`);
  }

  async handleControlRequest(request, response) {
    if (!this.controlAuthorized(request)) {
      sendJson(response, 404, { error: "Not found" });
      return;
    }

    const url = new URL(request.url, `http://127.0.0.1:${this.controlPort}`);

    if (request.method === "GET" && url.pathname === "/health") {
      sendJson(response, 200, {
        version: hostProtocolVersion,
        generation: this.generation,
        pid: process.pid,
        processStartTicks: this.processStartTicks,
        processStartExact: Boolean(this.options.generation),
        startedAt: this.startedAt,
      });
      return;
    }

    if (request.method === "GET" && url.pathname === "/sessions") {
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "POST" && url.pathname === "/sessions") {
      if (this.rejectMutationDuringShutdown(response)) return;
      const body = await readJsonBody(request);
      if (this.rejectMutationDuringShutdown(response)) return;
      await this.trackStart(body.worktreePath);
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "DELETE" && url.pathname.startsWith("/sessions/")) {
      if (this.rejectMutationDuringShutdown(response)) return;
      const sessionId = decodeURIComponent(url.pathname.slice("/sessions/".length));
      const session = this.sessions.get(sessionId);
      if (session) await this.closeSession(session, "explicit-close");
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "POST" && url.pathname === "/reservations") {
      if (this.rejectMutationDuringShutdown(response)) return;
      const body = await readJsonBody(request);
      if (this.rejectMutationDuringShutdown(response)) return;
      const reservation = await this.reserveWorktree(
        body.worktreePath,
        body.reservationId,
      );
      sendJson(response, 201, {
        reservation,
        sessions: this.publicSessions(),
      });
      return;
    }

    if (url.pathname.startsWith("/reservations/")) {
      if (this.rejectMutationDuringShutdown(response)) return;
      const suffix = url.pathname.slice("/reservations/".length);
      const renewSuffix = "/renew";
      const renewing =
        request.method === "POST" && suffix.endsWith(renewSuffix);
      const reservationId = decodeURIComponent(
        renewing ? suffix.slice(0, -renewSuffix.length) : suffix,
      );

      if (renewing) {
        const reservation = await this.renewReservation(reservationId);
        sendJson(response, 200, { reservation });
        return;
      }

      if (request.method === "DELETE") {
        const released = await this.releaseReservation(reservationId);
        sendJson(response, 200, { released });
        return;
      }
    }

    if (request.method === "POST" && url.pathname === "/events") {
      if (this.rejectMutationDuringShutdown(response)) return;
      const body = await readJsonBody(request);
      if (this.rejectMutationDuringShutdown(response)) return;
      if (body.kind !== "treemon-connected") {
        sendJson(response, 400, { error: "Unsupported event kind" });
        return;
      }

      this.record("treemon-connected", null, {
        treemonPid:
          Number.isInteger(body.treemonPid) && body.treemonPid > 0
            ? body.treemonPid
            : null,
        instanceId: sanitizeMetadataText(body.instanceId, 80),
      });
      sendJson(response, 200, { recorded: true });
      return;
    }

    if (request.method === "POST" && url.pathname === "/shutdown") {
      try {
        await this.beginShutdown("control-request");
        response.once("finish", () =>
          void this.finalizeShutdown("control-request"),
        );
        sendJson(response, 200, { stopping: true, pid: process.pid });
      } catch (error) {
        this.resumeAfterFailedShutdown();
        sendJson(response, error?.statusCode ?? 500, {
          error: error.message,
        });
      }
      return;
    }

    sendJson(response, 404, { error: "Not found" });
  }

  rejectMutationDuringShutdown(response) {
    if (!this.shuttingDown) return false;
    sendJson(response, 503, {
      error: "Durable terminal host is shutting down",
    });
    return true;
  }

  async trackStart(worktreePath) {
    const operation = this.startSession(worktreePath);
    this.inFlightStarts.add(operation);

    try {
      return await operation;
    } finally {
      this.inFlightStarts.delete(operation);
    }
  }

  async withKeyTransition(key, transition) {
    const previous = this.keyOperations.get(key) ?? Promise.resolve();
    const operation = previous.catch(() => {}).then(transition);
    this.keyOperations.set(key, operation);

    try {
      return await operation;
    } finally {
      if (this.keyOperations.get(key) === operation) {
        this.keyOperations.delete(key);
      }
    }
  }

  activeReservation(key) {
    const reservation = this.reservations.get(key);
    if (!reservation) return null;
    if (reservation.acquiring) return reservation;
    if (reservation.expiresAtMs > this.now()) return reservation;
    this.reservations.delete(key);
    this.record("worktree-reservation-expired", null);
    return null;
  }

  publicReservation(reservation) {
    return {
      id: reservation.id,
      worktreePath: reservation.worktreePath,
      expiresAt: new Date(reservation.expiresAtMs).toISOString(),
    };
  }

  reservationById(id) {
    return [...this.reservations.values()].find(
      (reservation) => reservation.id === id,
    );
  }

  async reserveWorktree(rawPath, requestedId) {
    const worktreePath = canonicalWorktreePath(rawPath);
    const key = worktreeKey(worktreePath);
    if (
      requestedId !== undefined &&
      !/^[A-Za-z0-9_-]{16,128}$/.test(requestedId)
    ) {
      throw new ControlError(400, "Terminal cleanup reservation ID is invalid");
    }

    return this.withKeyTransition(key, async () => {
      if (this.activeReservation(key)) {
        throw new ControlError(
          409,
          "A terminal cleanup reservation already owns this worktree",
        );
      }

      const reservation = {
        id: requestedId ?? randomToken(24),
        key,
        worktreePath,
        acquiring: true,
        expiresAtMs: this.now() + this.reservationLeaseMs,
      };
      this.reservations.set(key, reservation);
      this.record("worktree-reservation-acquired", null);

      try {
        const existing = this.sessionForKey(key);
        if (existing) {
          await this.closeSessionOnce(existing, "worktree-reservation");
        }
        reservation.expiresAtMs =
          this.now() + this.reservationLeaseMs;
        reservation.acquiring = false;
        return this.publicReservation(reservation);
      } catch (error) {
        if (this.reservations.get(key) === reservation) {
          this.reservations.delete(key);
        }
        this.record("worktree-reservation-failed", null);
        throw error;
      }
    });
  }

  async renewReservation(id) {
    const found = this.reservationById(id);
    if (!found) {
      throw new ControlError(409, "Terminal cleanup reservation is no longer active");
    }

    return this.withKeyTransition(found.key, async () => {
      const active = this.activeReservation(found.key);
      if (active !== found) {
        throw new ControlError(409, "Terminal cleanup reservation is no longer active");
      }
      found.expiresAtMs = this.now() + this.reservationLeaseMs;
      return this.publicReservation(found);
    });
  }

  async releaseReservation(id) {
    const found = this.reservationById(id);
    if (!found) return false;

    return this.withKeyTransition(found.key, async () => {
      if (this.reservations.get(found.key) !== found) return false;
      this.reservations.delete(found.key);
      this.record("worktree-reservation-released", null);
      return true;
    });
  }

  newSession(worktreePath, order) {
    return {
      id: randomToken(16),
      capability: randomToken(),
      worktreePath,
      key: worktreeKey(worktreePath),
      state: "starting",
      error: null,
      order,
      replay: emptyReplayBuffer(),
      replayDroppedBytesRecorded: 0,
      titleFrame: null,
      preferencesFrame: null,
      attachment: null,
      terminalSize: { columns: defaultColumns, rows: defaultRows },
      closing: false,
      failureRecorded: false,
      ownedProcesses: new Map(),
      unverifiedSpawnedPids: [],
      shellPid: null,
      ttydPid: null,
      upstreamOpenedAt: null,
      upstreamClosedAt: null,
      upstreamCloseCode: null,
      upstreamCloseReason: null,
      lastPongAt: null,
    };
  }

  async startSession(rawPath) {
    const worktreePath = canonicalWorktreePath(rawPath);
    const key = worktreeKey(worktreePath);
    return this.withKeyTransition(key, () =>
      this.startSessionSerialized(worktreePath, key),
    );
  }

  sessionForKey(key) {
    return [...this.sessions.values()].find((session) => session.key === key);
  }

  async startSessionSerialized(worktreePath, key) {
    if (this.activeReservation(key)) {
      throw new ControlError(
        409,
        "Terminal start is blocked while the worktree is being deleted or archived",
      );
    }

    const existing = this.sessionForKey(key);
    if (existing?.state === "starting" || existing?.state === "running") return existing;

    const order = existing?.order ?? this.nextOrder++;
    if (existing) {
      await this.closeSessionOnce(existing, "failed-restart");
      const replacement = this.sessionForKey(key);
      if (replacement) {
        if (
          replacement !== existing &&
          (replacement.state === "starting" || replacement.state === "running")
        ) {
          return replacement;
        }
        throw new Error(
          "Terminal session ownership changed while its failed generation was closing",
        );
      }
    }

    const session = this.newSession(worktreePath, order);
    this.sessions.set(session.id, session);
    this.persistStatus();
    this.record("session-starting", session);

    try {
      await this.startSessionProxy(session);
      session.state = "running";
      session.error = null;
      this.persistStatus();
      this.record("session-running", session, {
        ttydPid: session.ttydPid,
        shellPid: session.shellPid,
        publicPort: session.publicPort,
        ttydPort: session.ttydPort,
      });
    } catch (error) {
      session.state = "failed";
      session.error = this.startFailureMessage(error);
      session.closing = true;
      this.record("session-start-failed", session, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
      try {
        await this.stopSessionResources(session);
      } catch (cleanupError) {
        session.error = `Terminal startup failed and owned process cleanup did not complete: ${sanitizeMetadataText(cleanupError.message, 240)}`;
        this.record("session-start-cleanup-failed", session, {
          trackedPids: trackedOwnedProcessIds(session),
          unverifiedPids: session.unverifiedSpawnedPids,
        });
      }
      session.closing = false;
      this.persistStatus();
    }

    return session;
  }

  startFailureMessage(error) {
    if (error?.code === "ENOENT") return "ttyd could not be started";
    if (String(error?.message).includes("Timed out")) return error.message;
    if (String(error?.message).includes("ttyd exited with code")) return error.message;
    return "The durable terminal host could not start ttyd";
  }

  async startSessionProxy(session) {
    session.publicServer = createHttpServer((request, response) => {
      const authorization = authorizedSessionRequest(request, session);
      if (!authorization.authorized) {
        response.writeHead(404, { "content-length": "0" });
        response.end();
        return;
      }

      if (session.state !== "starting" && session.state !== "running") {
        response.writeHead(410, { "content-length": "0" });
        response.end();
        return;
      }

      proxyHttpRequest(
        request,
        response,
        session,
        authorization.setCookie,
        authorization.url,
      );
    });

    session.browserWebSockets = new WebSocketServer({
      noServer: true,
      maxPayload: maxBrowserFrameBytes,
      handleProtocols: (protocols) => (protocols.has("tty") ? "tty" : false),
    });
    session.browserWebSockets.on("connection", (socket) =>
      this.attachBrowser(session, socket),
    );
    session.publicServer.on("upgrade", (request, socket, head) => {
      const authorization = authorizedSessionRequest(request, session);
      const expectedOrigin = `http://127.0.0.1:${session.publicPort}`;

      if (
        session.state !== "running" ||
        !authorization.authorized ||
        request.headers.origin !== expectedOrigin ||
        authorization.url.pathname !== "/ws"
      ) {
        rejectSocket(socket);
        return;
      }

      session.browserWebSockets.handleUpgrade(request, socket, head, (webSocket) =>
        session.browserWebSockets.emit("connection", webSocket, request),
      );
    });
    session.publicPort = await listenLoopback(session.publicServer);

    session.ttydPort = await freeLoopbackPort();
    session.cookieName = sessionCookieName(session.id);
    session.pidFile = join(
      this.options.stateDirectory,
      `session-${session.id}.pid`,
    );
    const shellScript = "$PID > $env:TMTP;sl -LiteralPath $env:TMTW";
    const shellArguments = [
      this.options.shellCommand,
      "-WorkingDirectory",
      ".",
      "-NoExit",
      "-EncodedCommand",
      Buffer.from(shellScript, "utf16le").toString("base64"),
    ];
    if (Buffer.byteLength(shellArguments.join(" "), "utf8") >= 256) {
      throw new Error("ttyd child command exceeds the Windows command buffer");
    }
    const ttydArguments = [
      "-p",
      String(session.ttydPort),
      "-i",
      "127.0.0.1",
      "-W",
      "-O",
      "-o",
      "-t",
      "fontSize=16",
      "-t",
      "disableLeaveAlert=true",
      "-w",
      session.worktreePath,
      ...shellArguments,
    ];
    const spawnFailure = { error: null };

    session.ttydProcess = spawn(this.options.ttydPath, ttydArguments, {
      windowsHide: true,
      detached: process.platform !== "win32",
      stdio: ["ignore", "pipe", "pipe"],
      env: {
        ...process.env,
        TMTP: session.pidFile,
        TMTW: session.worktreePath,
        TREEMON_TERMINAL_WORKTREE: session.worktreePath,
        TREEMON_TERMINAL_SESSION_ID: session.id,
      },
    });
    session.ttydPid = session.ttydProcess.pid ?? null;
    session.ttydProcess.once("error", (error) => {
      spawnFailure.error = error;
    });
    session.ttydProcess.once("exit", (code, signal) => {
      this.record("ttyd-exited", session, {
        ttydPid: session.ttydPid,
        exitCode: code,
        signal: sanitizeMetadataText(signal, 32),
      });
      if (!session.closing) {
        void this.interruptSession(session, `ttyd exited with code ${code ?? "unknown"}`);
      }
    });
    const identityDeadline = Date.now() + 2000;
    const captureTtydIdentity = async () => {
      if (spawnFailure.error) throw spawnFailure.error;
      let identity;
      try {
        identity =
          await this.processController.inspect(session.ttydPid);
      } catch (error) {
        if (
          validPid(session.ttydPid) &&
          session.ttydProcess.exitCode === null
        ) {
          session.unverifiedSpawnedPids = [session.ttydPid];
        }
        throw error;
      }
      if (identity) return identity;
      if (session.ttydProcess.exitCode !== null) {
        throw new Error(
          `ttyd exited with code ${session.ttydProcess.exitCode}`,
        );
      }
      if (Date.now() >= identityDeadline) {
        session.unverifiedSpawnedPids = [session.ttydPid];
        throw new Error("Could not capture ttyd process creation identity");
      }
      await this.wait(25);
      return captureTtydIdentity();
    };
    const ttydIdentity = await captureTtydIdentity();
    trackOwnedProcess(session, ttydIdentity, 0);

    [session.ttydProcess.stdout, session.ttydProcess.stderr].forEach((stream) => {
      const lines = createInterface({ input: stream });
      lines.on("line", (line) => {
        const match = /started process, pid:\s*(\d+)/i.exec(line);
        if (!match) return;
        const shellPid = Number.parseInt(match[1], 10);
        if (session.shellPid === shellPid) return;
        session.shellPid = shellPid;
        this.persistStatus();
        this.record("shell-discovered", session, { shellPid: session.shellPid });
      });
    });

    const ttydHttp = `http://127.0.0.1:${session.ttydPort}/`;
    await waitForTtyd(ttydHttp, session.ttydProcess, spawnFailure, 10_000);
    session.upstream = await openUpstream(
      `ws://127.0.0.1:${session.ttydPort}/ws`,
      ttydHttp.slice(0, -1),
    );
    this.configureUpstream(session);
    session.upstream.send(
      Buffer.from(
        JSON.stringify({
          AuthToken: "",
          columns: session.terminalSize.columns,
          rows: session.terminalSize.rows,
        }),
        "utf8",
      ),
    );
    session.upstreamOpenedAt = timestamp();
    session.lastPongAt = session.upstreamOpenedAt;
    const shellPid = await this.waitForShellPid(session);
    if (session.shellPid !== shellPid) {
      session.shellPid = shellPid;
      this.persistStatus();
      this.record("shell-discovered", session, { shellPid: session.shellPid });
    }
  }

  async waitForShellPid(session) {
    const deadline = Date.now() + 5000;

    const read = async () => {
      if (session.ttydProcess.exitCode !== null) {
        throw new Error(`ttyd exited with code ${session.ttydProcess.exitCode}`);
      }

      if (existsSync(session.pidFile)) {
        const pid = Number.parseInt(readFileSync(session.pidFile, "utf8").trim(), 10);
        if (validPid(pid)) {
          await discoverOwnedDescendants(session, this.processController);
          const owned = [...ownedProcesses(session).values()].find(
            ({ identity }) => identity.pid === pid,
          );
          if (owned) return pid;
        }
      }

      if (Date.now() >= deadline) {
        throw new Error("Timed out waiting for PowerShell process identity");
      }

      await delay(50);
      return read();
    };

    return read();
  }

  configureUpstream(session) {
    session.upstream.on("message", (data) =>
      this.handleUpstreamFrame(session, Buffer.from(data)),
    );
    session.upstream.on("pong", () => {
      session.lastPongAt = timestamp();
    });
    session.upstream.on("close", (code, reason) => {
      session.upstreamClosedAt = timestamp();
      session.upstreamCloseCode = code;
      session.upstreamCloseReason = sanitizeMetadataText(reason.toString());
      this.record("upstream-closed", session, {
        closeCode: code,
        closeReason: session.upstreamCloseReason,
        ttydAlive: isPidAlive(session.ttydPid),
        shellAlive: isPidAlive(session.shellPid),
      });
      this.persistStatus();
      if (!session.closing) {
        void this.interruptSession(
          session,
          `ttyd WebSocket closed with code ${code}`,
        );
      }
    });
    session.upstream.on("error", (error) => {
      this.record("upstream-error", session, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
    });
  }

  handleUpstreamFrame(session, frame) {
    const command = frame[0];

    if (command === "0".charCodeAt(0)) {
      const previousDropped = session.replay.droppedBytes;
      session.replay = appendReplayFrame(
        session.replay,
        frame,
        this.options.replayBytes,
      );
      if (
        session.replay.droppedBytes > previousDropped &&
        (session.replayDroppedBytesRecorded === 0 ||
          session.replay.droppedBytes - session.replayDroppedBytesRecorded >=
            this.options.replayBytes)
      ) {
        session.replayDroppedBytesRecorded = session.replay.droppedBytes;
        this.record("replay-truncated", session, {
          droppedBytes: session.replay.droppedBytes,
        });
      }
      this.sendLiveOutput(session, frame, session.replay.nextSequence - 1);
      return;
    }

    if (command === "1".charCodeAt(0)) session.titleFrame = Buffer.from(frame);
    if (command === "2".charCodeAt(0)) session.preferencesFrame = Buffer.from(frame);

    if (session.attachment?.initialized) {
      this.safeBrowserSend(session.attachment.socket, frame);
    }
  }

  sendLiveOutput(session, frame, sequence) {
    const attachment = session.attachment;
    if (!attachment?.initialized || attachment.paused) return;
    if (this.safeBrowserSend(attachment.socket, frame)) {
      attachment.nextSequence = sequence + 1;
    }
  }

  safeBrowserSend(socket, data) {
    if (socket?.readyState !== WebSocket.OPEN) return false;
    socket.send(data, { binary: true });
    return true;
  }

  attachBrowser(session, socket) {
    const previous = session.attachment;
    session.attachment = null;
    if (previous?.socket?.readyState === WebSocket.OPEN) {
      previous.socket.close(1000, "Replaced by a new attachment");
    }

    const attachment = {
      socket,
      initialized: false,
      paused: false,
      nextSequence: session.replay.nextSequence,
    };
    session.attachment = attachment;
    this.persistStatus();
    this.record("browser-attached", session, { browserAttachments: 1 });

    socket.on("message", (data) => {
      if (session.attachment !== attachment) return;
      try {
        this.handleBrowserFrame(session, attachment, Buffer.from(data));
      } catch (error) {
        this.record("browser-protocol-error", session, {
          errorType: sanitizeMetadataText(error?.name || "Error", 80),
        });
        socket.close(1008, "Invalid terminal protocol frame");
      }
    });
    socket.on("close", (code, reason) => {
      if (session.attachment !== attachment) return;
      session.attachment = null;
      this.persistStatus();
      this.record("browser-detached", session, {
        browserAttachments: 0,
        closeCode: code,
        closeReason: sanitizeMetadataText(reason.toString()),
      });
    });
    socket.on("error", (error) => {
      if (session.attachment !== attachment) return;
      this.record("browser-error", session, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
    });
  }

  handleBrowserFrame(session, attachment, frame) {
    if (session.attachment !== attachment) return;

    if (!attachment.initialized) {
      session.terminalSize = parseInitialHandshake(frame);
      [session.titleFrame, session.preferencesFrame]
        .filter(Boolean)
        .forEach((initialFrame) =>
          this.safeBrowserSend(attachment.socket, initialFrame),
        );
      session.replay.frames.forEach((replayFrame) =>
        this.safeBrowserSend(attachment.socket, replayFrame.data),
      );
      attachment.nextSequence = session.replay.nextSequence;
      attachment.initialized = true;
      this.sendUpstream(session, resizeFrame(session.terminalSize));
      this.record("browser-ready", session, {
        replayBytes: session.replay.bytes,
        columns: session.terminalSize.columns,
        rows: session.terminalSize.rows,
      });
      return;
    }

    switch (String.fromCharCode(frame[0])) {
      case "0":
        this.sendUpstream(session, frame);
        break;
      case "1":
        session.terminalSize = parseResizeFrame(frame);
        this.sendUpstream(session, resizeFrame(session.terminalSize));
        break;
      case "2":
        attachment.paused = true;
        break;
      case "3":
        attachment.paused = false;
        this.resumeBrowser(session, attachment);
        break;
      default:
        throw new Error("Unknown ttyd browser command");
    }
  }

  resumeBrowser(session, attachment) {
    if (session.attachment !== attachment) return;

    const available = replayFramesFrom(session.replay, attachment.nextSequence);
    const oldestSequence = session.replay.frames[0]?.sequence;

    if (
      oldestSequence !== undefined &&
      oldestSequence > attachment.nextSequence
    ) {
      this.record("browser-replay-truncated", session, {
        missingFrames: oldestSequence - attachment.nextSequence,
      });
    }

    available.forEach((frame) =>
      this.safeBrowserSend(attachment.socket, frame.data),
    );
    attachment.nextSequence = session.replay.nextSequence;
    this.sendUpstream(session, resizeFrame(session.terminalSize));
  }

  sendUpstream(session, frame) {
    if (session.upstream?.readyState !== WebSocket.OPEN) {
      throw new Error("ttyd WebSocket is not open");
    }
    session.upstream.send(frame, { binary: true });
  }

  async interruptSession(session, error) {
    if (session.closing || session.failureRecorded) return;
    session.failureRecorded = true;
    session.state = "failed";
    session.error = error;
    const attachment = session.attachment;
    session.attachment = null;
    attachment?.socket?.close(1011, "Terminal session interrupted");
    session.browserWebSockets?.close();
    await closeServer(session.publicServer);
    this.persistStatus();
    this.record("session-interrupted", session, {
      ttydAlive: isPidAlive(session.ttydPid),
      shellAlive: isPidAlive(session.shellPid),
    });
  }

  async closeSession(session, reason) {
    return this.withKeyTransition(session.key, async () => {
      if (this.sessions.get(session.id) !== session) return;
      return this.closeSessionOnce(session, reason);
    });
  }

  async closeSessionOnce(session, reason) {
    if (this.sessions.get(session.id) !== session) return;
    session.closing = true;
    session.state = "closing";
    session.error = null;
    this.persistStatus();
    this.record("session-close-requested", session, { reason });

    try {
      await this.stopSessionResources(session);
      this.sessions.delete(session.id);
      this.persistStatus();
      this.record("session-closed", session, {
        ttydOwnedAlive: false,
        shellOwnedAlive: false,
      });
    } catch (error) {
      session.closing = false;
      session.state = "failed";
      session.error = `Terminal cleanup did not complete: ${sanitizeMetadataText(error.message, 240)}`;
      this.persistStatus();
      this.record("session-close-failed", session, {
        trackedPids: trackedOwnedProcessIds(session),
        unverifiedPids: session.unverifiedSpawnedPids,
      });
      throw new Error(session.error);
    }
  }

  async stopSessionResources(session) {
    await discoverOwnedDescendants(session, this.processController);

    const attachment = session.attachment;
    session.attachment = null;
    attachment?.socket?.close(1000, "Terminal session closed");

    if (
      session.upstream &&
      session.upstream.readyState !== WebSocket.CLOSED
    ) {
      session.upstream.close(1000, "Terminal session closed");
      const closed = await Promise.race([
        once(session.upstream, "close").then(() => true),
        delay(2000).then(() => false),
      ]);
      if (!closed) session.upstream.terminate();
    }

    let remaining = await waitForOwnedProcessExit(
      session,
      this.processController,
      this.cleanupTimeouts.graceful,
      this.wait,
    );

    remaining = await forceOwnedProcessExit(
      session,
      this.processController,
      this.cleanupTimeouts.forced,
      this.wait,
    );
    const unverifiedRemaining = await Promise.all(
      (session.unverifiedSpawnedPids ?? []).map(async (pid) => ({
        pid,
        actual: await this.processController.inspect(pid),
      })),
    ).then((processes) =>
      processes.filter(
        ({ pid, actual }) =>
          actual &&
          !(
            pid === session.ttydPid &&
            session.ttydProcess?.exitCode !== null
          ),
      ),
    );
    await closeServer(session.publicServer);
    session.browserWebSockets?.close();

    if (remaining.length > 0 || unverifiedRemaining.length > 0) {
      const remainingPids = [
        ...remaining.map(({ identity }) => identity.pid),
        ...unverifiedRemaining.map(({ pid }) => pid),
      ];
      throw new Error(
        `Owned terminal processes remain: ${remainingPids.join(", ")}`,
      );
    }

    if (session.pidFile) rmSync(session.pidFile, { force: true });
  }

  pingSessions() {
    const now = Date.now();

    this.sessions.forEach((session) => {
      if (
        session.state !== "running" ||
        session.upstream?.readyState !== WebSocket.OPEN
      ) {
        return;
      }

      const lastPong = Date.parse(session.lastPongAt ?? session.upstreamOpenedAt);
      if (Number.isFinite(lastPong) && now - lastPong > heartbeatFailureMs) {
        this.record("upstream-heartbeat-failed", session, {
          heartbeatAgeMs: now - lastPong,
        });
        session.upstream.terminate();
        return;
      }

      session.upstream.ping();
    });
  }

  recordHeartbeats() {
    const now = Date.now();

    this.sessions.forEach((session) => {
      if (session.state !== "running") return;
      const openedAt = Date.parse(session.upstreamOpenedAt);
      const lastPongAt = Date.parse(session.lastPongAt);
      this.record("heartbeat", session, {
        upstreamAgeMs: Number.isFinite(openedAt) ? now - openedAt : null,
        heartbeatAgeMs: Number.isFinite(lastPongAt) ? now - lastPongAt : null,
        ttydPid: session.ttydPid,
        ttydAlive: isPidAlive(session.ttydPid),
        shellPid: session.shellPid,
        shellAlive: isPidAlive(session.shellPid),
        browserAttachments: session.attachment ? 1 : 0,
      });
    });
    this.persistStatus();
  }

  beginShutdown(reason) {
    if (this.shutdownPromise) return this.shutdownPromise;

    const activeReservations = [...this.reservations.keys()].filter((key) =>
      this.activeReservation(key),
    );
    if (activeReservations.length > 0) {
      throw new ControlError(
        409,
        "Durable terminal host has active worktree mutation reservations",
      );
    }

    this.shuttingDown = true;
    this.stopTimers();
    this.record("host-stopping", null, { reason });
    this.shutdownPromise = (async () => {
      await Promise.allSettled([...this.inFlightStarts]);
      await Promise.allSettled([...this.keyOperations.values()]);
      const reservationsAfterQuiescence = [
        ...this.reservations.keys(),
      ].filter((key) => this.activeReservation(key));
      if (reservationsAfterQuiescence.length > 0) {
        throw new ControlError(
          409,
          "Durable terminal host has active worktree mutation reservations",
        );
      }
      const results = await Promise.allSettled(
        [...this.sessions.values()].map((session) =>
          this.closeSession(session, "host-shutdown"),
        ),
      );
      const failures = results.filter((result) => result.status === "rejected");

      if (failures.length > 0) {
        this.record("host-stop-failed", null, {
          failedSessions: failures.length,
        });
        this.persistStatus();
        throw new Error(
          `Durable terminal host could not close ${failures.length} session(s)`,
        );
      }
    })();
    return this.shutdownPromise;
  }

  resumeAfterFailedShutdown() {
    if (!this.shuttingDown) return;
    this.shuttingDown = false;
    this.shutdownPromise = null;
    this.startTimers();
  }

  async finalizeShutdown(reason) {
    await closeServer(this.controlServer);
    this.record("host-stopped", null, { reason });
    this.persistStatus();
    const removed = removeManifestIfOwned(
      this.statePath,
      this.manifestOwner(),
    );
    if (!removed) {
      this.record("host-manifest-ownership-changed", null, { reason });
    }
    this.exitProcess(0);
  }

  async shutdown(reason) {
    try {
      await this.beginShutdown(reason);
      await this.finalizeShutdown(reason);
    } catch (error) {
      this.resumeAfterFailedShutdown();
      this.record("host-shutdown-error", null, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
    }
  }
}

function parseArguments(argumentsList) {
  const values = argumentsList.reduce((state, value, index) => {
    if (!value.startsWith("--")) return state;
    const next = argumentsList[index + 1];
    return {
      ...state,
      [value.slice(2)]: next && !next.startsWith("--") ? next : true,
    };
  }, {});

  const stateDirectory = resolve(String(values["state-dir"] ?? ""));
  const ttydPath = resolve(String(values.ttyd ?? ""));
  const shellCommand = String(values.shell ?? "pwsh");

  if (!values["state-dir"]) throw new Error("--state-dir is required");
  if (!values.ttyd) throw new Error("--ttyd is required");

  return {
    stateDirectory,
    ttydPath,
    shellCommand,
    generation:
      typeof values.generation === "string" ? values.generation : undefined,
    replayBytes: safeInteger(
      Number.parseInt(values["replay-bytes"], 10),
      defaultReplayBytes,
      16 * 1024 * 1024,
    ),
    diagnosticBytes: safeInteger(
      Number.parseInt(values["diagnostic-bytes"], 10),
      defaultDiagnosticBytes,
      16 * 1024 * 1024,
    ),
  };
}

export async function runHost(argumentsList = process.argv.slice(2)) {
  const host = new DurableTerminalHost(parseArguments(argumentsList));
  await host.start();
  return host;
}

const invokedDirectly =
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href;

if (invokedDirectly) {
  runHost().catch((error) => {
    process.stderr.write(`${sanitizeMetadataText(error.message, 500)}\n`);
    process.exitCode = 1;
  });
}
