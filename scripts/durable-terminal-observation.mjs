import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  renameSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import {
  defaultProcessController,
  materializeRuntimeBundle,
  sameProcessIdentity,
  terminateRetainedChild,
} from "./durable-terminal-host.mjs";

const repo = resolve(import.meta.dirname, "..");
const hostScript = join(repo, "scripts", "durable-terminal-host.mjs");
const ttyd = join(repo, ".tools", "ttyd", "1.7.7", "ttyd.exe");
const stateDirectory = join(repo, ".agents", "durable-terminal-observation");
const worktree = join(stateDirectory, "worktree");
const hostStatePath = join(stateDirectory, "host.json");
const observationPath = join(stateDirectory, "observation.json");
const diagnosticsPath = join(stateDirectory, "diagnostics.jsonl");
const lockPath = join(stateDirectory, "host.lock");
const durationMs = 24 * 60 * 60 * 1000;
const staleHeartbeatMs = 2 * 60 * 1000;
const processController = defaultProcessController();

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const processIsAlive = async (pid) =>
  Boolean(await processController.inspect(pid));

async function waitFor(description, predicate, timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await delay(100);
  }

  throw new Error(`Timed out waiting for ${description}`);
}

function atomicWrite(path, value) {
  const temporaryPath = `${path}.${process.pid}.tmp`;
  writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  renameSync(temporaryPath, path);
}

function readJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function publicObservation(observation) {
  const {
    controlToken,
    attachmentCapability,
    ...safeObservation
  } = observation;
  return safeObservation;
}

export function hostStateMatchesObservation(observation, hostState) {
  return (
    hostState?.version === observation.hostProtocolVersion &&
    hostState?.generation === observation.hostGeneration &&
    hostState?.bundleHash === observation.hostBundleHash &&
    hostState?.hostScriptHash === observation.hostScriptHash &&
    hostState?.supervisorScriptHash === observation.supervisorScriptHash &&
    hostState?.processIdentityHelperHash ===
      observation.processIdentityHelperHash &&
    hostState?.supervisorProtocolGeneration ===
      observation.supervisorProtocolGeneration &&
    JSON.stringify(hostState?.capabilities) ===
      JSON.stringify(observation.hostCapabilities) &&
    hostState?.pid === observation.hostPid &&
    hostState?.processStartTicks === observation.hostProcessStartTicks &&
    hostState?.processStartExact === observation.hostProcessStartExact &&
    hostState?.controlPort === observation.controlPort &&
    hostState?.controlToken === observation.controlToken &&
    hostState?.startedAt === observation.hostStartedAt
  );
}

export async function observedHostOwnership(
  observation,
  hostState,
  inspectProcess,
) {
  if (!hostStateMatchesObservation(observation, hostState)) {
    return {
      owned: false,
      reason:
        "Current host manifest runtime, process identity, or credentials differ from the observation",
    };
  }

  const actualIdentity = await inspectProcess(observation.hostPid);
  if (
    !sameProcessIdentity(
      actualIdentity,
      observation.hostProcessIdentity,
    )
  ) {
    return {
      owned: false,
      reason:
        "Recorded host PID no longer has the observation's process creation identity",
    };
  }

  return { owned: true, actualIdentity };
}

export async function stopObservedHost(
  observation,
  hostState,
  dependencies,
) {
  const ownership = await observedHostOwnership(
    observation,
    hostState,
    dependencies.inspectProcess,
  );
  if (!ownership.owned) {
    return {
      shutdownSent: false,
      stopped: false,
      ownershipChanged: true,
      reason: ownership.reason,
    };
  }

  await dependencies.sendShutdown(hostState);
  await dependencies.waitForExit(async () => {
    const actual = await dependencies.inspectProcess(observation.hostPid);
    return !sameProcessIdentity(
      actual,
      observation.hostProcessIdentity,
    );
  });
  return {
    shutdownSent: true,
    stopped: true,
    ownershipChanged: false,
    reason: null,
  };
}

async function control(hostState, path, method = "GET", body) {
  const response = await fetch(
    `http://127.0.0.1:${hostState.controlPort}${path}`,
    {
      method,
      headers: {
        authorization: `Bearer ${hostState.controlToken}`,
        ...(body ? { "content-type": "application/json" } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(method === "POST" ? 30_000 : 10_000),
    },
  );
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Control ${method} ${path} failed with HTTP ${response.status}: ${text}`);
  }
  return text ? JSON.parse(text) : {};
}

async function start() {
  if (existsSync(observationPath)) {
    throw new Error(
      `Observation already exists at '${observationPath}'. Inspect or stop it instead of replacing its evidence.`,
    );
  }
  if (existsSync(hostStatePath)) {
    throw new Error(
      `Durable host state already exists at '${hostStatePath}'. Inspect it before starting an observation.`,
    );
  }
  if (!existsSync(ttyd)) {
    throw new Error(`Missing ${ttyd}. Run '.\\treemon.ps1 setup-ttyd'.`);
  }

  mkdirSync(worktree, { recursive: true });
  const bundle = materializeRuntimeBundle(stateDirectory);
  const startedAt = new Date();
  const generation = randomBytes(16).toString("hex");
  atomicWrite(lockPath, {
    generation,
    ownerPid: process.pid,
  });
  const host = spawn(
    process.execPath,
    [
      hostScript,
      "--state-dir",
      stateDirectory,
      "--ttyd",
      ttyd,
      "--shell",
      "pwsh",
      "--generation",
      generation,
      "--runtime-bundle-dir",
      bundle.bundleDirectory,
      "--runtime-bundle-hash",
      bundle.bundleHash,
      "--host-script-hash",
      bundle.hostScriptHash,
      "--supervisor-script-hash",
      bundle.supervisorScriptHash,
      "--process-helper-hash",
      bundle.processIdentityHelperHash,
    ],
    {
      cwd: repo,
      detached: true,
      windowsHide: true,
      stdio: "ignore",
    },
  );
  const spawnedHostPid = host.pid;
  host.unref();

  let hostState;
  let spawnedHostIdentity;
  try {
    spawnedHostIdentity = await waitFor(
      "spawned durable host process identity",
      () => processController.inspect(spawnedHostPid),
    );
    const windowsStartTicks =
      /^windows:(\d+)$/.exec(spawnedHostIdentity.startIdentity)?.[1];
    const claimStartTicks =
      windowsStartTicks ??
      (
        621355968000000000n +
        BigInt(Date.now()) * 10_000n
      ).toString();
    atomicWrite(lockPath, {
      generation,
      ownerPid: process.pid,
      hostPid: spawnedHostPid,
      hostProcessStartTicks: claimStartTicks,
    });
    hostState = await waitFor("durable host state", () => {
      if (!existsSync(hostStatePath)) return null;
      return readJson(hostStatePath);
    });
    if (hostState.pid !== spawnedHostPid) {
      throw new Error("Durable host state did not identify the spawned process");
    }

    const response = await control(hostState, "/sessions", "POST", {
      worktreePath: worktree,
    });
    const session = await waitFor("observation process identities", async () => {
      const current = await control(hostState, "/sessions");
      const candidate = current.sessions.find(
        (item) => item.id === response.sessions[0]?.id,
      );
      return candidate?.lifecycle === "running" &&
        candidate.ttydPid &&
        candidate.shellPid
        ? candidate
        : null;
    });
    const observation = {
      status: "running",
      startedAt: startedAt.toISOString(),
      dueAt: new Date(startedAt.getTime() + durationMs).toISOString(),
      lastCheckedAt: new Date().toISOString(),
      hostProtocolVersion: hostState.version,
      hostGeneration: hostState.generation,
      hostBundleHash: hostState.bundleHash,
      hostScriptHash: hostState.hostScriptHash,
      supervisorScriptHash: hostState.supervisorScriptHash,
      processIdentityHelperHash: hostState.processIdentityHelperHash,
      supervisorProtocolGeneration: hostState.supervisorProtocolGeneration,
      hostCapabilities: hostState.capabilities,
      hostPid: hostState.pid,
      hostProcessStartTicks: hostState.processStartTicks,
      hostProcessStartExact: hostState.processStartExact,
      hostProcessIdentity: spawnedHostIdentity,
      hostStartedAt: hostState.startedAt,
      controlPort: hostState.controlPort,
      controlToken: hostState.controlToken,
      terminalUrl: new URL(session.endpoint).origin,
      terminalSessionId: session.id,
      ttydPid: session.ttydPid,
      powershellPid: session.shellPid,
      upstreamOpenedAt: session.upstreamOpenedAt,
      lastPongAt: session.lastPongAt,
      browserAttachments: session.browserAttachments,
      diagnosticsPath,
      diagnosticLimitBytes: 1024 * 1024,
      stopCommand:
        "node scripts\\durable-terminal-observation.mjs stop",
    };
    atomicWrite(observationPath, observation);
    process.stdout.write(`${JSON.stringify(publicObservation(observation), null, 2)}\n`);
  } catch (error) {
    if (!spawnedHostIdentity) {
      await terminateRetainedChild(host);
      throw error;
    }
    const actualIdentity = spawnedHostIdentity
      ? await processController.inspect(spawnedHostPid)
      : null;
    const stillSpawnedHost = sameProcessIdentity(
      actualIdentity,
      spawnedHostIdentity,
    );
    const hostStateIsSpawned =
      hostState?.generation === generation &&
      hostState?.pid === spawnedHostPid;
    if (hostStateIsSpawned && stillSpawnedHost) {
      try {
        await control(hostState, "/shutdown", "POST");
      } catch {
        await processController.terminate(spawnedHostIdentity);
      }
    } else if (stillSpawnedHost) {
      await processController.terminate(spawnedHostIdentity);
    }
    throw error;
  }
}

function diagnosticEvents() {
  if (!existsSync(diagnosticsPath)) return [];

  return readFileSync(diagnosticsPath, "utf8")
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => JSON.parse(line));
}

async function evaluate() {
  const observation = readJson(observationPath);
  const checkedAt = new Date();
  const actualHostIdentity =
    await processController.inspect(observation.hostPid);
  const hostAlive = sameProcessIdentity(
    actualHostIdentity,
    observation.hostProcessIdentity,
  );
  let hostIdentityMatches = false;
  let session = null;
  let controlError = null;

  if (hostAlive && existsSync(hostStatePath)) {
    try {
      const hostState = readJson(hostStatePath);
      hostIdentityMatches = hostStateMatchesObservation(
        observation,
        hostState,
      );
      if (hostIdentityMatches) {
        const response = await control(hostState, "/sessions");
        session =
          response.sessions.find(
            (candidate) => candidate.id === observation.terminalSessionId,
          ) ?? null;
      }
    } catch (error) {
      controlError = error.message;
    }
  }

  const events = diagnosticEvents().filter(
    (event) => event.sessionId === observation.terminalSessionId,
  );
  const upstreamClosed = [...events]
    .reverse()
    .find((event) => event.kind === "upstream-closed");
  const heartbeatAgeMs = session?.lastPongAt
    ? checkedAt.getTime() - Date.parse(session.lastPongAt)
    : null;
  const diagnosticsBytes = existsSync(diagnosticsPath)
    ? statSync(diagnosticsPath).size
    : 0;
  const failure =
    !hostAlive
      ? "Durable host process exited"
      : controlError
        ? `Durable host control failed: ${controlError}`
        : !hostIdentityMatches
          ? "Durable host generation or process identity changed"
        : !session
          ? "Terminal session is no longer registered"
          : session.lifecycle !== "running"
            ? session.error || `Terminal lifecycle is ${session.lifecycle}`
            : session.ttydPid !== observation.ttydPid
              ? "ttyd PID changed"
              : session.shellPid !== observation.powershellPid
                ? "PowerShell PID changed"
                : !(await processIsAlive(observation.ttydPid))
                  ? "ttyd process exited"
                  : !(await processIsAlive(observation.powershellPid))
                    ? "PowerShell process exited"
                    : diagnosticsBytes > observation.diagnosticLimitBytes
                      ? "Diagnostic record exceeded its configured bound"
                      : upstreamClosed
                        ? `Upstream WebSocket closed with code ${upstreamClosed.closeCode}`
                        : heartbeatAgeMs === null || heartbeatAgeMs > staleHeartbeatMs
                          ? "Upstream heartbeat is stale"
                          : null;
  const elapsedMs = checkedAt.getTime() - Date.parse(observation.startedAt);
  const status = failure
    ? "failed"
    : elapsedMs >= durationMs
      ? "passed"
      : "running";
  const updated = {
    ...observation,
    status,
    lastCheckedAt: checkedAt.toISOString(),
    elapsedMs,
    remainingMs: Math.max(0, durationMs - elapsedMs),
    hostAlive,
    hostIdentityMatches,
    ttydAlive: await processIsAlive(observation.ttydPid),
    powershellAlive: await processIsAlive(observation.powershellPid),
    browserAttachments: session?.browserAttachments ?? null,
    lastPongAt: session?.lastPongAt ?? observation.lastPongAt,
    heartbeatAgeMs,
    diagnosticsBytes,
    failure,
    upstreamCloseCode: upstreamClosed?.closeCode ?? null,
    upstreamCloseReason: upstreamClosed?.closeReason ?? null,
  };
  atomicWrite(observationPath, updated);
  return updated;
}

async function status() {
  if (!existsSync(observationPath)) {
    throw new Error(`No observation record at '${observationPath}'`);
  }
  const observation = await evaluate();
  process.stdout.write(`${JSON.stringify(publicObservation(observation), null, 2)}\n`);
  if (observation.status === "failed") process.exitCode = 1;
}

async function stop() {
  if (!existsSync(observationPath)) {
    throw new Error(`No observation record at '${observationPath}'`);
  }

  let observation = await evaluate();
  const stopResult = existsSync(hostStatePath)
    ? await stopObservedHost(
        observation,
        readJson(hostStatePath),
        {
          inspectProcess: (pid) => processController.inspect(pid),
          sendShutdown: (hostState) =>
            control(hostState, "/shutdown", "POST"),
          waitForExit: (predicate) =>
            waitFor(
              `recorded durable host PID ${observation.hostPid} to exit`,
              predicate,
              15_000,
            ),
        },
      )
    : {
        shutdownSent: false,
        stopped: false,
        ownershipChanged: true,
        reason: "Current host manifest is absent",
      };
  const actualHostIdentity =
    await processController.inspect(observation.hostPid);
  const observedHostAlive = sameProcessIdentity(
    actualHostIdentity,
    observation.hostProcessIdentity,
  );

  observation = {
    ...observation,
    status:
      observation.status === "passed" || observation.status === "failed"
        ? observation.status
        : "stopped",
    hostAlive: observedHostAlive,
    ttydAlive: await processIsAlive(observation.ttydPid),
    powershellAlive: await processIsAlive(observation.powershellPid),
    stopShutdownSent: stopResult.shutdownSent,
    stopOwnershipChanged: stopResult.ownershipChanged,
    stopSkippedReason: stopResult.reason,
    stoppedAt: new Date().toISOString(),
  };
  atomicWrite(observationPath, observation);
  process.stdout.write(`${JSON.stringify(publicObservation(observation), null, 2)}\n`);
}

export async function runObservationCommand(command = "status") {
  if (command === "start") await start();
  else if (command === "status") await status();
  else if (command === "stop") await stop();
  else throw new Error(`Unsupported observation command '${command}'`);
}

const invokedDirectly =
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href;

if (invokedDirectly) {
  runObservationCommand(process.argv[2] ?? "status").catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
