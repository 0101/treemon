import { spawn } from "node:child_process";
import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
import { once } from "node:events";
import {
  appendFileSync,
  existsSync,
  lstatSync,
  linkSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { createServer as createHttpServer, request as httpRequest } from "node:http";
import { createServer as createNetServer } from "node:net";
import {
  dirname,
  isAbsolute,
  join,
  relative,
  resolve,
  sep,
} from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath, pathToFileURL } from "node:url";
import { WebSocket, WebSocketServer } from "ws";

export const hostProtocolVersion = 3;
export const terminalOwnershipBoundary = "windows-job-v1";
export const terminalJobProtocolGeneration = 2;
export const terminalGenerationRecordVersion = 2;
export const terminalRuntimeBundleVersion = 1;
export const terminalRuntimeCapabilities = Object.freeze([
  "immutable-runtime-bundle-v1",
  "strict-evidence-paths-v1",
  "trusted-empty-supervisor-v1",
]);
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
const maximumGenerationRecords = 64;
const unixEpochTicks = 621355968000000000n;
const sha256Pattern = /^[0-9a-f]{64}$/;
const generationPattern = /^[A-Za-z0-9_-]{1,128}$/;
const runtimeBundleRoles = Object.freeze([
  ["host", "durable-terminal-host.mjs"],
  ["supervisor", "terminal-job-supervisor.ps1"],
  ["processIdentityHelper", "terminate-owned-process.ps1"],
]);
const processIdentityHelperPath = join(
  import.meta.dirname,
  "terminate-owned-process.ps1",
);
const terminalJobSupervisorPath = join(
  import.meta.dirname,
  "terminal-job-supervisor.ps1",
);

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const timestamp = () => new Date().toISOString();

const randomToken = (bytes = 24) => randomBytes(bytes).toString("base64url");

const witnessTokenHash = (token) =>
  createHash("sha256").update(token, "utf8").digest("hex");

export const isValidGeneration = (value) =>
  typeof value === "string" && generationPattern.test(value);

const sha256Bytes = (bytes) =>
  createHash("sha256").update(bytes).digest("hex");

export function runtimeBundleHash({
  hostScriptHash,
  supervisorScriptHash,
  processIdentityHelperHash,
}) {
  const hashes = new Map([
    ["host", hostScriptHash],
    ["supervisor", supervisorScriptHash],
    ["processIdentityHelper", processIdentityHelperHash],
  ]);
  if ([...hashes.values()].some((hash) => !sha256Pattern.test(hash ?? ""))) {
    throw new Error("Durable terminal runtime file hash is invalid");
  }
  const lines = [
    `bundle-version:${terminalRuntimeBundleVersion}`,
    `host-protocol:${hostProtocolVersion}`,
    `supervisor-protocol:${terminalJobProtocolGeneration}`,
    ...[...terminalRuntimeCapabilities]
      .sort()
      .map((capability) => `capability:${capability}`),
    ...runtimeBundleRoles.map(
      ([role, name]) => `file:${role}:${name}:${hashes.get(role)}`,
    ),
  ];
  return sha256Bytes(Buffer.from(`${lines.join("\n")}\n`, "utf8"));
}

const pathComparisonValue = (value) =>
  process.platform === "win32" ? value.toLowerCase() : value;

const isDescendantPath = (root, candidate) => {
  const relativePath = relative(resolve(root), resolve(candidate));
  return (
    Boolean(relativePath) &&
    !relativePath.startsWith(`..${sep}`) &&
    relativePath !== ".." &&
    !isAbsolute(relativePath)
  );
};

const assertNoReparsePoint = (root, candidate) => {
  const rootPath = resolve(root);
  const relativePath = relative(rootPath, resolve(candidate));
  const segments = relativePath.split(/[\\/]/).filter(Boolean);
  const paths = [
    rootPath,
    ...segments.map((_, index) =>
      join(rootPath, ...segments.slice(0, index + 1)),
    ),
  ];

  paths.forEach((path) => {
    if (!existsSync(path)) return;
    if (lstatSync(path).isSymbolicLink()) {
      throw new Error("Durable terminal evidence path crosses a reparse point");
    }
  });
};

export function containedStatePath(root, candidate) {
  const rootPath = resolve(root);
  const candidatePath = resolve(candidate);
  if (!isDescendantPath(rootPath, candidatePath)) {
    throw new Error("Durable terminal evidence path escaped its state directory");
  }

  const segments = relative(rootPath, candidatePath).split(/[\\/]/);
  if (
    segments.some(
      (segment) =>
        !segment ||
        segment === "." ||
        segment === ".." ||
        segment.includes(":"),
    )
  ) {
    throw new Error("Durable terminal evidence path contains an invalid segment");
  }

  assertNoReparsePoint(rootPath, candidatePath);
  if (existsSync(rootPath) && existsSync(candidatePath)) {
    const realRoot = realpathSync.native(rootPath);
    const realCandidate = realpathSync.native(candidatePath);
    if (!isDescendantPath(realRoot, realCandidate)) {
      throw new Error("Durable terminal evidence path resolved outside its state directory");
    }
  }
  return candidatePath;
}

const containedRegularFile = (bundleDirectory, filePath, expectedName) => {
  const path = containedStatePath(bundleDirectory, filePath);
  if (
    dirname(pathComparisonValue(path)) !==
      pathComparisonValue(resolve(bundleDirectory)) ||
    pathComparisonValue(path.slice(path.lastIndexOf(sep) + 1)) !==
      pathComparisonValue(expectedName)
  ) {
    throw new Error("Durable terminal runtime file is not a direct bundle child");
  }
  const info = lstatSync(path);
  if (!info.isFile() || info.isSymbolicLink()) {
    throw new Error("Durable terminal runtime file is not a regular file");
  }
  return path;
};

const verifiedRuntimeFile = (
  bundleDirectory,
  filePath,
  expectedName,
  expectedHash,
) => {
  if (!sha256Pattern.test(expectedHash ?? "")) {
    throw new Error("Durable terminal runtime file hash is invalid");
  }
  const path = containedRegularFile(bundleDirectory, filePath, expectedName);
  const actualHash = sha256Bytes(readFileSync(path));
  if (actualHash !== expectedHash) {
    throw new Error(`Durable terminal runtime file hash mismatch for ${expectedName}`);
  }
  return path;
};

export function verifyRuntimeBundle({
  stateDirectory,
  bundleDirectory,
  bundleHash,
  hostScriptHash,
  supervisorScriptHash,
  processIdentityHelperHash,
}) {
  if (!sha256Pattern.test(bundleHash ?? "")) {
    throw new Error("Durable terminal runtime bundle hash is invalid");
  }
  if (
    runtimeBundleHash({
      hostScriptHash,
      supervisorScriptHash,
      processIdentityHelperHash,
    }) !== bundleHash
  ) {
    throw new Error("Durable terminal runtime bundle hash does not match its files");
  }
  const runtimeRoot = join(resolve(stateDirectory), "terminal-runtime-bundles");
  const expectedDirectory = containedStatePath(
    runtimeRoot,
    join(runtimeRoot, bundleHash),
  );
  if (
    pathComparisonValue(expectedDirectory) !==
    pathComparisonValue(resolve(bundleDirectory))
  ) {
    throw new Error("Durable terminal runtime bundle path does not match its hash");
  }

  const manifestPath = containedRegularFile(
    expectedDirectory,
    join(expectedDirectory, "bundle.json"),
    "bundle.json",
  );
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  const expectedNames = [
    "bundle.json",
    ...runtimeBundleRoles.map(([, name]) => name),
  ].sort();
  const actualNames = readdirSync(expectedDirectory).sort();
  if (JSON.stringify(actualNames) !== JSON.stringify(expectedNames)) {
    throw new Error("Durable terminal runtime bundle contains unexpected files");
  }
  const expectedCapabilities = [...terminalRuntimeCapabilities].sort();
  const actualCapabilities = Array.isArray(manifest.capabilities)
    ? [...manifest.capabilities].sort()
    : [];
  if (
    manifest.version !== terminalRuntimeBundleVersion ||
    manifest.bundleHash !== bundleHash ||
    manifest.hostProtocolVersion !== hostProtocolVersion ||
    manifest.hostScriptHash !== hostScriptHash ||
    manifest.supervisorScriptHash !== supervisorScriptHash ||
    manifest.processIdentityHelperHash !== processIdentityHelperHash ||
    manifest.supervisorProtocolGeneration !== terminalJobProtocolGeneration ||
    JSON.stringify(actualCapabilities) !== JSON.stringify(expectedCapabilities)
  ) {
    throw new Error("Durable terminal runtime bundle manifest is incompatible");
  }

  const expectedHashes = new Map([
    ["host", hostScriptHash],
    ["supervisor", supervisorScriptHash],
    ["processIdentityHelper", processIdentityHelperHash],
  ]);
  const files = Array.isArray(manifest.files) ? manifest.files : [];
  if (files.length !== runtimeBundleRoles.length) {
    throw new Error("Durable terminal runtime bundle manifest has invalid files");
  }

  const resolvedFiles = Object.fromEntries(
    runtimeBundleRoles.map(([role, name]) => {
      const entry = files.find((candidate) => candidate?.role === role);
      const expectedHash = expectedHashes.get(role);
      if (
        entry?.name !== name ||
        entry?.sha256 !== expectedHash ||
        !sha256Pattern.test(expectedHash ?? "")
      ) {
        throw new Error("Durable terminal runtime bundle manifest file identity changed");
      }
      return [
        role,
        verifiedRuntimeFile(
          expectedDirectory,
          join(expectedDirectory, name),
          name,
          expectedHash,
        ),
      ];
    }),
  );

  return { directory: expectedDirectory, manifest, files: resolvedFiles };
}

export function materializeRuntimeBundle(
  stateDirectory,
  sourceDirectory = import.meta.dirname,
) {
  const assets = runtimeBundleRoles.map(([role, name]) => {
    const sourcePath = resolve(sourceDirectory, name);
    const info = lstatSync(sourcePath);
    if (!info.isFile() || info.isSymbolicLink()) {
      throw new Error(`Durable terminal runtime source is invalid for ${name}`);
    }
    const content = readFileSync(sourcePath);
    return { role, name, content, sha256: sha256Bytes(content) };
  });
  const hashFor = (role) =>
    assets.find((asset) => asset.role === role)?.sha256;
  const identity = {
    hostScriptHash: hashFor("host"),
    supervisorScriptHash: hashFor("supervisor"),
    processIdentityHelperHash: hashFor("processIdentityHelper"),
  };
  const bundleHash = runtimeBundleHash(identity);
  const runtimeRoot = join(
    resolve(stateDirectory),
    "terminal-runtime-bundles",
  );
  mkdirSync(runtimeRoot, { recursive: true });
  const bundleDirectory = containedStatePath(
    runtimeRoot,
    join(runtimeRoot, bundleHash),
  );
  if (!existsSync(bundleDirectory)) {
    const staging = containedStatePath(
      runtimeRoot,
      join(
        runtimeRoot,
        `${bundleHash}.${process.pid}.${randomToken(8)}.pending`,
      ),
    );
    try {
      mkdirSync(staging);
      containedStatePath(runtimeRoot, staging);
      assets.forEach((asset) =>
        writeFileSync(join(staging, asset.name), asset.content, {
          flush: true,
        }),
      );
      writeFileSync(
        join(staging, "bundle.json"),
        `${JSON.stringify(
          {
            version: terminalRuntimeBundleVersion,
            bundleHash,
            hostProtocolVersion,
            ...identity,
            supervisorProtocolGeneration: terminalJobProtocolGeneration,
            capabilities: terminalRuntimeCapabilities,
            files: assets.map(({ role, name, sha256 }) => ({
              role,
              name,
              sha256,
            })),
          },
          null,
          2,
        )}\n`,
        { flush: true },
      );
      try {
        renameSync(staging, bundleDirectory);
      } catch (error) {
        if (!existsSync(bundleDirectory)) throw error;
      }
    } finally {
      rmSync(staging, { recursive: true, force: true });
    }
  }
  const bundle = {
    stateDirectory: resolve(stateDirectory),
    bundleDirectory,
    bundleHash,
    ...identity,
  };
  verifyRuntimeBundle(bundle);
  return bundle;
}

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
    supervisorPid: session.supervisorPid ?? null,
    supervisorStartTimeUtcTicks:
      session.supervisorStartTimeUtcTicks ?? null,
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
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, {
    encoding: "utf8",
    flush: true,
  });
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
  const bundleHash =
    value?.version === hostProtocolVersion &&
    sha256Pattern.test(value?.bundleHash ?? "")
      ? value.bundleHash
      : null;

  return generation && pid && processStartTicks
    ? { generation, pid, processStartTicks, bundleHash }
    : null;
}

export function sameManifestOwner(left, right) {
  return (
    left?.generation === right?.generation &&
    left?.pid === right?.pid &&
    left?.processStartTicks === right?.processStartTicks &&
    left?.bundleHash === right?.bundleHash
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

const generationRecordOwnership = (value) => {
  const generation =
    value?.version === terminalGenerationRecordVersion &&
    typeof value?.generation === "string" &&
    value.generation
      ? value.generation
      : null;
  const hostPid =
    Number.isInteger(value?.hostPid) && value.hostPid > 0
      ? value.hostPid
      : null;
  const hostProcessStartTicks =
    typeof value?.hostProcessStartTicks === "string" &&
    /^\d+$/.test(value.hostProcessStartTicks)
      ? value.hostProcessStartTicks
      : null;
  const bundleHash =
    value?.version === terminalGenerationRecordVersion &&
    sha256Pattern.test(value?.bundleHash ?? "")
      ? value.bundleHash
      : null;
  return generation && hostPid && hostProcessStartTicks
    ? { generation, hostPid, hostProcessStartTicks, bundleHash }
    : null;
};

const sameGenerationRecordOwner = (left, right) =>
  left?.generation === right?.generation &&
  left?.hostPid === right?.hostPid &&
  left?.hostProcessStartTicks === right?.hostProcessStartTicks &&
  left?.bundleHash === right?.bundleHash;

function removeEmptyGenerationIfOwned(path, owner) {
  if (!existsSync(path)) return true;
  const claimedPath =
    `${path}.${process.pid}.${randomToken(6)}.reclaim.json`;
  try {
    renameSync(path, claimedPath);
  } catch (error) {
    return error?.code === "ENOENT";
  }

  try {
    const current = JSON.parse(readFileSync(claimedPath, "utf8"));
    if (
      !sameGenerationRecordOwner(
        generationRecordOwnership(current),
        owner,
      ) ||
      !Array.isArray(current.sessions) ||
      current.sessions.length !== 0
    ) {
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
      { encoding: "utf8", flush: true },
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

const runBoundedProcess = (
  fileName,
  argumentsList,
  description,
  timeoutMs = processForceCommandMs,
) =>
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
      if (child.exitCode === null && !child.kill() && child.exitCode === null) {
        finish(
          null,
          new Error(
            `Timed out running ${description} and could not stop ${fileName}`,
          ),
        );
      } else {
        finish(
          null,
          new Error(`Timed out running ${description} with ${fileName}`),
        );
      }
    }, timeoutMs);
    child.once("error", (error) => finish(null, error));
    child.once("exit", (code) => {
      if (outputBytes > maxControlBodyBytes) {
        finish(null, new Error(`${description} exceeded the output limit`));
      } else {
        finish({
          code,
          stdout: Buffer.concat(stdout).toString("utf8"),
          stderr: Buffer.concat(stderr).toString("utf8"),
        });
      }
    });
  });

const supervisorRequestId = () => randomToken(12);

const protocolFingerprint = (value) => {
  if (Array.isArray(value)) {
    return `[${value.map(protocolFingerprint).join(",")}]`;
  }
  if (value && typeof value === "object") {
    return `{${Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${protocolFingerprint(value[key])}`)
      .join(",")}}`;
  }
  return JSON.stringify(value);
};

export function jobSupervisorPolicyIsSafe(message) {
  return (
    message?.assignedBeforeResume === true &&
    message?.killOnJobClose === true &&
    message?.breakawayAllowed === false &&
    message?.silentBreakawayAllowed === false
  );
}

export function requireKernelTerminalOwnership(platform = process.platform) {
  if (platform !== "win32") {
    throw new Error(
      `Kernel-enforced durable terminal ownership is unsupported on ${platform}`,
    );
  }
}

export class TerminalJobSupervisor {
  constructor(child, token, requestTimeoutMs = processForceCommandMs) {
    this.child = child;
    this.token = token;
    this.requestTimeoutMs = requestTimeoutMs;
    this.sessionId = null;
    this.pending = new Map();
    this.requests = new Map();
    this.naturalEvents = new Map();
    this.exited = child.exitCode !== null || child.signalCode != null;
    this.boundaryFailure = null;
    this.protocolFailure = null;
    this.channelFailure = null;
    this.emptyAcknowledged = false;
    this.emptyPromise = new Promise((resolveEmpty) => {
      this.resolveEmpty = resolveEmpty;
    });
    this.exitPromise = new Promise((resolveExit) => {
      this.resolveExit = resolveExit;
    });
    this.outputClosed = !child.stdout;
    this.outputClosedPromise = new Promise((resolveOutputClosed) => {
      this.resolveOutputClosed = resolveOutputClosed;
    });
    if (this.outputClosed) this.resolveOutputClosed();
    child.stderr?.resume();
    this.lines = createInterface({ input: child.stdout });
    this.lines.on("line", (line) => this.handleLine(line));
    this.lines.once("close", () => {
      this.outputClosed = true;
      this.resolveOutputClosed();
      if (!this.exited && !this.emptyAcknowledged) {
        this.recordChannelFailure(
          new Error("Terminal Job Object supervisor control output closed"),
        );
      }
    });
    child.stdin?.on("error", (error) =>
      this.recordChannelFailure(error, "stdin"),
    );
    child.stdout?.on("error", (error) =>
      this.recordChannelFailure(error, "stdout"),
    );
    child.once("error", (error) => {
      this.exited = true;
      this.exitCode = null;
      this.exitSignal = null;
      this.resolveExit({ code: null, signal: null });
      this.fail(error);
    });
    child.once("exit", (code, signal) => {
      this.exited = true;
      this.exitCode = code;
      this.exitSignal = signal;
      this.resolveExit({ code, signal });
      this.fail(
        new Error(
          `Terminal Job Object supervisor exited with code ${code ?? "unknown"}`,
        ),
      );
    });
    if (this.exited) {
      this.exitCode = child.exitCode;
      this.exitSignal = child.signalCode;
      this.resolveExit({
        code: child.exitCode,
        signal: child.signalCode,
      });
    }
  }

  handleLine(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      this.recordProtocolFailure(
        new Error("Terminal Job Object supervisor returned invalid JSON"),
      );
      return;
    }

    if (!message || typeof message !== "object" || Array.isArray(message)) {
      this.recordProtocolFailure(
        new Error("Terminal Job Object supervisor returned an invalid message"),
      );
      return;
    }

    if (
      message?.token !== this.token ||
      message?.sessionId !== this.sessionId ||
      message?.protocolGeneration !== terminalJobProtocolGeneration
    ) {
      this.recordProtocolFailure(
        new Error("Terminal Job Object supervisor authentication changed"),
      );
      return;
    }

    if (this.protocolFailure) return;

    if (message.event === "exited") {
      if (
        message.empty === true &&
        Number.isInteger(message.rootExitCode)
      ) {
        if (!this.recordNaturalEvent("exited", message)) return;
        this.acknowledgeEmpty("exited");
      } else {
        this.recordProtocolFailure(
          new Error(
            "Terminal Job Object supervisor returned an invalid empty-job exit acknowledgement",
          ),
        );
      }
      return;
    }

    if (message.event === "boundary-failed") {
      if (
        typeof message.error !== "string" ||
        !message.error.trim() ||
        !this.recordNaturalEvent("boundary-failed", message)
      ) {
        if (!this.protocolFailure) {
          this.recordProtocolFailure(
            new Error(
              "Terminal Job Object supervisor returned an invalid boundary failure",
            ),
          );
        }
        return;
      }
      this.boundaryFailure =
        sanitizeMetadataText(message.error, 240) ||
        "Terminal Job Object boundary failed";
      this.onBoundaryFailure?.(this.boundaryFailure);
      return;
    }

    if (typeof message.requestId !== "string" || !message.requestId) {
      this.recordProtocolFailure(
        new Error(
          "Terminal Job Object supervisor returned an unsolicited protocol message",
        ),
      );
      return;
    }

    const request = this.requests.get(message.requestId);
    if (!request) {
      this.recordProtocolFailure(
        new Error(
          "Terminal Job Object supervisor returned an unsolicited protocol message",
        ),
      );
      return;
    }

    if (!this.responseMatchesRequest(request, message)) {
      this.recordProtocolFailure(
        new Error(
          "Terminal Job Object supervisor returned an invalid protocol response",
        ),
      );
      return;
    }

    const fingerprint = protocolFingerprint(message);
    if (request.responseFingerprint) {
      if (request.responseFingerprint !== fingerprint) {
        this.recordProtocolFailure(
          new Error(
            "Terminal Job Object supervisor returned contradictory duplicate responses",
          ),
        );
      }
      return;
    }
    request.responseFingerprint = fingerprint;

    const pending = this.pending.get(message.requestId);
    if (!pending) {
      this.acceptEmptyResponse(message);
      return;
    }
    this.pending.delete(message.requestId);
    clearTimeout(pending.timeout);

    this.acceptEmptyResponse(message);
    if (
      message.event === "request-failed" ||
      message.event === "start-failed" ||
      message.event === "terminate-failed" ||
      message.event === "startup-failure-empty"
    ) {
      pending.reject(
        new Error(
          sanitizeMetadataText(message.error, 240) ||
            "Terminal Job Object supervisor request failed",
        ),
      );
    } else {
      pending.resolve(message);
    }
  }

  recordNaturalEvent(kind, message) {
    const fingerprint = protocolFingerprint(message);
    const existing = this.naturalEvents.get(kind);
    if (!existing) {
      this.naturalEvents.set(kind, fingerprint);
      return true;
    }
    if (existing === fingerprint) return true;
    this.recordProtocolFailure(
      new Error(
        "Terminal Job Object supervisor returned contradictory duplicate events",
      ),
    );
    return false;
  }

  responseMatchesRequest(request, message) {
    const expectedEvents = {
      start: new Set([
        "ready",
        "start-failed",
        "startup-failure-empty",
        "request-failed",
      ]),
      contains: new Set(["contains", "request-failed"]),
      terminate: new Set([
        "terminated",
        "terminate-failed",
        "request-failed",
      ]),
      "startup-failed": new Set([
        "startup-failure-empty",
        "terminate-failed",
        "request-failed",
      ]),
    }[request.command];
    if (!expectedEvents?.has(message.event)) return false;

    if (
      ["request-failed", "start-failed", "terminate-failed"].includes(
        message.event,
      )
    ) {
      return typeof message.error === "string" && Boolean(message.error.trim());
    }
    if (message.event === "ready") {
      return (
        jobSupervisorPolicyIsSafe(message) &&
        validPid(message.ttydPid) &&
        message.supervisorPid === this.child.pid &&
        /^\d+$/.test(message.supervisorStartTimeUtcTicks ?? "")
      );
    }
    if (message.event === "contains") {
      return (
        message.processId === request.payload.processId &&
        typeof message.member === "boolean"
      );
    }
    if (message.event === "terminated") return message.empty === true;
    if (message.event === "startup-failure-empty") {
      return (
        message.empty === true &&
        message.supervisorPid === this.child.pid &&
        /^\d+$/.test(message.supervisorStartTimeUtcTicks ?? "") &&
        typeof message.error === "string" &&
        Boolean(message.error.trim())
      );
    }
    return false;
  }

  acceptEmptyResponse(message) {
    if (this.protocolFailure) return;
    if (message.event === "terminated") {
      this.acknowledgeEmpty("terminated");
    } else if (message.event === "startup-failure-empty") {
      this.supervisorPid = message.supervisorPid;
      this.supervisorStartTimeUtcTicks =
        message.supervisorStartTimeUtcTicks;
      this.acknowledgeEmpty("startup-failure");
    }
  }

  acknowledgeEmpty(source) {
    if (this.protocolFailure || this.emptyAcknowledged) return;
    this.emptyAcknowledged = true;
    this.emptyAcknowledgementSource = source;
    this.resolveEmpty();
  }

  recordProtocolFailure(error) {
    if (this.protocolFailure) return;
    this.protocolFailure = error;
    try {
      this.onProtocolFailure?.(this.protocolFailure);
    } catch (persistenceError) {
      this.protocolFailurePersistenceError ??= persistenceError;
    }
    this.fail(error);
  }

  recordChannelFailure(error, channel = "output") {
    if (channel === "stdin" && this.emptyAcknowledged) return;
    this.channelFailure ??= error;
    this.fail(error);
  }

  fail(error) {
    const pending = [...this.pending.values()];
    this.pending.clear();
    pending.forEach(({ reject, timeout }) => {
      clearTimeout(timeout);
      reject(error);
    });
  }

  request(command, payload = {}, timeoutMs = this.requestTimeoutMs) {
    if (this.protocolFailure) return Promise.reject(this.protocolFailure);
    if (this.channelFailure) return Promise.reject(this.channelFailure);
    if (this.exited) {
      return Promise.reject(
        new Error("Terminal Job Object supervisor already exited"),
      );
    }

    const requestId = supervisorRequestId();
    this.requests.set(requestId, {
      command,
      payload,
      responseFingerprint: null,
    });
    return new Promise((resolveRequest, rejectRequest) => {
      const timeout = setTimeout(() => {
        this.pending.delete(requestId);
        rejectRequest(
          new Error(
            `Timed out waiting for terminal Job Object supervisor ${command} acknowledgement`,
          ),
        );
      }, timeoutMs);
      this.pending.set(requestId, {
        resolve: resolveRequest,
        reject: rejectRequest,
        timeout,
      });

      const body = JSON.stringify({
        command,
        token: this.token,
        sessionId: this.sessionId,
        protocolGeneration: terminalJobProtocolGeneration,
        requestId,
        ...payload,
      });
      const rejectWrite = (error) => {
        if (!error) return;
        const pending = this.pending.get(requestId);
        if (!pending) return;
        this.pending.delete(requestId);
        clearTimeout(pending.timeout);
        pending.reject(error);
      };

      try {
        this.child.stdin.write(`${body}\n`, rejectWrite);
      } catch (error) {
        rejectWrite(error);
      }
    });
  }

  async start(options) {
    if (
      typeof options.sessionId !== "string" ||
      !/^[A-Za-z0-9_-]{16,128}$/.test(options.sessionId)
    ) {
      throw new Error("Terminal Job Object protocol session identity is invalid");
    }
    if (this.sessionId !== null && this.sessionId !== options.sessionId) {
      throw new Error("Terminal Job Object protocol session identity changed");
    }
    if (
      typeof options.generation !== "string" ||
      !/^[A-Za-z0-9_-]{1,128}$/.test(options.generation) ||
      typeof options.worktreePath !== "string" ||
      !isAbsolute(options.worktreePath) ||
      typeof options.witnessRoot !== "string" ||
      !isAbsolute(options.witnessRoot) ||
      typeof options.witnessPath !== "string" ||
      !isAbsolute(options.witnessPath) ||
      typeof options.witnessNonce !== "string" ||
      !/^[A-Za-z0-9_-]{24,128}$/.test(options.witnessNonce)
    ) {
      throw new Error("Terminal Job Object empty-witness metadata is invalid");
    }
    const witnessPath = containedStatePath(
      options.witnessRoot,
      options.witnessPath,
    );
    if (
      dirname(pathComparisonValue(witnessPath)) !==
        pathComparisonValue(resolve(options.witnessRoot)) ||
      pathComparisonValue(witnessPath.slice(witnessPath.lastIndexOf(sep) + 1)) !==
        pathComparisonValue(`${options.sessionId}.json`)
    ) {
      throw new Error("Terminal Job Object empty-witness path is not session-bound");
    }
    this.sessionId = options.sessionId;

    const ready = await this.request(
      "start",
      {
        fileName: options.fileName,
        arguments: options.argumentsList,
        workingDirectory: options.workingDirectory,
        environment: options.environment,
        generation: options.generation,
        worktreePath: options.worktreePath,
        witness: {
          root: resolve(options.witnessRoot),
          path: witnessPath,
          nonce: options.witnessNonce,
        },
        ...(options.testFailureStage
          ? { testFailureStage: options.testFailureStage }
          : {}),
      },
      options.timeoutMs,
    );
    if (
      ready.event !== "ready" ||
      !jobSupervisorPolicyIsSafe(ready) ||
      !validPid(ready.ttydPid) ||
      ready.supervisorPid !== this.child.pid ||
      !/^\d+$/.test(ready.supervisorStartTimeUtcTicks ?? "")
    ) {
      throw new Error(
        "Terminal Job Object supervisor did not prove assign-before-resume ownership",
      );
    }
    this.supervisorPid = ready.supervisorPid;
    this.supervisorStartTimeUtcTicks =
      ready.supervisorStartTimeUtcTicks;
    return ready;
  }

  async contains(processId, timeoutMs = this.requestTimeoutMs) {
    if (this.exited) return false;
    const response = await this.request(
      "contains",
      { processId },
      timeoutMs,
    );
    if (
      response.event !== "contains" ||
      response.processId !== processId ||
      typeof response.member !== "boolean"
    ) {
      throw new Error(
        "Terminal Job Object supervisor returned an invalid membership acknowledgement",
      );
    }
    return response.member;
  }

  async waitForTerminationEvidence(deadline) {
    while (!this.emptyAcknowledged || !this.exited || !this.outputClosed) {
      if (this.outputClosed && !this.emptyAcknowledged) break;
      const evidence = [
        deadline,
        ...(!this.emptyAcknowledged
          ? [this.emptyPromise.then(() => "empty")]
          : []),
        ...(!this.exited ? [this.exitPromise.then(() => "exit")] : []),
        ...(!this.outputClosed
          ? [this.outputClosedPromise.then(() => "output-closed")]
          : []),
      ];
      if ((await Promise.race(evidence)) === "timeout") break;
    }
  }

  terminationFailure(requestError) {
    if (this.protocolFailure) return this.protocolFailure;
    if (this.emptyAcknowledged && !this.exited) {
      return new Error(
        "Timed out waiting for terminal Job Object supervisor to exit after empty acknowledgement",
      );
    }

    if (this.exited) {
      return new Error(
        `Terminal Job Object supervisor exited with code ${this.exitCode ?? "unknown"} without an authenticated empty-job acknowledgement`,
      );
    }
    if (requestError) return requestError;
    if (this.channelFailure) return this.channelFailure;
    return new Error(
      "Timed out waiting for authenticated empty-job acknowledgement and supervisor exit",
    );
  }

  trustedEmptyEvidence() {
    const failures = [
      ...(this.protocolFailure ? ["protocol-failure"] : []),
      ...(this.protocolFailurePersistenceError
        ? ["quarantine-persistence-failure"]
        : []),
      ...(this.channelFailure ? ["channel-failure"] : []),
      ...(!this.emptyAcknowledged ? ["missing-empty"] : []),
      ...(!this.exited ? ["running"] : []),
      ...(!this.outputClosed ? ["output-open"] : []),
      ...(this.exitCode !== 0 ? ["nonzero-exit"] : []),
      ...(this.exitSignal != null ? ["signaled-exit"] : []),
      ...(!validPid(this.supervisorPid) ? ["missing-pid"] : []),
      ...(!/^\d+$/.test(this.supervisorStartTimeUtcTicks ?? "")
        ? ["missing-start-identity"]
        : []),
    ];
    if (failures.length > 0) {
      throw new Error(
        `Terminal Job Object supervisor did not complete a clean authenticated empty transcript (${failures.join(", ")})`,
      );
    }
    return {
      supervisorPid: this.supervisorPid,
      supervisorStartTimeUtcTicks: this.supervisorStartTimeUtcTicks,
      exitCode: this.exitCode,
      exitSignal: this.exitSignal,
      outputClosed: this.outputClosed,
    };
  }

  async terminateOnce(timeoutMs, command = "terminate", error = null) {
    if (this.protocolFailure) throw this.protocolFailure;
    const boundedTimeoutMs = Math.max(1, Math.floor(timeoutMs));
    let deadlineTimer;
    const deadline = new Promise((resolveDeadline) => {
      deadlineTimer = setTimeout(
        () => resolveDeadline("timeout"),
        boundedTimeoutMs,
      );
    });
    const requestOutcome = { error: null };

    if (!this.exited) {
      void this.request(
        command,
        {
          timeoutMilliseconds: boundedTimeoutMs,
          ...(error ? { error: sanitizeMetadataText(error, 240) } : {}),
        },
        boundedTimeoutMs,
      )
        .then((response) => {
          const expectedEvent =
            command === "startup-failed"
              ? "startup-failure-empty"
              : "terminated";
          if (response.event !== expectedEvent || response.empty !== true) {
            throw new Error(
              "Terminal Job Object supervisor did not acknowledge an empty job",
            );
          }
        })
        .catch((error) => {
          requestOutcome.error = error;
        });
    }

    try {
      await this.waitForTerminationEvidence(deadline);
    } finally {
      clearTimeout(deadlineTimer);
    }
    if (this.protocolFailure) throw this.protocolFailure;
    if (this.emptyAcknowledged && this.exited && this.outputClosed) return;
    await Promise.resolve();
    if (this.protocolFailure) throw this.protocolFailure;
    throw this.terminationFailure(requestOutcome.error);
  }

  async terminateWith(command, timeoutMs, error = null) {
    if (this.terminationAttempt) return this.terminationAttempt;
    const attempt = this.terminateOnce(timeoutMs, command, error);
    this.terminationAttempt = attempt;
    try {
      return await attempt;
    } finally {
      if (this.terminationAttempt === attempt) {
        this.terminationAttempt = null;
      }
    }
  }

  terminate(timeoutMs) {
    return this.terminateWith("terminate", timeoutMs);
  }

  terminateStartupFailure(timeoutMs, error) {
    return this.terminateWith(
      "startup-failed",
      timeoutMs,
      error || "Terminal startup failed",
    );
  }
}

export function createTerminalJobSupervisor({
  spawnProcess = spawn,
  supervisorPath = terminalJobSupervisorPath,
  supervisorHash,
  bundleDirectory,
  requestTimeoutMs = processForceCommandMs,
  environment = process.env,
} = {}) {
  requireKernelTerminalOwnership();

  const verifiedSupervisorPath =
    supervisorHash && bundleDirectory
      ? verifiedRuntimeFile(
          bundleDirectory,
          supervisorPath,
          "terminal-job-supervisor.ps1",
          supervisorHash,
        )
      : supervisorPath;
  const token = randomToken();
  const child = spawnProcess(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      verifiedSupervisorPath,
    ],
    {
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
      env: environment,
    },
  );
  return new TerminalJobSupervisor(child, token, requestTimeoutMs);
}

const parseWindowsProcessIdentities = (output) =>
  output
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      const [pidText, parentText, startText] = line.trim().split("|");
      const pid = Number.parseInt(pidText, 10);
      const parentPid = Number.parseInt(parentText, 10);
      if (!validPid(pid) || !Number.isInteger(parentPid) || !/^\d+$/.test(startText)) {
        throw new Error("Process ownership helper returned an invalid identity");
      }
      return {
        pid,
        parentPid,
        startIdentity: `windows:${startText}`,
      };
    });

const runWindowsProcessIdentityHelper = async (
  operation,
  identity,
  timeoutMs = processForceCommandMs,
  helperPath = processIdentityHelperPath,
) => {
  const boundedTimeoutMs = Math.max(1, Math.floor(timeoutMs));
  const startIdentity = /^windows:(\d+)$/.exec(
    identity?.startIdentity ?? "",
  )?.[1];
  if (!validPid(identity?.pid) || (operation !== "Inspect" && !startIdentity)) {
    throw new Error(
      `${operation} requires an exact Windows process creation identity`,
    );
  }

  const argumentsList = [
    "-NoProfile",
    "-NonInteractive",
    "-File",
    helperPath,
    "-Operation",
    operation,
    "-ProcessId",
    String(identity.pid),
    "-TimeoutMilliseconds",
    String(boundedTimeoutMs),
    ...(startIdentity
      ? ["-StartTimeUtcTicks", startIdentity]
      : []),
  ];
  const result = await runBoundedProcess(
    "pwsh",
    argumentsList,
    `identity-bound process ${operation.toLowerCase()}`,
    boundedTimeoutMs,
  );
  if (result.code === 3) return null;
  if (result.code !== 0) {
    throw new Error(
      `Identity-bound process ${operation.toLowerCase()} failed with code ${result.code}: ${sanitizeMetadataText(result.stderr, 240)}`,
    );
  }

  return parseWindowsProcessIdentities(result.stdout);
};

const inspectWindowsProcess = async (
  pid,
  timeoutMs,
  helperPath = processIdentityHelperPath,
) => {
  const identities = await runWindowsProcessIdentityHelper(
    "Inspect",
    { pid },
    timeoutMs,
    helperPath,
  );
  if (identities === null) return null;
  if (identities.length !== 1 || identities[0].pid !== pid) {
    throw new Error("Process ownership helper returned an unexpected identity");
  }
  return identities[0];
};

const terminateWindowsProcess = async (
  identity,
  timeoutMs,
  helperPath = processIdentityHelperPath,
) =>
  (await runWindowsProcessIdentityHelper(
    "Terminate",
    identity,
    timeoutMs,
    helperPath,
  )) !== null;

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

export function defaultProcessController({
  processHelperPath = processIdentityHelperPath,
  processHelperHash,
  bundleDirectory,
} = {}) {
  const pinnedHelperPath = () =>
    processHelperHash && bundleDirectory
      ? verifiedRuntimeFile(
          bundleDirectory,
          processHelperPath,
          "terminate-owned-process.ps1",
          processHelperHash,
        )
      : processHelperPath;
  const inspect =
    process.platform === "win32"
      ? async (pid, timeoutMs = processForceCommandMs) =>
          validPid(pid)
            ? inspectWindowsProcess(pid, timeoutMs, pinnedHelperPath())
            : null
      : process.platform === "linux"
        ? async (pid) => (validPid(pid) ? linuxProcessIdentity(pid) : null)
        : async () => {
            throw new Error(
              `Durable terminal ownership inspection is unsupported on ${process.platform}`,
            );
          };
  return {
    inspect,
    terminate:
      process.platform === "win32"
        ? (identity, timeoutMs) =>
            terminateWindowsProcess(
              identity,
              timeoutMs,
              pinnedHelperPath(),
            )
        : async () => {
            throw new Error(
              `Identity-bound process termination is unsupported on ${process.platform}`,
            );
          },
  };
}

const spawnedProcessExited = (child) =>
  child?.exitCode !== null || child?.signalCode != null;

export async function captureSpawnedProcessIdentity(
  child,
  inspect,
  wait = delay,
  timeoutMs = 2000,
  spawnFailure = () => null,
  now = () => Date.now(),
) {
  const pid = child?.pid;
  if (!validPid(pid)) throw new Error("Spawned process did not report a PID");
  const deadline = now() + timeoutMs;

  const capture = async () => {
    const failure = spawnFailure();
    if (failure) throw failure;
    if (spawnedProcessExited(child)) {
      throw new Error(`Spawned process ${pid} exited during identity capture`);
    }

    const remainingMs = deadline - now();
    if (remainingMs <= 0) {
      throw new Error("Could not capture spawned process creation identity");
    }
    const identity = await inspect(pid, remainingMs);
    const failureAfterInspection = spawnFailure();
    if (failureAfterInspection) throw failureAfterInspection;
    if (spawnedProcessExited(child)) {
      throw new Error(`Spawned process ${pid} exited during identity capture`);
    }
    if (identity?.pid === pid) return identity;
    if (now() >= deadline) {
      throw new Error("Could not capture spawned process creation identity");
    }
    await wait(Math.min(25, Math.max(1, deadline - now())));
    return capture();
  };

  return capture();
}

export async function terminateRetainedChild(
  child,
  timeoutMs = processForceCommandMs,
) {
  if (!child || spawnedProcessExited(child)) return;
  const exited = once(child, "exit").then(() => true);
  if (!child.kill("SIGKILL") && !spawnedProcessExited(child)) {
    throw new Error("Retained child process handle could not be terminated");
  }
  const completed = await Promise.race([
    exited,
    delay(timeoutMs).then(() => false),
  ]);
  if (!completed && !spawnedProcessExited(child)) {
    throw new Error("Timed out waiting for retained child process to exit");
  }
}

export class DurableTerminalHost {
  constructor(options) {
    this.options = options;
    this.startedAt = timestamp();
    if (options.generation && !isValidGeneration(options.generation)) {
      throw new Error("Durable terminal host generation is invalid");
    }
    this.generation = options.generation ?? randomToken(16);
    this.runtimeBundle = options.runtimeBundle
      ? verifyRuntimeBundle({
          stateDirectory: options.stateDirectory,
          ...options.runtimeBundle,
        })
      : null;
    if (this.runtimeBundle) {
      const loadedHostHash = sha256Bytes(
        readFileSync(fileURLToPath(import.meta.url)),
      );
      if (loadedHostHash !== options.runtimeBundle.hostScriptHash) {
        throw new Error(
          "Loaded durable terminal host does not match its runtime bundle",
        );
      }
    }
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
    this.supervisorFactory =
      options.supervisorFactory ??
      (() =>
        createTerminalJobSupervisor({
          supervisorPath: this.runtimeBundle?.files.supervisor,
          supervisorHash: options.runtimeBundle?.supervisorScriptHash,
          bundleDirectory: this.runtimeBundle?.directory,
        }));
    this.processController =
      options.processController ??
      defaultProcessController(
        this.runtimeBundle
          ? {
              processHelperPath:
                this.runtimeBundle.files.processIdentityHelper,
              processHelperHash:
                options.runtimeBundle.processIdentityHelperHash,
              bundleDirectory: this.runtimeBundle.directory,
            }
          : {},
      );
    this.wait = options.wait ?? delay;
    this.now = options.now ?? (() => Date.now());
    this.reservationLeaseMs =
      options.reservationLeaseMs ?? reservationLeaseMs;
    this.cleanupTimeouts = {
      graceful: options.cleanupTimeouts?.graceful ?? gracefulProcessExitMs,
      forced: options.cleanupTimeouts?.forced ?? forcedProcessExitMs,
    };
    this.exitProcess = options.exitProcess ?? ((code) => process.exit(code));
    const stateDirectory = resolve(options.stateDirectory);
    this.statePath = containedStatePath(
      stateDirectory,
      join(stateDirectory, "host.json"),
    );
    this.statusPath = containedStatePath(
      stateDirectory,
      join(stateDirectory, "status.json"),
    );
    this.lockPath = containedStatePath(
      stateDirectory,
      join(stateDirectory, "host.lock"),
    );
    this.generationDirectory = containedStatePath(
      stateDirectory,
      join(stateDirectory, "terminal-generations"),
    );
    this.generationPath = containedStatePath(
      this.generationDirectory,
      join(this.generationDirectory, `${this.generation}.json`),
    );
    const witnessRoot = containedStatePath(
      stateDirectory,
      join(stateDirectory, "terminal-empty-witnesses"),
    );
    this.witnessDirectory = containedStatePath(
      witnessRoot,
      join(witnessRoot, this.generation),
    );
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
      bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
    };
  }

  manifest() {
    return {
      version: hostProtocolVersion,
      generation: this.generation,
      pid: process.pid,
      processStartTicks: this.processStartTicks,
      processStartExact: Boolean(this.options.generation),
      ownershipBoundary:
        process.platform === "win32"
          ? terminalOwnershipBoundary
          : "unsupported",
      bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
      hostScriptHash: this.options.runtimeBundle?.hostScriptHash ?? null,
      supervisorScriptHash:
        this.options.runtimeBundle?.supervisorScriptHash ?? null,
      processIdentityHelperHash:
        this.options.runtimeBundle?.processIdentityHelperHash ?? null,
      supervisorProtocolGeneration: terminalJobProtocolGeneration,
      capabilities: terminalRuntimeCapabilities,
      controlPort: this.controlPort,
      controlToken: this.controlToken,
      startedAt: this.startedAt,
    };
  }

  generationOwner() {
    return {
      generation: this.generation,
      hostPid: process.pid,
      hostProcessStartTicks: this.processStartTicks,
      bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
    };
  }

  generationRecord() {
    return {
      version: terminalGenerationRecordVersion,
      hostProtocolVersion,
      generation: this.generation,
      hostPid: process.pid,
      hostProcessStartTicks: this.processStartTicks,
      hostProcessStartExact: Boolean(this.options.generation),
      ownershipBoundary:
        process.platform === "win32"
          ? terminalOwnershipBoundary
          : "unsupported",
      bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
      hostScriptHash: this.options.runtimeBundle?.hostScriptHash ?? null,
      supervisorScriptHash:
        this.options.runtimeBundle?.supervisorScriptHash ?? null,
      processIdentityHelperHash:
        this.options.runtimeBundle?.processIdentityHelperHash ?? null,
      supervisorProtocolGeneration: terminalJobProtocolGeneration,
      capabilities: terminalRuntimeCapabilities,
      startedAt: this.startedAt,
      sessions: [...this.sessions.values()]
        .sort((left, right) => left.order - right.order)
        .map((session) => ({
          sessionId: session.id,
          worktreePath: session.worktreePath,
          witnessTokenHash: witnessTokenHash(session.witnessNonce),
          supervisorPid: session.supervisorPid ?? null,
          supervisorStartTimeUtcTicks:
            session.supervisorStartTimeUtcTicks ?? null,
          supervisorState: session.supervisorTrustState,
          supervisorExited:
            session.supervisorTrustState === "trusted-empty"
              ? session.supervisorExitCode !== undefined
              : false,
          supervisorExitCode:
            session.supervisorTrustState === "trusted-empty"
              ? session.supervisorExitCode
              : null,
          supervisorExitSignal:
            session.supervisorTrustState === "trusted-empty"
              ? session.supervisorExitSignal ?? null
              : null,
          supervisorOutputClosed:
            session.supervisorTrustState === "trusted-empty"
              ? Boolean(session.supervisorOutputClosed)
              : false,
        })),
    };
  }

  ensureGenerationCapacity() {
    mkdirSync(this.generationDirectory, { recursive: true });
    assertNoReparsePoint(
      this.options.stateDirectory,
      this.generationDirectory,
    );
    const records = readdirSync(this.generationDirectory)
      .filter((name) => name.endsWith(".json"));
    if (
      records.some(
        (name) => !isValidGeneration(name.slice(0, -".json".length)),
      )
    ) {
      throw new Error(
        "Durable terminal generation directory contains invalid evidence",
      );
    }
    if (
      existsSync(this.generationPath) &&
      lstatSync(this.generationPath).isSymbolicLink()
    ) {
      throw new Error("Durable terminal generation evidence is a reparse point");
    }
    if (
      !records.includes(`${this.generation}.json`) &&
      records.length >= maximumGenerationRecords
    ) {
      throw new Error(
        `Durable terminal generation retention reached ${maximumGenerationRecords} unresolved records; verify or manually drain retired terminal generations before starting another host`,
      );
    }
  }

  persistGeneration() {
    this.ensureGenerationCapacity();
    atomicWriteJson(this.generationPath, this.generationRecord());
  }

  witnessPath(session) {
    if (!/^[A-Za-z0-9_-]{16,128}$/.test(session.id ?? "")) {
      throw new Error("Durable terminal session identity is invalid");
    }
    return containedStatePath(
      this.witnessDirectory,
      join(this.witnessDirectory, `${session.id}.json`),
    );
  }

  removeSessionWitness(session) {
    rmSync(this.witnessPath(session), { force: true });
    try {
      rmSync(this.witnessDirectory);
    } catch (error) {
      if (!["ENOENT", "ENOTEMPTY"].includes(error?.code)) throw error;
    }
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
        supervisorPid: session.supervisorPid ?? null,
        supervisorStartTimeUtcTicks:
          session.supervisorStartTimeUtcTicks ?? null,
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
      bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
      hostScriptHash: this.options.runtimeBundle?.hostScriptHash ?? null,
      supervisorScriptHash:
        this.options.runtimeBundle?.supervisorScriptHash ?? null,
      processIdentityHelperHash:
        this.options.runtimeBundle?.processIdentityHelperHash ?? null,
      supervisorProtocolGeneration: terminalJobProtocolGeneration,
      capabilities: terminalRuntimeCapabilities,
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
      assertNoReparsePoint(
        this.options.stateDirectory,
        this.generationDirectory,
      );
      if (this.options.generation && !this.runtimeBundle) {
        throw new Error(
          "Durable terminal host startup requires an immutable runtime bundle",
        );
      }
      this.controlPort = await listenLoopback(this.controlServer);
      await this.acceptStartupClaim();
      this.persistGeneration();
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
        ownershipBoundary:
          process.platform === "win32"
            ? terminalOwnershipBoundary
            : "unsupported",
        bundleHash: this.options.runtimeBundle?.bundleHash ?? null,
        hostScriptHash: this.options.runtimeBundle?.hostScriptHash ?? null,
        supervisorScriptHash:
          this.options.runtimeBundle?.supervisorScriptHash ?? null,
        processIdentityHelperHash:
          this.options.runtimeBundle?.processIdentityHelperHash ?? null,
        supervisorProtocolGeneration: terminalJobProtocolGeneration,
        capabilities: terminalRuntimeCapabilities,
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
      resourcesStopped: false,
      witnessNonce: randomToken(),
      jobSupervisor: null,
      supervisorProcess: null,
      supervisorPid: null,
      supervisorStartTimeUtcTicks: null,
      supervisorTrustState: "in-progress",
      supervisorExitCode: undefined,
      supervisorExitSignal: undefined,
      supervisorOutputClosed: false,
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
    this.persistGeneration();
    this.persistStatus();
    this.record("session-starting", session);

    try {
      await this.startSessionProxy(session);
      const readinessError = this.startupReadinessError(session);
      if (readinessError) throw new Error(readinessError);
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
      this.synchronizeSupervisorEvidence(session);
      if (!session.failureRecorded) {
        session.failureRecorded = true;
        session.error = this.startFailureMessage(error);
      }
      session.state = "failed";
      this.record("session-start-failed", session, {
        errorType: sanitizeMetadataText(error?.name || "Error", 80),
      });
      const cleanupError = await this.stopFailedSessionResources(
        session,
        error,
      );
      this.synchronizeSupervisorEvidence(session);
      if (cleanupError) {
        session.error = `Terminal startup failed and owned process cleanup did not complete: ${sanitizeMetadataText(cleanupError.message, 240)}`;
        this.record("session-start-cleanup-failed", session, {
          supervisorPid: session.supervisorPid,
          ttydPid: session.ttydPid,
        });
      }
      this.persistStatus();
    }

    return session;
  }

  synchronizeSupervisorEvidence(session) {
    const supervisorPid =
      session.jobSupervisor?.supervisorPid ??
      session.supervisorProcess?.pid ??
      session.supervisorPid;
    const supervisorStartTimeUtcTicks =
      session.jobSupervisor?.supervisorStartTimeUtcTicks ??
      session.supervisorStartTimeUtcTicks;
    const changed =
      session.supervisorPid !== supervisorPid ||
      session.supervisorStartTimeUtcTicks !==
        supervisorStartTimeUtcTicks;
    session.supervisorPid = validPid(supervisorPid)
      ? supervisorPid
      : null;
    session.supervisorStartTimeUtcTicks =
      typeof supervisorStartTimeUtcTicks === "string" &&
      /^\d+$/.test(supervisorStartTimeUtcTicks)
        ? supervisorStartTimeUtcTicks
        : null;
    if (changed) this.persistGeneration();
  }

  quarantineSupervisor(session, error) {
    session.supervisorTrustState = "quarantined";
    try {
      this.persistGeneration();
    } catch (persistenceError) {
      try {
        this.record("terminal-supervisor-quarantine-persist-failed", session, {
          errorType: sanitizeMetadataText(
            persistenceError?.name || "Error",
            80,
          ),
        });
      } catch {
        // The in-memory quarantine and on-disk in-progress state both fail closed.
      }
    }
    if (error && !session.error) {
      session.error = sanitizeMetadataText(error.message, 240);
    }
  }

  validateEmptyWitness(session, transcript) {
    const path = this.witnessPath(session);
    if (!existsSync(path)) {
      throw new Error(
        "Terminal Job Object supervisor exited without its durable empty witness",
      );
    }
    const info = lstatSync(path);
    if (!info.isFile() || info.isSymbolicLink() || info.size > 1024 * 1024) {
      throw new Error("Terminal Job Object empty witness is not a bounded regular file");
    }

    let witness;
    try {
      witness = JSON.parse(readFileSync(path, "utf8"));
    } catch {
      throw new Error("Terminal Job Object empty witness is invalid");
    }
    if (
      witness?.version !== 1 ||
      witness.generation !== this.generation ||
      witness.sessionId !== session.id ||
      typeof witness.worktreePath !== "string" ||
      worktreeKey(witness.worktreePath) !== session.key ||
      witness.nonce !== session.witnessNonce ||
      witness.supervisorPid !== transcript.supervisorPid ||
      witness.supervisorStartTimeUtcTicks !==
        transcript.supervisorStartTimeUtcTicks
    ) {
      throw new Error(
        "Terminal Job Object empty witness does not match the exact session identity",
      );
    }
  }

  promoteSupervisorTrustedEmpty(session) {
    if (session.supervisorTrustState === "quarantined") {
      throw new Error(
        "Terminal Job Object supervisor transcript is quarantined",
      );
    }
    const transcript = session.jobSupervisor?.trustedEmptyEvidence();
    if (!transcript) {
      throw new Error(
        "Terminal Job Object supervisor omitted terminal empty evidence",
      );
    }
    this.synchronizeSupervisorEvidence(session);
    if (
      session.supervisorPid !== transcript.supervisorPid ||
      session.supervisorStartTimeUtcTicks !==
        transcript.supervisorStartTimeUtcTicks
    ) {
      throw new Error(
        "Terminal Job Object supervisor exit identity changed before promotion",
      );
    }
    this.validateEmptyWitness(session, transcript);

    session.supervisorTrustState = "trusted-empty";
    session.supervisorExitCode = transcript.exitCode;
    session.supervisorExitSignal = transcript.exitSignal;
    session.supervisorOutputClosed = transcript.outputClosed;
    try {
      this.persistGeneration();
    } catch (error) {
      session.supervisorTrustState = "quarantined";
      session.supervisorExitCode = undefined;
      session.supervisorExitSignal = undefined;
      session.supervisorOutputClosed = false;
      try {
        this.persistGeneration();
      } catch {
        // The previous durable in-progress state remains untrusted.
      }
      throw new Error(
        `Could not promote terminal supervisor cleanup trust: ${sanitizeMetadataText(error.message, 240)}`,
      );
    }
  }

  startupReadinessError(session) {
    if (
      this.sessions.get(session.id) !== session ||
      this.sessionForKey(session.key) !== session
    ) {
      return "Terminal session ownership changed during startup";
    }
    if (session.failureRecorded) {
      return session.error ?? "Terminal startup was interrupted";
    }
    if (
      !session.jobSupervisor ||
      !session.supervisorProcess ||
      session.jobSupervisor.exited
    ) {
      return "Terminal Job Object supervisor exited before startup completed";
    }
    if (!validPid(session.shellPid)) {
      return "PowerShell process identity was not ready";
    }
    if (
      session.upstream?.readyState !== WebSocket.OPEN ||
      session.upstreamClosedAt
    ) {
      return "ttyd WebSocket was not open when terminal startup completed";
    }
    if (!session.publicServer?.listening) {
      return "Terminal public server stopped listening during startup";
    }
    return null;
  }

  startFailureMessage(error) {
    const message = String(error?.message ?? "");
    if (error?.code === "ENOENT") return "ttyd could not be started";
    if (
      message.includes("Timed out") ||
      message.includes("ttyd exited with code") ||
      message.includes("Job Object") ||
      message.includes("Kernel-enforced") ||
      message.includes("startup was interrupted") ||
      [
        "Terminal session ownership changed during startup",
        "Terminal Job Object supervisor exited before startup completed",
        "PowerShell process identity was not ready",
        "ttyd WebSocket was not open when terminal startup completed",
        "Terminal public server stopped listening during startup",
      ].includes(message)
    ) {
      return sanitizeMetadataText(message, 240);
    }
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
      "-Command",
      shellScript,
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

    mkdirSync(this.witnessDirectory, { recursive: true });
    assertNoReparsePoint(
      join(this.options.stateDirectory, "terminal-empty-witnesses"),
      this.witnessDirectory,
    );
    session.jobSupervisor = this.supervisorFactory();
    session.supervisorProcess = session.jobSupervisor.child;
    session.supervisorPid = validPid(session.supervisorProcess?.pid)
      ? session.supervisorProcess.pid
      : null;
    session.jobSupervisor.onProtocolFailure = (error) => {
      this.quarantineSupervisor(session, error);
    };
    this.persistGeneration();
    session.supervisorProcess.once("error", (error) => {
      spawnFailure.error = error;
    });
    session.supervisorProcess.once("exit", (code, signal) => {
      if (!spawnFailure.error && code !== 0) {
        spawnFailure.error = new Error(
          `Terminal Job Object supervisor exited with code ${code ?? "unknown"}`,
        );
      }
      this.record("terminal-supervisor-exited", session, {
        supervisorPid: session.supervisorPid,
        ttydPid: session.ttydPid,
        exitCode: code,
        signal: sanitizeMetadataText(signal, 32),
      });
      if (!session.closing) {
        void this.interruptSession(
          session,
          `Terminal Job Object supervisor exited with code ${code ?? "unknown"}`,
        );
      }
    });
    session.jobSupervisor.onBoundaryFailure = (error) => {
      if (!session.closing) void this.interruptSession(session, error);
    };
    const ownership = await session.jobSupervisor.start({
      sessionId: session.id,
      generation: this.generation,
      worktreePath: session.worktreePath,
      witnessRoot: this.witnessDirectory,
      witnessPath: this.witnessPath(session),
      witnessNonce: session.witnessNonce,
      fileName: this.options.ttydPath,
      argumentsList: ttydArguments,
      workingDirectory: session.worktreePath,
      environment: {
        TMTP: session.pidFile,
        TMTW: session.worktreePath,
        TREEMON_TERMINAL_WORKTREE: session.worktreePath,
        TREEMON_TERMINAL_SESSION_ID: session.id,
      },
      timeoutMs: 10_000,
    });
    session.ttydPid = ownership.ttydPid;
    session.supervisorPid = ownership.supervisorPid;
    session.supervisorStartTimeUtcTicks =
      ownership.supervisorStartTimeUtcTicks;
    this.persistGeneration();
    session.publicPort = await listenLoopback(session.publicServer);

    const ttydHttp = `http://127.0.0.1:${session.ttydPort}/`;
    await waitForTtyd(
      ttydHttp,
      session.supervisorProcess,
      spawnFailure,
      10_000,
    );
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
    const deadline = this.now() + 5000;

    const read = async () => {
      if (session.jobSupervisor?.exited) {
        throw new Error(
          `Terminal Job Object supervisor exited with code ${session.jobSupervisor.exitCode ?? "unknown"}`,
        );
      }

      if (existsSync(session.pidFile)) {
        const pid = Number.parseInt(readFileSync(session.pidFile, "utf8").trim(), 10);
        if (
          validPid(pid) &&
          (await session.jobSupervisor.contains(
            pid,
            Math.max(1, deadline - this.now()),
          ))
        ) {
          return pid;
        }
      }

      const remainingMs = deadline - this.now();
      if (remainingMs <= 0) {
        throw new Error("Timed out waiting for PowerShell process identity");
      }

      await this.wait(Math.min(50, remainingMs));
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
      if (!session.closing) {
        void this.interruptSession(
          session,
          `ttyd WebSocket failed: ${sanitizeMetadataText(error?.message || "unknown error", 160)}`,
        );
      }
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

  interruptSession(session, error) {
    if (
      session.closing ||
      session.failureRecorded ||
      this.sessions.get(session.id) !== session
    ) {
      return Promise.resolve();
    }
    session.failureRecorded = true;
    session.state = "failed";
    session.error = error;
    const attachment = session.attachment;
    session.attachment = null;
    attachment?.socket?.close(1011, "Terminal session interrupted");
    session.browserWebSockets?.close();
    this.persistStatus();
    this.record("session-interrupted", session, {
      ttydAlive: isPidAlive(session.ttydPid),
      shellAlive: isPidAlive(session.shellPid),
    });

    return this.withKeyTransition(session.key, async () => {
      if (this.sessions.get(session.id) !== session) return;
      const cleanupError = await this.stopFailedSessionResources(session);
      if (cleanupError) {
        session.error = `Terminal interruption cleanup did not complete: ${sanitizeMetadataText(cleanupError.message, 240)}`;
        this.record("session-interrupt-cleanup-failed", session, {
          supervisorPid: session.supervisorPid,
          ttydPid: session.ttydPid,
        });
        this.persistStatus();
      }
    });
  }

  async stopFailedSessionResources(session, startupFailure = null) {
    if (session.resourcesStopped) return null;
    session.closing = true;
    try {
      await this.stopSessionResources(session, startupFailure);
      session.resourcesStopped = true;
      return null;
    } catch (error) {
      return error;
    } finally {
      session.closing = false;
    }
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
      this.persistGeneration();
      try {
        this.removeSessionWitness(session);
      } catch {
        this.record("session-witness-cleanup-deferred", session);
      }
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
        supervisorPid: session.supervisorPid,
        ttydPid: session.ttydPid,
      });
      throw new Error(session.error);
    }
  }

  async stopSessionResources(session, startupFailure = null) {
    const upstreamCloseAllowance =
      session.upstream &&
      session.upstream.readyState !== WebSocket.CLOSED
        ? 2000
        : 0;
    const cleanupDeadline =
      this.now() +
      upstreamCloseAllowance +
      this.cleanupTimeouts.graceful +
      this.cleanupTimeouts.forced;
    const remaining = () =>
      Math.max(0, Math.floor(cleanupDeadline - this.now()));

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
        this.wait(Math.min(2000, remaining())).then(() => false),
      ]);
      if (!closed) session.upstream.terminate();
    }

    if (session.jobSupervisor) {
      const timeoutMs = remaining();
      if (timeoutMs <= 0) {
        throw new Error(
          "Timed out before requesting terminal Job Object termination",
        );
      }
      if (startupFailure) {
        await session.jobSupervisor.terminateStartupFailure(
          timeoutMs,
          startupFailure.message,
        );
      } else {
        await session.jobSupervisor.terminate(timeoutMs);
      }
      this.promoteSupervisorTrustedEmpty(session);
    } else if (validPid(session.ttydPid) || validPid(session.shellPid)) {
      throw new Error(
        "Terminal process ownership has no Job Object supervisor acknowledgement",
      );
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
      if (
        session.state !== "running" ||
        session.upstream?.readyState !== WebSocket.OPEN
      ) {
        return;
      }
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
    if (
      !removeEmptyGenerationIfOwned(
        this.generationPath,
        this.generationOwner(),
      )
    ) {
      this.record("host-generation-compaction-deferred", null, { reason });
    }
    try {
      rmSync(this.witnessDirectory);
    } catch (error) {
      if (!["ENOENT", "ENOTEMPTY"].includes(error?.code)) {
        this.record("host-witness-compaction-deferred", null, { reason });
      }
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
  const runtimeBundleValues = {
    bundleDirectory: resolve(String(values["runtime-bundle-dir"] ?? "")),
    bundleHash: String(values["runtime-bundle-hash"] ?? ""),
    hostScriptHash: String(values["host-script-hash"] ?? ""),
    supervisorScriptHash: String(values["supervisor-script-hash"] ?? ""),
    processIdentityHelperHash: String(
      values["process-helper-hash"] ?? "",
    ),
  };

  if (!values["state-dir"]) throw new Error("--state-dir is required");
  if (!values.ttyd) throw new Error("--ttyd is required");
  if (
    !values["runtime-bundle-dir"] ||
    !sha256Pattern.test(runtimeBundleValues.bundleHash) ||
    !sha256Pattern.test(runtimeBundleValues.hostScriptHash) ||
    !sha256Pattern.test(runtimeBundleValues.supervisorScriptHash) ||
    !sha256Pattern.test(runtimeBundleValues.processIdentityHelperHash)
  ) {
    throw new Error("Immutable durable terminal runtime bundle is required");
  }

  return {
    stateDirectory,
    ttydPath,
    shellCommand,
    generation:
      typeof values.generation === "string" ? values.generation : undefined,
    runtimeBundle: runtimeBundleValues,
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
  requireKernelTerminalOwnership();
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
