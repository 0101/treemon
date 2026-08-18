import { spawn } from "node:child_process";
import { randomBytes, timingSafeEqual } from "node:crypto";
import { once } from "node:events";
import {
  appendFileSync,
  existsSync,
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

export const hostProtocolVersion = 1;
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

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const timestamp = () => new Date().toISOString();

const randomToken = (bytes = 24) => randomBytes(bytes).toString("base64url");

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

async function waitForChildExit(child, timeoutMs) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return true;

  return Promise.race([
    once(child, "exit").then(() => true),
    delay(timeoutMs).then(() => false),
  ]);
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

function isPidAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return null;

  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error?.code === "ESRCH" ? false : null;
  }
}

class DurableTerminalHost {
  constructor(options) {
    this.options = options;
    this.startedAt = timestamp();
    this.controlToken = randomToken();
    this.sessions = new Map();
    this.nextOrder = 0;
    this.shuttingDown = false;
    this.statePath = join(options.stateDirectory, "host.json");
    this.statusPath = join(options.stateDirectory, "status.json");
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
          sendJson(response, 500, { error: error.message });
        } else {
          response.end();
        }
      });
    });
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
      hostPid: process.pid,
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
    this.controlPort = await listenLoopback(this.controlServer);
    atomicWriteJson(this.statePath, {
      version: hostProtocolVersion,
      pid: process.pid,
      controlPort: this.controlPort,
      controlToken: this.controlToken,
      startedAt: this.startedAt,
    });
    this.persistStatus();
    this.record("host-started", null, { controlPort: this.controlPort });

    this.pingTimer = setInterval(() => this.pingSessions(), pingIntervalMs);
    this.heartbeatTimer = setInterval(
      () => this.recordHeartbeats(),
      heartbeatIntervalMs,
    );

    process.once("SIGINT", () => void this.shutdown("sigint"));
    process.once("SIGTERM", () => void this.shutdown("sigterm"));
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
        pid: process.pid,
        startedAt: this.startedAt,
      });
      return;
    }

    if (request.method === "GET" && url.pathname === "/sessions") {
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "POST" && url.pathname === "/sessions") {
      const body = await readJsonBody(request);
      await this.startSession(body.worktreePath);
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "DELETE" && url.pathname.startsWith("/sessions/")) {
      const sessionId = decodeURIComponent(url.pathname.slice("/sessions/".length));
      const session = this.sessions.get(sessionId);
      if (session) await this.closeSession(session, "explicit-close");
      sendJson(response, 200, { sessions: this.publicSessions() });
      return;
    }

    if (request.method === "POST" && url.pathname === "/events") {
      const body = await readJsonBody(request);
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
      sendJson(response, 202, { stopping: true, pid: process.pid });
      setImmediate(() => void this.shutdown("control-request"));
      return;
    }

    sendJson(response, 404, { error: "Not found" });
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
    const existing = [...this.sessions.values()].find((session) => session.key === key);

    if (existing?.state === "starting" || existing?.state === "running") return existing;

    const order = existing?.order ?? this.nextOrder++;
    if (existing) await this.closeSession(existing, "failed-restart");

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
      await this.stopSessionResources(session);
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
        if (Number.isInteger(pid) && pid > 0) return pid;
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
    if (session.attachment?.socket?.readyState === WebSocket.OPEN) {
      session.attachment.socket.close(1000, "Replaced by a new attachment");
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
      if (session.attachment === attachment) session.attachment = null;
      this.persistStatus();
      this.record("browser-detached", session, {
        browserAttachments: session.attachment ? 1 : 0,
        closeCode: code,
        closeReason: sanitizeMetadataText(reason.toString()),
      });
    });
    socket.on("error", (error) => {
      this.record("browser-error", session, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
    });
  }

  handleBrowserFrame(session, attachment, frame) {
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
    session.attachment?.socket?.close(1011, "Terminal session interrupted");
    session.browserWebSockets?.close();
    await closeServer(session.publicServer);
    this.persistStatus();
    this.record("session-interrupted", session, {
      ttydAlive: isPidAlive(session.ttydPid),
      shellAlive: isPidAlive(session.shellPid),
    });
  }

  async closeSession(session, reason) {
    if (!this.sessions.has(session.id)) return;
    session.closing = true;
    session.state = "closing";
    this.persistStatus();
    this.record("session-close-requested", session, { reason });
    await this.stopSessionResources(session);
    this.sessions.delete(session.id);
    this.persistStatus();
    this.record("session-closed", session, {
      ttydAlive: isPidAlive(session.ttydPid),
      shellAlive: isPidAlive(session.shellPid),
    });
  }

  async stopSessionResources(session) {
    session.attachment?.socket?.close(1000, "Terminal session closed");

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

    const ttydExited = await waitForChildExit(session.ttydProcess, 5000);
    if (!ttydExited && session.ttydProcess?.pid) {
      session.ttydProcess.kill();
      await waitForChildExit(session.ttydProcess, 2000);
    }

    await closeServer(session.publicServer);
    session.browserWebSockets?.close();
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

  async shutdown(reason) {
    if (this.shuttingDown) return;
    this.shuttingDown = true;
    clearInterval(this.pingTimer);
    clearInterval(this.heartbeatTimer);
    this.record("host-stopping", null, { reason });

    await Promise.all(
      [...this.sessions.values()].map((session) =>
        this.closeSession(session, "host-shutdown"),
      ),
    );
    await closeServer(this.controlServer);
    this.record("host-stopped", null, { reason });
    this.persistStatus();
    rmSync(this.statePath, { force: true });
    process.exit(0);
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
  if (!existsSync(ttydPath)) throw new Error(`ttyd is not installed at '${ttydPath}'`);

  return {
    stateDirectory,
    ttydPath,
    shellCommand,
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
