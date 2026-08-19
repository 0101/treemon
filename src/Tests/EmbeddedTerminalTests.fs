module Tests.EmbeddedTerminalTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.GitWorktree

let private run workflow =
    workflow |> Async.RunSynchronously

let private canonical path =
    Server.PathUtils.toWorktreePath path

let private isPath path (tab: EmbeddedTerminalTab) =
    Shared.PathUtils.pathEquals
        (WorktreePath.value path)
        (WorktreePath.value tab.Worktree)

let private tryFindTab path snapshot =
    snapshot.Tabs |> List.tryFind (isPath path)

let private endpointFor path snapshot =
    match snapshot |> tryFindTab path |> Option.map _.Lifecycle with
    | Some (EmbeddedTerminalLifecycle.Running endpoint) -> endpoint
    | lifecycle ->
        Assert.Fail($"Expected running terminal for '{WorktreePath.value path}', got {lifecycle}")
        ""

let private errorFor path snapshot =
    match snapshot |> tryFindTab path |> Option.map _.Lifecycle with
    | Some (EmbeddedTerminalLifecycle.Failed error) -> error
    | lifecycle ->
        Assert.Fail($"Expected failed terminal for '{WorktreePath.value path}', got {lifecycle}")
        ""

let private start manager path =
    match EmbeddedTerminal.start manager path |> run with
    | Ok snapshot -> snapshot
    | Error error ->
        Assert.Fail(error)
        EmbeddedTerminalSnapshot.empty

let private interruptedErrorFor path snapshot =
    match snapshot |> tryFindTab path |> Option.map _.Lifecycle with
    | Some (EmbeddedTerminalLifecycle.Interrupted error) -> error
    | lifecycle ->
        Assert.Fail($"Expected interrupted terminal for '{WorktreePath.value path}', got {lifecycle}")
        ""

let private processIsAlive pid =
    try
        use proc = Process.GetProcessById pid
        not proc.HasExited
    with :? ArgumentException ->
        false

let private waitUntil description predicate =
    let deadline = DateTimeOffset.UtcNow.AddSeconds 10.0

    let rec wait () =
        if predicate () then
            ()
        elif DateTimeOffset.UtcNow >= deadline then
            Assert.Fail($"Timed out waiting for {description}")
        else
            Async.Sleep 50 |> run
            wait ()

    wait ()

let private awaitWithin
    description
    (operation: Task<'value>)
    =
    try
        operation.WaitAsync(TimeSpan.FromSeconds 2.0).GetAwaiter().GetResult()
    with :? TimeoutException ->
        Assert.Fail($"Timed out waiting for {description}")
        Unchecked.defaultof<'value>

type private ControlledLockAcquisition =
    { Acquire:
        WorktreePath ->
            Async<Result<IDisposable, string>>
      Entered: Task<unit>
      Release: unit -> unit
      Disposed: Task<unit> }

let private controlledLockAcquisition () =
    let entered =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

    let release =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

    let disposed =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

    { Acquire =
        fun _ ->
            async {
                entered.TrySetResult(()) |> ignore
                do! release.Task |> Async.AwaitTask

                return
                    Ok
                        ({ new IDisposable with
                            member _.Dispose() =
                                disposed.TrySetResult(())
                                |> ignore } : IDisposable)
            }
      Entered = entered.Task
      Release =
        fun () ->
            release.TrySetResult(()) |> ignore
      Disposed = disposed.Task }

let private worktreeLockPath stateDirectory worktree =
    let key =
        worktree
        |> WorktreePath.value
        |> Server.PathUtils.normalizePath
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    Path.Combine(stateDirectory, "worktree-locks", $"{key}.lock")

let private canAcquireWorktreeLock stateDirectory worktree =
    try
        use _ =
            new FileStream(
                worktreeLockPath stateDirectory worktree,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            )

        true
    with :? IOException ->
        false

let private fakeHostScript =
    """
import { createHash, randomBytes } from "node:crypto";
import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, resolve, join } from "node:path";

const args = process.argv.slice(2);
const stateDirectory = resolve(args[args.indexOf("--state-dir") + 1]);
const generation = args[args.indexOf("--generation") + 1];
const bundleHash = args[args.indexOf("--runtime-bundle-hash") + 1];
const runtimeBundleVersion = Number(args[args.indexOf("--runtime-bundle-version") + 1]);
const extendedRuntimeBundleVersion = Number(args[args.indexOf("--extended-runtime-bundle-version") + 1]);
const extendedBundleHash = args[args.indexOf("--extended-runtime-bundle-hash") + 1];
const hostScriptHash = args[args.indexOf("--host-script-hash") + 1];
const supervisorScriptHash = args[args.indexOf("--supervisor-script-hash") + 1];
const processIdentityHelperHash = args[args.indexOf("--process-helper-hash") + 1];
const runtimeLockHelperHash = args[args.indexOf("--runtime-lock-helper-hash") + 1];
const ttydExecutableHash = args[args.indexOf("--ttyd-hash") + 1];
const webSocketPackageHash = args[args.indexOf("--ws-package-hash") + 1];
const runtimeLockOwnerPid = Number(args[args.indexOf("--runtime-lock-owner-pid") + 1]);
const runtimeLockOwnerProcessStartTicks = args[args.indexOf("--runtime-lock-owner-start-ticks") + 1];
const statePath = join(stateDirectory, "host.json");
const lockPath = join(stateDirectory, "host.lock");
const eventPath = join(stateDirectory, "events.json");
const behaviorPath = join(stateDirectory, "behavior.json");
const launchesPath = join(stateDirectory, "launches.txt");
const generationDirectory = join(stateDirectory, "terminal-generations");
const witnessDirectory = join(stateDirectory, "terminal-empty-witnesses", generation);
const generationPath = join(generationDirectory, `${generation}.json`);
const token = randomBytes(16).toString("hex");
const startedAt = new Date().toISOString();
let processStartTicks = (
  621355968000000000n +
  BigInt(Math.round(Date.now() - process.uptime() * 1000)) * 10000n
).toString();
let sessions = [];
let nextOrder = 0;
let reservations = [];

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const waitForFile = async (path) => {
  if (existsSync(path)) return;
  await delay(5);
  return waitForFile(path);
};

const behavior = () =>
  existsSync(behaviorPath)
    ? JSON.parse(readFileSync(behaviorPath, "utf8"))
    : {};

const ownershipBoundary = () =>
  behavior().omitOwnershipBoundary
    ? {}
    : { ownershipBoundary: "windows-job-v1" };

const manifestVersion = behavior().protocolVersion ?? 3;
const runtimeCapabilities = [
  "immutable-runtime-bundle-v1",
  "strict-evidence-paths-v1",
  "trusted-empty-supervisor-v1",
];
const extendedRuntimeCapabilities = [
  "immutable-executable-dependencies-v1",
  "immutable-runtime-bundle-v1",
  "locked-runtime-files-v1",
  "strict-evidence-paths-v1",
  "trusted-empty-supervisor-v1",
];

const runtimeIdentity = {
  runtimeBundleVersion,
  bundleHash,
  hostScriptHash,
  supervisorScriptHash,
  processIdentityHelperHash,
  extendedRuntime: {
    version: extendedRuntimeBundleVersion,
    bundleHash: extendedBundleHash,
    runtimeLockHelperHash,
    ttydExecutableHash,
    webSocketPackageHash,
    capabilities: extendedRuntimeCapabilities,
  },
  runtimeLockBoundary: "windows-file-share-read-v1",
  runtimeLockOwnerPid,
  runtimeLockOwnerProcessStartTicks,
};

function appendSession(worktreePath) {
  const key = process.platform === "win32" ? worktreePath.toLowerCase() : worktreePath;
  if (sessions.some((session) => session.key === key)) return;
  sessions = [
    ...sessions,
    {
      id: randomBytes(8).toString("hex"),
      key,
      worktreePath,
      lifecycle: "running",
      endpoint: `http://127.0.0.1:${42000 + nextOrder}/?cap=fake`,
      error: null,
      order: nextOrder++,
      witnessNonce: randomBytes(24).toString("base64url"),
      supervisorPid: behavior().supervisorPid ?? process.pid,
      supervisorStartTimeUtcTicks:
        behavior().supervisorStartTimeUtcTicks ?? processStartTicks,
      supervisorState: "in-progress",
    },
  ];
}

(behavior().initialWorktreePaths ?? []).forEach(appendSession);

const startupClaim = async () => {
  const claim = JSON.parse(readFileSync(lockPath, "utf8"));
  if (claim.generation !== generation) throw new Error("startup generation changed");
  if (claim.hostPid === process.pid && claim.hostProcessStartTicks) return claim;
  await delay(10);
  return startupClaim();
};

function writeJson(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  const temporary = `${path}.${process.pid}.tmp`;
  writeFileSync(temporary, JSON.stringify(value));
  renameSync(temporary, path);
}

function writeGeneration() {
  if (manifestVersion === 1) return;
  writeJson(generationPath, {
    version: manifestVersion === 3 ? 2 : 1,
    hostProtocolVersion: manifestVersion,
    generation,
    hostPid: process.pid,
    hostProcessStartTicks: processStartTicks,
    hostProcessStartExact: true,
    ownershipBoundary: "windows-job-v1",
    ...(manifestVersion === 3
      ? {
          ...runtimeIdentity,
          supervisorProtocolGeneration: 2,
          capabilities: runtimeCapabilities,
        }
      : {}),
    startedAt,
    sessions: sessions.map((session) => ({
      sessionId: session.id,
      worktreePath: session.worktreePath,
      witnessTokenHash: createHash("sha256")
        .update(session.witnessNonce, "utf8")
        .digest("hex"),
      supervisorPid: session.supervisorPid,
      supervisorStartTimeUtcTicks: session.supervisorStartTimeUtcTicks,
      ...(manifestVersion === 3
        ? {
            supervisorState: session.supervisorState,
            supervisorExited: session.supervisorState === "trusted-empty",
            supervisorExitCode:
              session.supervisorState === "trusted-empty" ? 0 : null,
            supervisorExitSignal: null,
            supervisorOutputClosed:
              session.supervisorState === "trusted-empty",
          }
        : { protocolFailure: false }),
    })),
  });
}

function writeWitness(session) {
  if (manifestVersion === 1) return;
  mkdirSync(witnessDirectory, { recursive: true });
  writeJson(join(witnessDirectory, `${session.id}.json`), {
    version: 1,
    generation,
    worktreePath: session.worktreePath,
    sessionId: session.id,
    supervisorPid: session.supervisorPid,
    supervisorStartTimeUtcTicks: session.supervisorStartTimeUtcTicks,
    nonce: session.witnessNonce,
    observedAt: new Date().toISOString(),
  });
  session.supervisorState = "trusted-empty";
}

function send(response, status, value) {
  const body = Buffer.from(JSON.stringify(value));
  response.writeHead(status, {
    "content-type": "application/json",
    "content-length": body.length,
  });
  response.end(body);
}

function readBody(request) {
  return new Promise((resolveBody, rejectBody) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      try {
        resolveBody(JSON.parse(Buffer.concat(chunks).toString() || "{}"));
      } catch (error) {
        rejectBody(error);
      }
    });
  });
}

const server = createServer(async (request, response) => {
  if (request.headers.authorization !== `Bearer ${token}`) {
    send(response, 404, { error: "Not found" });
    return;
  }

  const url = new URL(request.url, "http://127.0.0.1");

  if (request.method === "GET" && url.pathname === "/health") {
    const current = behavior();
    const identity =
      manifestVersion === 1
        ? {
            version: 1,
            pid: process.pid,
            ...ownershipBoundary(),
            startedAt,
          }
        : {
            version: manifestVersion,
            generation,
            pid: process.pid,
            processStartTicks,
            processStartExact: true,
            ...ownershipBoundary(),
            ...(manifestVersion === 3
              ? {
                  ...runtimeIdentity,
                  supervisorProtocolGeneration: 2,
                  capabilities: runtimeCapabilities,
                }
              : {}),
            startedAt,
          };
    send(response, current.healthStatus ?? 200, identity);
  } else if (request.method === "GET" && url.pathname === "/sessions") {
    const current = behavior();
    if (current.listTransportFailure) {
      request.socket.destroy();
    } else if (current.listMalformedResponse) {
      response.end("{");
    } else {
      send(response, current.listStatus ?? 200, { sessions });
    }
  } else if (request.method === "POST" && url.pathname === "/sessions") {
    const body = await readBody(request);
    const key = process.platform === "win32" ? body.worktreePath.toLowerCase() : body.worktreePath;
    const existing = sessions.find((session) => session.key === key);
    if (!existing) {
      appendSession(body.worktreePath);
      writeGeneration();
    }
    const current = behavior();
    if (current.startDelayMs) await delay(current.startDelayMs);
    if (current.startTransportFailure) {
      request.socket.destroy();
    } else {
      send(response, 200, { sessions });
    }
  } else if (request.method === "DELETE" && url.pathname.startsWith("/sessions/")) {
    const id = decodeURIComponent(url.pathname.slice("/sessions/".length));
    const current = behavior();
    if (!current.deleteKeepsSession && !current.deleteStatus && !current.deleteTransportFailure && !current.deleteMalformedResponse) {
      sessions.filter((session) => session.id === id).forEach(writeWitness);
      sessions = sessions.filter((session) => session.id !== id);
      writeGeneration();
    }
    if (current.deleteTransportFailure) {
      request.socket.destroy();
    } else if (current.deleteMalformedResponse) {
      response.end("{");
    } else {
      send(response, current.deleteStatus ?? 200, { sessions });
    }
  } else if (request.method === "POST" && url.pathname === "/reservations") {
    if (manifestVersion === 1) {
      send(response, 404, { error: "Not found" });
      return;
    }
    const body = await readBody(request);
    const key = process.platform === "win32" ? body.worktreePath.toLowerCase() : body.worktreePath;
    sessions.filter((session) => session.key === key).forEach(writeWitness);
    sessions = sessions.filter((session) => session.key !== key);
    writeGeneration();
    const reservation = {
      id: body.reservationId ?? randomBytes(16).toString("hex"),
      worktreePath: body.worktreePath,
      expiresAt: new Date(Date.now() + 300000).toISOString(),
    };
    reservations = [...reservations, reservation];
    const current = behavior();
    if (current.reserveMalformedResponse) {
      response.end("{");
    } else {
      send(response, current.reserveStatus ?? 201, { reservation, sessions });
    }
  } else if (request.method === "POST" && url.pathname.startsWith("/reservations/") && url.pathname.endsWith("/renew")) {
    const id = decodeURIComponent(url.pathname.slice("/reservations/".length, -"/renew".length));
    const reservation = reservations.find((candidate) => candidate.id === id);
    const current = behavior();
    appendFileSync(join(stateDirectory, "reservation-renewals.txt"), `${id}\n`);
    if (current.renewTransportFailure) {
      request.socket.destroy();
    } else {
      const status = current.renewStatus ?? (reservation ? 200 : 409);
      send(response, status, status < 400 ? { reservation } : { error: "expired" });
    }
  } else if (request.method === "DELETE" && url.pathname.startsWith("/reservations/")) {
    const id = decodeURIComponent(url.pathname.slice("/reservations/".length));
    const found = reservations.some((reservation) => reservation.id === id);
    reservations = reservations.filter((reservation) => reservation.id !== id);
    appendFileSync(join(stateDirectory, "reservation-releases.txt"), `${id}\n`);
    const current = behavior();
    if (current.releaseGatePath) await waitForFile(current.releaseGatePath);
    send(response, current.releaseStatus ?? 200, { released: found });
  } else if (request.method === "POST" && url.pathname === "/events") {
    const body = await readBody(request);
    const events = existsSync(eventPath)
      ? JSON.parse(readFileSync(eventPath, "utf8"))
      : [];
    writeJson(eventPath, [...events, body]);
    send(response, 200, { recorded: true });
  } else if (request.method === "POST" && url.pathname === "/shutdown") {
    send(response, 200, { stopping: true });
    setImmediate(() => server.close(() => {
      sessions.forEach(writeWitness);
      sessions = [];
      writeGeneration();
      const currentBehavior = behavior();
      const replacementManifestPath =
        manifestVersion === 1
          ? currentBehavior.replacementManifestPath
          : null;
      if (replacementManifestPath) {
        writeJson(
          statePath,
          JSON.parse(readFileSync(replacementManifestPath, "utf8")),
        );
      } else if (manifestVersion < 3 && currentBehavior.upgradeAfterDrain) {
        writeJson(behaviorPath, {
          ...currentBehavior,
          protocolVersion: 3,
          initialWorktreePaths: [],
        });
      }
      if (!replacementManifestPath && existsSync(statePath)) {
        const current = JSON.parse(readFileSync(statePath, "utf8"));
        const owned =
          manifestVersion === 1
            ? current.version === 1 && current.pid === process.pid && current.startedAt === startedAt
            : current.generation === generation && current.processStartTicks === processStartTicks;
        if (owned) {
          rmSync(statePath, { force: true });
        }
      }
      process.exit(0);
    }));
  } else if (request.method === "POST" && url.pathname === "/crash") {
    send(response, 200, { crashing: true });
    setImmediate(() => server.close(() => {
      if (!behavior().omitCrashWitness) sessions.forEach(writeWitness);
      if (!behavior().omitCrashGeneration) writeGeneration();
      process.exit(0);
    }));
  } else {
    send(response, 404, { error: "Not found" });
  }
});

process.stdin.resume();
process.stdin.once("end", () => {
  server.close(() => process.exit(12));
});

mkdirSync(stateDirectory, { recursive: true });
server.listen(0, "127.0.0.1", async () => {
  const { port } = server.address();
  const claim = await startupClaim();
  processStartTicks = claim.hostProcessStartTicks;
  sessions = sessions.map((session) => ({
    ...session,
    supervisorStartTimeUtcTicks:
      behavior().supervisorStartTimeUtcTicks ?? processStartTicks,
  }));
  writeGeneration();
  if (existsSync(statePath)) process.exit(3);
  appendFileSync(launchesPath, `${process.pid}\n`);
  const manifest =
    manifestVersion === 1
      ? {
          version: 1,
          pid: process.pid,
          ...ownershipBoundary(),
          controlPort: port,
          controlToken: token,
          startedAt,
        }
      : {
          version: manifestVersion,
          generation,
          pid: process.pid,
          processStartTicks,
          processStartExact: true,
          ...ownershipBoundary(),
          ...(manifestVersion === 3
            ? {
                ...runtimeIdentity,
                supervisorProtocolGeneration: 2,
                capabilities: runtimeCapabilities,
              }
            : {}),
          controlPort: port,
          controlToken: token,
          startedAt,
        };
  writeJson(statePath, manifest);
});
"""

let private config stateDirectory hostScript ttydPath : EmbeddedTerminal.Config =
    { NodeExecutable = "node"
      HostScriptPath = hostScript
      SupervisorScriptPath =
        Path.GetFullPath(
            Path.Combine(
                "scripts",
                "terminal-job-supervisor.ps1"
            )
        )
      ProcessIdentityHelperPath =
        Path.GetFullPath(
            Path.Combine(
                "scripts",
                "terminate-owned-process.ps1"
            )
        )
      RuntimeLockHelperPath =
        Path.GetFullPath(
            Path.Combine(
                "scripts",
                "terminal-runtime-lock.ps1"
            )
        )
      WebSocketPackagePath =
        Path.GetFullPath(
            Path.Combine(
                __SOURCE_DIRECTORY__,
                "..",
                "..",
                "node_modules",
                "ws"
            )
        )
      HostStateDirectory = stateDirectory
      TtydExecutablePath = ttydPath
      TtydExpectedHash = None
      ShellCommand = "pwsh"
      StartupTimeout = TimeSpan.FromSeconds 5.0
      ControlRequestTimeout = TimeSpan.FromSeconds 5.0
      ProbeInterval = TimeSpan.FromMilliseconds 25.0
      ReservationRenewalInterval = TimeSpan.FromSeconds 30.0 }

let private copyDirectory source destination =
    Directory.GetFiles(
        source,
        "*",
        SearchOption.AllDirectories
    )
    |> Array.map (fun sourcePath ->
        let relative =
            Path.GetRelativePath(source, sourcePath)

        sourcePath,
        Path.Combine(destination, relative))
    |> Array.iter (fun (sourcePath, destinationPath) ->
        Directory.CreateDirectory(
            Path.GetDirectoryName destinationPath
        )
        |> ignore

        File.Copy(
            sourcePath,
            destinationPath,
            true
        ))

let private readHostPid stateDirectory =
    use document =
        Path.Combine(stateDirectory, "host.json")
        |> File.ReadAllText
        |> JsonDocument.Parse

    document.RootElement.GetProperty("pid").GetInt32()

let private readHostGeneration stateDirectory =
    use document =
        Path.Combine(stateDirectory, "host.json")
        |> File.ReadAllText
        |> JsonDocument.Parse

    document.RootElement.GetProperty("generation").GetString()

let private readHostVersion stateDirectory =
    use document =
        Path.Combine(stateDirectory, "host.json")
        |> File.ReadAllText
        |> JsonDocument.Parse

    document.RootElement.GetProperty("version").GetInt32()

let private writeBehavior stateDirectory (json: string) =
    Directory.CreateDirectory stateDirectory |> ignore
    File.WriteAllText(
        Path.Combine(stateDirectory, "behavior.json"),
        json
    )

type private GenerationSessionFixture =
    { SessionId: string
      WorktreePath: string
      WitnessNonce: string
      SupervisorPid: int
      SupervisorStartTicks: int64
      TrustState: string }

let private generationRecordPath stateDirectory generation =
    Path.Combine(
        stateDirectory,
        "terminal-generations",
        $"{generation}.json"
    )

let private witnessPath stateDirectory generation sessionId =
    Path.Combine(
        stateDirectory,
        "terminal-empty-witnesses",
        generation,
        $"{sessionId}.json"
    )

let private writeGenerationRecord
    stateDirectory
    hostConfig
    generation
    hostPid
    hostStartTicks
    protocolVersion
    sessionsUnknown
    sessions
    =
    let path =
        generationRecordPath
            stateDirectory
            generation

    Directory.CreateDirectory(Path.GetDirectoryName path)
    |> ignore

    let witnessTokenHash session =
        session.WitnessNonce
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let content =
        if protocolVersion = 3 then
            let bundle =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok bundle -> bundle
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            let serializedSessions =
                sessions
                |> List.map (fun session ->
                    {| sessionId = session.SessionId
                       worktreePath = session.WorktreePath
                       witnessTokenHash =
                        witnessTokenHash session
                       supervisorPid = session.SupervisorPid
                       supervisorStartTimeUtcTicks =
                        string session.SupervisorStartTicks
                       supervisorState =
                        session.TrustState
                       supervisorExited =
                        (session.TrustState = "trusted-empty")
                       supervisorExitCode =
                        if
                            session.TrustState
                            <> "trusted-empty"
                        then
                            Nullable()
                        else
                            Nullable 0
                       supervisorExitSignal =
                        (null: string)
                       supervisorOutputClosed =
                        (session.TrustState = "trusted-empty") |})
                |> List.toArray

            JsonSerializer.Serialize(
                {| version = 2
                   hostProtocolVersion = protocolVersion
                   generation = generation
                   hostPid = hostPid
                   hostProcessStartTicks = string hostStartTicks
                   hostProcessStartExact = true
                   ownershipBoundary = "windows-job-v1"
                   runtimeBundleVersion =
                    bundle.Identity.Version
                   bundleHash =
                    bundle.Identity.BundleHash
                   hostScriptHash =
                    bundle.Identity.HostScriptHash
                   supervisorScriptHash =
                    bundle.Identity.SupervisorScriptHash
                   processIdentityHelperHash =
                    bundle.Identity.ProcessIdentityHelperHash
                   extendedRuntime =
                    {| version =
                        bundle.Identity.ExtendedVersion.Value
                       bundleHash =
                        bundle.Identity.ExtendedBundleHash.Value
                       runtimeLockHelperHash =
                        bundle.Identity.RuntimeLockHelperHash.Value
                       ttydExecutableHash =
                        bundle.Identity.TtydExecutableHash.Value
                       webSocketPackageHash =
                        bundle.Identity.WebSocketPackageHash.Value
                       capabilities =
                        [| "immutable-executable-dependencies-v1"
                           "immutable-runtime-bundle-v1"
                           "locked-runtime-files-v1"
                           "strict-evidence-paths-v1"
                           "trusted-empty-supervisor-v1" |] |}
                   supervisorProtocolGeneration = 2
                   capabilities =
                    [| "immutable-runtime-bundle-v1"
                       "strict-evidence-paths-v1"
                       "trusted-empty-supervisor-v1" |]
                   startedAt =
                    DateTimeOffset.UtcNow.ToString("O")
                   sessionsUnknown = sessionsUnknown
                   sessions = serializedSessions |}
            )
        else
            let serializedSessions =
                sessions
                |> List.map (fun session ->
                    {| sessionId = session.SessionId
                       worktreePath = session.WorktreePath
                       witnessTokenHash =
                        witnessTokenHash session
                       supervisorPid = session.SupervisorPid
                       supervisorStartTimeUtcTicks =
                        string session.SupervisorStartTicks
                       protocolFailure =
                        (session.TrustState = "quarantined") |})
                |> List.toArray

            JsonSerializer.Serialize(
                {| version = 1
                   hostProtocolVersion = protocolVersion
                   generation = generation
                   hostPid = hostPid
                   hostProcessStartTicks = string hostStartTicks
                   hostProcessStartExact = true
                   ownershipBoundary = "windows-job-v1"
                   startedAt =
                    DateTimeOffset.UtcNow.ToString("O")
                   sessionsUnknown = sessionsUnknown
                   sessions = serializedSessions |}
            )

    File.WriteAllText(
        path,
        content
    )

let private writeEmptyWitnessAs
    stateDirectory
    pathGeneration
    payloadGeneration
    session
    =
    let path =
        witnessPath
            stateDirectory
            pathGeneration
            session.SessionId

    Directory.CreateDirectory(Path.GetDirectoryName path)
    |> ignore

    File.WriteAllText(
        path,
        JsonSerializer.Serialize(
            {| version = 1
               generation = payloadGeneration
               worktreePath = session.WorktreePath
               sessionId = session.SessionId
               supervisorPid = session.SupervisorPid
               supervisorStartTimeUtcTicks =
                string session.SupervisorStartTicks
               nonce = session.WitnessNonce
               observedAt = DateTimeOffset.UtcNow.ToString("O") |}
        )
    )

let private writeEmptyWitness stateDirectory generation session =
    writeEmptyWitnessAs
        stateDirectory
        generation
        generation
        session

let private fixtureSession suffix worktreePath pid startTicks =
    { SessionId = $"session-{suffix}-00000000"
      WorktreePath = worktreePath
      WitnessNonce =
        $"witness-{suffix}-000000000000000000000000"
      SupervisorPid = pid
      SupervisorStartTicks = startTicks
      TrustState = "trusted-empty" }

let private crashFakeHostManifest (manifest: string) =
    use document =
        manifest |> JsonDocument.Parse

    let root = document.RootElement
    let port = root.GetProperty("controlPort").GetInt32()
    let token = root.GetProperty("controlToken").GetString()
    use client = new HttpClient()
    use request =
        new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/crash"
        )

    request.Headers.Authorization <-
        AuthenticationHeaderValue("Bearer", token)

    use response = client.Send request
    response.EnsureSuccessStatusCode() |> ignore

let private crashFakeHost stateDirectory =
    Path.Combine(stateDirectory, "host.json")
    |> File.ReadAllText
    |> crashFakeHostManifest

let private withFakeHostConfig configure test =
    Tests.TestUtils.withTempDir "durable-terminal-host" (fun tempDir ->
        let stateDirectory = Path.Combine(tempDir, "state")
        let hostScript = Path.Combine(tempDir, "fake-host.mjs")
        let ttydPath = Path.Combine(tempDir, "fake-ttyd.exe")
        File.WriteAllText(hostScript, fakeHostScript)
        File.WriteAllText(ttydPath, "")
        let hostConfig =
            config stateDirectory hostScript ttydPath
            |> configure

        let manager = EmbeddedTerminal.createWithConfig hostConfig

        try
            test tempDir stateDirectory hostConfig manager
        finally
            let behaviorPath =
                Path.Combine(stateDirectory, "behavior.json")

            if File.Exists behaviorPath then
                File.Delete behaviorPath

            EmbeddedTerminal.shutdownHost manager |> run |> ignore)

let private withFakeHost test =
    withFakeHostConfig id test

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Platform("Win")>]
[<NonParallelizable>]
type EmbeddedTerminalTests() =

    [<Test>]
    member _.``missing ttyd reports the setup command on its tab``() =
        Tests.TestUtils.withTempDir "missing-durable-terminal" (fun tempDir ->
            let worktree = canonical tempDir
            let hostScript = Path.Combine(tempDir, "host.mjs")
            File.WriteAllText(hostScript, "")

            let manager =
                EmbeddedTerminal.createWithConfig
                    (config
                        (Path.Combine(tempDir, "state"))
                        hostScript
                        (Path.Combine(tempDir, "missing-ttyd.exe")))

            let failed = start manager worktree
            Assert.That(errorFor worktree failed, Does.Contain(@".\treemon.ps1 setup-ttyd")))

    [<Test>]
    member _.``host without Job Object capability cannot start or authorize mutation``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| omitOwnershipBoundary = true
                       initialWorktreePaths =
                        [| worktreePath |] |}
                ))

            let failed = start manager worktree
            let oldPid = readHostPid stateDirectory
            let listed =
                EmbeddedTerminal.get manager |> run

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let reserved =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                Assert.That(
                    errorFor worktree failed,
                    Does.Contain("Job Object ownership")
                        .IgnoreCase
                )
                Assert.That(
                    endpointFor worktree listed,
                    Is.Not.Empty
                )

                match reserved with
                | Ok () ->
                    Assert.Fail(
                        "A host without kernel ownership must not authorize mutation"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("Job Object ownership")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                ))

            crashFakeHost stateDirectory
            waitUntil "unsupported host to exit" (fun () ->
                processIsAlive oldPid |> not))

    [<Test>]
    member _.``API rejects an unknown worktree before starting the host``() =
        let agent = SchedulerState.createAgent ()

        Tests.TestUtils.withTempDir "unknown-durable-terminal" (fun tempDir ->
            let manager =
                EmbeddedTerminal.createWithConfig
                    (config
                        (Path.Combine(tempDir, "state"))
                        (Path.Combine(tempDir, "host.mjs"))
                        (Path.Combine(tempDir, "ttyd.exe")))

            let api =
                WorktreeApi.worktreeApi
                    { Agent = agent
                      CardLog = CardEventLog.createAgent ()
                      SessionAgent = SessionManager.createAgent ()
                      EmbeddedTerminal = manager
                      ActivityStore = None
                      SnapshotStore = None
                      AutoSyncStore = None
                      WorktreeRoots = []
                      TestFixtures = None
                      AppVersion = "test"
                      DeployBranch = None }

            let unknown = canonical tempDir

            match api.startEmbeddedTerminal unknown |> run with
            | Error error ->
                Assert.That(error, Does.StartWith "Unknown worktree path:")
            | Ok snapshot ->
                Assert.Fail($"Expected rejected path, got {snapshot}")

            Assert.That(
                api.getEmbeddedTerminals () |> run,
                Is.EqualTo EmbeddedTerminalSnapshot.empty
            ))

    [<Test>]
    member _.``new Treemon manager rediscovers the same durable host session``() =
        withFakeHost (fun tempDir stateDirectory hostConfig firstManager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let first = start firstManager worktree
            let endpoint = endpointFor worktree first
            let hostPid = readHostPid stateDirectory

            let restartedManager = EmbeddedTerminal.createWithConfig hostConfig
            let rediscovered = EmbeddedTerminal.get restartedManager |> run
            let alias = WorktreePath(WorktreePath.value worktree + string Path.DirectorySeparatorChar)
            let reused = start restartedManager alias

            Assert.Multiple(fun () ->
                Assert.That(endpointFor worktree rediscovered, Is.EqualTo endpoint)
                Assert.That(reused, Is.EqualTo rediscovered)
                Assert.That(readHostPid stateDirectory, Is.EqualTo hostPid)
                Assert.That(processIsAlive hostPid, Is.True))

            let eventPath = Path.Combine(stateDirectory, "events.json")
            waitUntil "both Treemon instances to register" (fun () ->
                if not (File.Exists eventPath) then
                    false
                else
                    use document = JsonDocument.Parse(File.ReadAllText eventPath)
                    document.RootElement.GetArrayLength() >= 2)

            let closed = EmbeddedTerminal.close restartedManager worktree |> run
            Assert.That(closed, Is.EqualTo EmbeddedTerminalSnapshot.empty)
            Assert.That(processIsAlive hostPid, Is.True))

    [<Test>]
    member _.``closing one terminal leaves the other durable session running``() =
        withFakeHost (fun tempDir _ _ manager ->
            let paths =
                [ "zebra"; "apple" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path |> ignore
                    canonical path)

            let first = start manager paths[0]
            let both = start manager paths[1]
            let firstEndpoint = endpointFor paths[0] first
            let secondEndpoint = endpointFor paths[1] both

            Assert.Multiple(fun () ->
                Assert.That(both.Tabs |> List.map _.Worktree, Is.EqualTo paths)
                Assert.That(firstEndpoint, Is.Not.EqualTo secondEndpoint))

            let remaining = EmbeddedTerminal.close manager paths[0] |> run

            Assert.Multiple(fun () ->
                Assert.That(remaining.Tabs |> List.map _.Worktree, Is.EqualTo([ paths[1] ]))
                Assert.That(endpointFor paths[1] remaining, Is.EqualTo secondEndpoint)))

    [<TestCase("""{"healthStatus":500}""", "discover")>]
    [<TestCase("""{"listStatus":500}""", "list")>]
    [<TestCase("""{"deleteStatus":500}""", "close request")>]
    [<TestCase("""{"deleteTransportFailure":true}""", "close request")>]
    [<TestCase("""{"deleteMalformedResponse":true}""", "response was invalid")>]
    [<TestCase("""{"deleteKeepsSession":true}""", "did not remove")>]
    member _.``strict close rejects every non-authoritative cleanup outcome``(
        behavior,
        expectedError
    ) =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            writeBehavior stateDirectory behavior

            match EmbeddedTerminal.closeStrict manager worktree |> run with
            | Error error ->
                Assert.That(
                    error,
                    Does.Contain(expectedError).IgnoreCase
                )
            | Ok snapshot ->
                Assert.Fail(
                    $"Expected strict close failure, got {snapshot}"
                ))

    [<Test>]
    member _.``reserved cleanup releases the key when mutation fails``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            return Error "mutation failed"
                        })
                |> run

            let restarted = start manager worktree
            let releases =
                Path.Combine(
                    stateDirectory,
                    "reservation-releases.txt"
                )
                |> File.ReadAllLines

            Assert.Multiple(fun () ->
                match result with
                | Error error ->
                    Assert.That(error, Is.EqualTo("mutation failed"))
                | Ok () -> Assert.Fail("Mutation should have failed")

                Assert.That(releases.Length, Is.EqualTo(1))
                Assert.That(endpointFor worktree restarted, Is.Not.Empty)))

    [<Test>]
    member _.``malformed reservation response still releases the known lease``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            writeBehavior
                stateDirectory
                """{"reserveMalformedResponse":true}"""

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async.Return(Ok()))
                |> run

            writeBehavior stateDirectory "{}"
            let restarted = start manager worktree
            let releases =
                Path.Combine(
                    stateDirectory,
                    "reservation-releases.txt"
                )
                |> File.ReadAllLines

            Assert.Multiple(fun () ->
                match result with
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("reservation response")
                    )
                | Ok () ->
                    Assert.Fail("Malformed reservation should fail")

                Assert.That(releases.Length, Is.EqualTo(1))
                Assert.That(endpointFor worktree restarted, Is.Not.Empty)))

    [<Test>]
    member _.``same-key lock waiter leaves unrelated manager operations responsive``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let reservedPath = Path.Combine(tempDir, "reserved")
            let closingPath = Path.Combine(tempDir, "closing")
            let startingPath = Path.Combine(tempDir, "starting")
            [ reservedPath; closingPath; startingPath ]
            |> List.iter (Directory.CreateDirectory >> ignore)
            let reservedWorktree = canonical reservedPath
            let closingWorktree = canonical closingPath
            let startingWorktree = canonical startingPath
            start manager reservedWorktree |> ignore
            start manager closingWorktree |> ignore

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let releaseMutation =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let reservation =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    reservedWorktree
                    (fun () ->
                        async {
                            mutationEntered.SetResult(())
                            do!
                                releaseMutation.Task
                                |> Async.AwaitTask

                            return Ok ()
                        })
                |> Async.StartAsTask

            mutationEntered.Task.GetAwaiter().GetResult()

            let exerciseBlockedWaiter () =
                Assert.That(
                    canAcquireWorktreeLock
                        stateDirectory
                        reservedWorktree,
                    Is.False
                )

                let firstSameKey =
                    EmbeddedTerminal.start
                        manager
                        reservedWorktree
                    |> Async.StartAsTask

                let secondSameKey =
                    EmbeddedTerminal.start
                        manager
                        reservedWorktree
                    |> Async.StartAsTask

                waitUntil
                    "one same-key request to report the active waiter"
                    (fun () ->
                        firstSameKey.IsCompleted
                        || secondSameKey.IsCompleted)

                let busy, sameKeyWaiter =
                    if firstSameKey.IsCompleted then
                        firstSameKey, secondSameKey
                    else
                        secondSameKey, firstSameKey

                match
                    awaitWithin
                        "same-key busy result"
                        busy
                with
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("already waiting").IgnoreCase
                    )
                | Ok snapshot ->
                    Assert.Fail(
                        $"Second same-key start should be busy, got {snapshot}"
                    )

                let current =
                    EmbeddedTerminal.get manager
                    |> Async.StartAsTask

                let started =
                    EmbeddedTerminal.start
                        manager
                        startingWorktree
                    |> Async.StartAsTask

                let closed =
                    EmbeddedTerminal.close
                        manager
                        closingWorktree
                    |> Async.StartAsTask

                awaitWithin
                    "unrelated terminal snapshot"
                    current
                |> ignore

                let startedSnapshot =
                    match
                        awaitWithin
                            "unrelated terminal start"
                            started
                    with
                    | Ok snapshot -> snapshot
                    | Error error ->
                        Assert.Fail(error)
                        EmbeddedTerminalSnapshot.empty

                let afterClose =
                    awaitWithin
                        "unrelated terminal close"
                        closed

                Assert.Multiple(fun () ->
                    Assert.That(reservation.IsCompleted, Is.False)
                    Assert.That(sameKeyWaiter.IsCompleted, Is.False)
                    Assert.That(
                        endpointFor
                            startingWorktree
                            startedSnapshot,
                        Is.Not.Empty
                    )
                    Assert.That(
                        tryFindTab closingWorktree afterClose,
                        Is.EqualTo(None)
                    ))

                sameKeyWaiter

            let sameKeyWaiter =
                try
                    exerciseBlockedWaiter ()
                finally
                    releaseMutation.TrySetResult(()) |> ignore

            match reservation.GetAwaiter().GetResult() with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                let restarted =
                    sameKeyWaiter.GetAwaiter().GetResult()

                match restarted with
                | Error error -> Assert.Fail(error)
                | Ok snapshot ->
                    Assert.That(
                        endpointFor reservedWorktree snapshot,
                        Is.Not.Empty
                    )

                Assert.That(
                    canAcquireWorktreeLock
                        stateDirectory
                        reservedWorktree,
                    Is.True
                ))

    [<Test>]
    member _.``cancelled start disposes its stale lock acquisition``() =
        Tests.TestUtils.withTempDir "cancelled-terminal-start" (fun tempDir ->
            let worktree = canonical tempDir
            let controlled = controlledLockAcquisition ()

            let manager =
                EmbeddedTerminal.createWithLockAcquisition
                    (config
                        (Path.Combine(tempDir, "state"))
                        (Path.Combine(tempDir, "host.mjs"))
                        (Path.Combine(tempDir, "ttyd.exe")))
                    controlled.Acquire

            use cancellation =
                new CancellationTokenSource()

            let started =
                EmbeddedTerminal.start manager worktree
                |> fun workflow ->
                    Async.StartAsTask(
                        workflow,
                        cancellationToken = cancellation.Token
                    )

            awaitWithin
                "cancelled start lock acquisition"
                controlled.Entered
            |> ignore

            cancellation.Cancel()

            try
                awaitWithin "cancelled terminal start" started
                |> ignore

                Assert.Fail("Terminal start should be cancelled")
            with :? OperationCanceledException ->
                ()

            controlled.Release()

            awaitWithin
                "stale start lock disposal"
                controlled.Disposed
            |> ignore

            Assert.That(
                EmbeddedTerminal.get manager |> run,
                Is.EqualTo EmbeddedTerminalSnapshot.empty
            ))

    [<Test>]
    member _.``cancelled cleanup reservation disposes its stale lock acquisition``() =
        Tests.TestUtils.withTempDir "cancelled-terminal-reservation" (fun tempDir ->
            let worktree = canonical tempDir
            let controlled = controlledLockAcquisition ()

            let manager =
                EmbeddedTerminal.createWithLockAcquisition
                    (config
                        (Path.Combine(tempDir, "state"))
                        (Path.Combine(tempDir, "host.mjs"))
                        (Path.Combine(tempDir, "ttyd.exe")))
                    controlled.Acquire

            let operationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            use cancellation =
                new CancellationTokenSource()

            let reserved =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            operationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> fun workflow ->
                    Async.StartAsTask(
                        workflow,
                        cancellationToken = cancellation.Token
                    )

            awaitWithin
                "cancelled reservation lock acquisition"
                controlled.Entered
            |> ignore

            cancellation.Cancel()

            try
                awaitWithin
                    "cancelled cleanup reservation"
                    reserved
                |> ignore

                Assert.Fail(
                    "Cleanup reservation should be cancelled"
                )
            with :? OperationCanceledException ->
                ()

            controlled.Release()

            awaitWithin
                "stale reservation lock disposal"
                controlled.Disposed
            |> ignore

            Assert.Multiple(fun () ->
                Assert.That(operationEntered.Task.IsCompleted, Is.False)
                Assert.That(
                    EmbeddedTerminal.get manager |> run,
                    Is.EqualTo EmbeddedTerminalSnapshot.empty
                )))

    [<Test>]
    member _.``worktree lock remains held through reservation release``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            let releaseGate = Path.Combine(tempDir, "release-gate")

            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| releaseGatePath = releaseGate |}
                ))

            let reserved =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async.Return(Ok()))
                |> Async.StartAsTask

            waitUntil
                "delayed reservation release"
                (fun () ->
                    Path.Combine(
                        stateDirectory,
                        "reservation-releases.txt"
                    )
                    |> File.Exists)

            try
                Assert.Multiple(fun () ->
                    Assert.That(reserved.IsCompleted, Is.False)
                    Assert.That(
                        canAcquireWorktreeLock stateDirectory worktree,
                        Is.False
                    ))
            finally
                File.WriteAllText(releaseGate, "")

            match reserved.GetAwaiter().GetResult() with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                Assert.That(
                    canAcquireWorktreeLock stateDirectory worktree,
                    Is.True
                ))

    [<Test>]
    member _.``renewal failure keeps the worktree lock until release finishes``() =
        withFakeHostConfig
            (fun hostConfig ->
                { hostConfig with
                    ReservationRenewalInterval =
                        TimeSpan.FromMilliseconds 10.0 })
            (fun tempDir stateDirectory _ manager ->
                let worktreePath = Path.Combine(tempDir, "worktree")
                let unrelatedPath =
                    Path.Combine(tempDir, "unrelated")

                [ worktreePath; unrelatedPath ]
                |> List.iter (Directory.CreateDirectory >> ignore)

                let worktree = canonical worktreePath
                let unrelated = canonical unrelatedPath
                start manager worktree |> ignore
                let releaseGate =
                    Path.Combine(tempDir, "renewal-release-gate")

                writeBehavior
                    stateDirectory
                    (JsonSerializer.Serialize(
                        {| renewStatus = 500
                           releaseGatePath = releaseGate |}
                    ))

                let mutationEntered =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let cancellationObserved =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let reserved =
                    EmbeddedTerminal.withReservedCleanup
                        manager
                        worktree
                        (fun () ->
                            async {
                                mutationEntered.SetResult(())
                                let! cancellation =
                                    Async.CancellationToken

                                use _registration =
                                    cancellation.Register(fun () ->
                                        cancellationObserved.TrySetResult(())
                                        |> ignore)

                                do!
                                    Task.Delay(
                                        Timeout.Infinite,
                                        cancellation
                                    )
                                    |> Async.AwaitTask

                                return Ok ()
                            })
                    |> Async.StartAsTask

                mutationEntered.Task.GetAwaiter().GetResult()

                let sameKeyStart =
                    EmbeddedTerminal.start manager worktree
                    |> Async.StartAsTask

                try
                    waitUntil
                        "reservation renewal failure"
                        (fun () ->
                            Path.Combine(
                                stateDirectory,
                                "reservation-renewals.txt"
                            )
                            |> File.Exists)

                    awaitWithin
                        "mutation cancellation observation"
                        cancellationObserved.Task
                    |> ignore

                    waitUntil
                        "cancellation-safe reservation release attempt"
                        (fun () ->
                            Path.Combine(
                                stateDirectory,
                                "reservation-releases.txt"
                            )
                            |> File.Exists)

                    let unrelatedStarted =
                        start manager unrelated

                    Assert.Multiple(fun () ->
                        Assert.That(reserved.IsCompleted, Is.False)
                        Assert.That(sameKeyStart.IsCompleted, Is.False)
                        Assert.That(
                            canAcquireWorktreeLock
                                stateDirectory
                                worktree,
                            Is.False
                        )
                        Assert.That(
                            endpointFor unrelated unrelatedStarted,
                            Is.Not.Empty
                        ))
                finally
                    File.WriteAllText(releaseGate, "")

                let result = reserved.GetAwaiter().GetResult()
                let restarted =
                    sameKeyStart.GetAwaiter().GetResult()

                Assert.Multiple(fun () ->
                    match result with
                    | Ok () ->
                        Assert.Fail("Renewal failure should be surfaced")
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain("renewal failed").IgnoreCase
                        )

                    Assert.That(
                        File.ReadAllLines(
                            Path.Combine(
                                stateDirectory,
                                "reservation-releases.txt"
                            )
                        ).Length,
                        Is.EqualTo(1)
                    )

                    match restarted with
                    | Error error -> Assert.Fail(error)
                    | Ok snapshot ->
                        Assert.That(
                            endpointFor worktree snapshot,
                            Is.Not.Empty
                        )

                    Assert.That(
                        canAcquireWorktreeLock
                            stateDirectory
                            worktree,
                        Is.True
                    )))

    [<Test>]
    member _.``renewal failure remains primary when reservation release also fails``() =
        withFakeHostConfig
            (fun hostConfig ->
                { hostConfig with
                    ReservationRenewalInterval =
                        TimeSpan.FromMilliseconds 10.0 })
            (fun tempDir stateDirectory _ manager ->
                let worktreePath =
                    Path.Combine(tempDir, "worktree")

                Directory.CreateDirectory worktreePath |> ignore
                let worktree = canonical worktreePath
                start manager worktree |> ignore
                writeBehavior
                    stateDirectory
                    """{"renewStatus":500,"releaseStatus":500}"""

                let result =
                    EmbeddedTerminal.withReservedCleanup
                        manager
                        worktree
                        (fun () ->
                            async {
                                let! cancellation =
                                    Async.CancellationToken

                                do!
                                    Task.Delay(
                                        Timeout.Infinite,
                                        cancellation
                                    )
                                    |> Async.AwaitTask

                                return Ok ()
                            })
                    |> run

                match result with
                | Ok () ->
                    Assert.Fail(
                        "Renewal and release failures should be surfaced"
                    )
                | Error error ->
                    Assert.Multiple(fun () ->
                        Assert.That(
                            error,
                            Does.StartWith(
                                "Durable terminal cleanup reservation renewal failed"
                            )
                        )
                        Assert.That(
                            error,
                            Does.Contain(
                                "reservation release failed"
                            ).IgnoreCase
                        )))

    [<Test>]
    member _.``caller cancellation still releases reservation and worktree lock``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            use cancellation = new CancellationTokenSource()

            let reserved =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.SetResult(())
                            let! token = Async.CancellationToken
                            do!
                                Task.Delay(Timeout.Infinite, token)
                                |> Async.AwaitTask

                            return Ok ()
                        })
                |> fun workflow ->
                    Async.StartAsTask(
                        workflow,
                        cancellationToken = cancellation.Token
                    )

            try
                mutationEntered.Task.GetAwaiter().GetResult()
                Assert.That(
                    canAcquireWorktreeLock stateDirectory worktree,
                    Is.False
                )
            finally
                cancellation.Cancel()

            try
                reserved.GetAwaiter().GetResult() |> ignore
                Assert.Fail("Reserved mutation should be cancelled")
            with :? OperationCanceledException ->
                ()

            waitUntil
                "cancellation-safe reservation release"
                (fun () ->
                    (Path.Combine(
                        stateDirectory,
                        "reservation-releases.txt"
                     )
                     |> File.Exists)
                    && canAcquireWorktreeLock
                        stateDirectory
                        worktree)

            Assert.Multiple(fun () ->
                Assert.That(
                    File.ReadAllLines(
                        Path.Combine(
                            stateDirectory,
                            "reservation-releases.txt"
                        )
                    ).Length,
                    Is.EqualTo(1)
                )

                Assert.That(
                    canAcquireWorktreeLock stateDirectory worktree,
                    Is.True
                )))

    [<TestCase("error", "primary mutation failure")>]
    [<TestCase("exception", "boom")>]
    member _.``operation failures preserve the primary error and release failure``(
        outcome,
        expectedPrimary
    ) =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            writeBehavior
                stateDirectory
                """{"releaseStatus":500}"""

            let operation () : Async<Result<unit, string>> =
                match outcome with
                | "error" ->
                    async.Return(Error "primary mutation failure")
                | "exception" ->
                    async {
                        return
                            raise (
                                InvalidOperationException "boom"
                            )
                    }
                | unexpected ->
                    failwith $"Unexpected outcome '{unexpected}'"

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    operation
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () ->
                    Assert.Fail("Mutation failure should be surfaced")
                | Error error ->
                    Assert.That(error, Does.Contain(expectedPrimary))
                    Assert.That(
                        error,
                        Does.Contain("release failed").IgnoreCase
                    )

                Assert.That(
                    File.ReadAllLines(
                        Path.Combine(
                            stateDirectory,
                            "reservation-releases.txt"
                        )
                    ).Length,
                    Is.EqualTo(1)
                )

                Assert.That(
                    canAcquireWorktreeLock stateDirectory worktree,
                    Is.True
                )))

    [<Test>]
    member _.``tab close keeps a failed tab when host removal is not authoritative``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            writeBehavior
                stateDirectory
                """{"deleteKeepsSession":true}"""

            let after = EmbeddedTerminal.close manager worktree |> run
            Assert.That(
                errorFor worktree after,
                Does.Contain("did not remove").IgnoreCase
            ))

    [<Test>]
    member _.``ambiguous start timeout reconciles the canonical session without duplication``() =
        withFakeHostConfig
            (fun hostConfig ->
                { hostConfig with
                    ControlRequestTimeout =
                        TimeSpan.FromMilliseconds 100.0 })
            (fun tempDir stateDirectory _ manager ->
                let worktreePath = Path.Combine(tempDir, "worktree")
                Directory.CreateDirectory worktreePath |> ignore
                let worktree = canonical worktreePath
                writeBehavior
                    stateDirectory
                    """{"startDelayMs":200}"""

                let first = start manager worktree
                let firstEndpoint = endpointFor worktree first
                File.Delete(
                    Path.Combine(stateDirectory, "behavior.json")
                )
                let retried = start manager worktree

                Assert.Multiple(fun () ->
                    Assert.That(first.Tabs.Length, Is.EqualTo(1))
                    Assert.That(retried.Tabs.Length, Is.EqualTo(1))
                    Assert.That(
                        endpointFor worktree retried,
                        Is.EqualTo(firstEndpoint)
                    )))

    [<Test>]
    member _.``concurrent Treemon starters converge on one host generation``() =
        withFakeHost (fun tempDir stateDirectory hostConfig firstManager ->
            let secondManager =
                EmbeddedTerminal.createWithConfig hostConfig

            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath

            let results =
                [ EmbeddedTerminal.start firstManager worktree
                  EmbeddedTerminal.start secondManager worktree ]
                |> Async.Parallel
                |> run

            let snapshots =
                results
                |> Array.map (function
                    | Ok snapshot -> snapshot
                    | Error error ->
                        Assert.Fail(error)
                        EmbeddedTerminalSnapshot.empty)

            let launches =
                Path.Combine(stateDirectory, "launches.txt")
                |> File.ReadAllLines

            Assert.Multiple(fun () ->
                Assert.That(launches.Length, Is.EqualTo(1))
                Assert.That(snapshots[0], Is.EqualTo(snapshots[1]))
                Assert.That(snapshots[0].Tabs.Length, Is.EqualTo(1))))

    [<Test>]
    member _.``dead protocol-two manifest is retained as untrusted evidence``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            Directory.CreateDirectory stateDirectory |> ignore
            let staleGeneration = "stale-generation"

            File.WriteAllText(
                Path.Combine(stateDirectory, "host.json"),
                JsonSerializer.Serialize(
                    {| version = 2
                       generation = staleGeneration
                       pid = Environment.ProcessId
                       processStartTicks = "1"
                       processStartExact = true
                       controlPort = 41234
                       controlToken = "stale"
                       startedAt = DateTimeOffset.UnixEpoch.ToString("O") |}
                )
            )

            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let result =
                EmbeddedTerminal.start manager worktree
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok _ ->
                    Assert.Fail(
                        "Dead protocol-two evidence authorized a replacement start"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("retired protocol-2")
                            .IgnoreCase
                    )

                Assert.That(
                    File.Exists(
                        generationRecordPath
                            stateDirectory
                            staleGeneration
                    ),
                    Is.True
                )))

    [<Test>]
    member _.``invalid manifest generations are preserved and never used as paths``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            use currentProcess = Process.GetCurrentProcess()

            let processStartTicks =
                currentProcess.StartTime
                    .ToUniversalTime()
                    .Ticks

            let invalidGenerations =
                [ Path.GetFullPath(
                      Path.Combine(tempDir, "absolute")
                  )
                  "..\\escape"
                  "../escape"
                  "nested\\generation"
                  "nested/generation"
                  "generation:stream"
                  "génération"
                  String.replicate 129 "a" ]

            invalidGenerations
            |> List.iteri (fun index generation ->
                let manifest =
                    JsonSerializer.Serialize(
                        {| version = 2
                           generation = generation
                           pid = Environment.ProcessId
                           processStartTicks =
                            string processStartTicks
                           processStartExact = true
                           ownershipBoundary =
                            "windows-job-v1"
                           controlPort = 41234
                           controlToken = "invalid-generation"
                           startedAt =
                            DateTimeOffset.UtcNow.ToString("O") |}
                    )

                let statePath =
                    Path.Combine(
                        stateDirectory,
                        "host.json"
                    )

                Directory.CreateDirectory stateDirectory
                |> ignore

                File.WriteAllText(statePath, manifest)

                let worktreePath =
                    Path.Combine(
                        tempDir,
                        $"invalid-{index}"
                    )

                Directory.CreateDirectory worktreePath
                |> ignore

                let snapshot =
                    start
                        manager
                        (canonical worktreePath)

                Assert.Multiple(fun () ->
                    Assert.That(
                        errorFor
                            (canonical worktreePath)
                            snapshot,
                        Does.Contain("generation")
                            .IgnoreCase
                    )
                    Assert.That(
                        File.ReadAllText statePath,
                        Is.EqualTo manifest
                    )
                    Assert.That(
                        File.Exists(
                            Path.Combine(
                                stateDirectory,
                                "launches.txt"
                            )
                        ),
                        Is.False
                    ))))

    [<Test>]
    member _.``generation evidence reparse containment fails closed``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let outside =
                Path.Combine(tempDir, "outside-evidence")

            let generationDirectory =
                Path.Combine(
                    stateDirectory,
                    "terminal-generations"
                )

            Directory.CreateDirectory stateDirectory
            |> ignore

            Directory.CreateDirectory outside |> ignore

            try
                Directory.CreateSymbolicLink(
                    generationDirectory,
                    outside
                )
                |> ignore
            with
            | :? UnauthorizedAccessException ->
                Assert.Ignore(
                    "Directory symbolic links are unavailable"
                )
            | :? IOException ->
                Assert.Ignore(
                    "Directory symbolic links are unavailable"
                )

            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath
            |> ignore

            let snapshot =
                start manager (canonical worktreePath)

            Assert.That(
                errorFor
                    (canonical worktreePath)
                    snapshot,
                Does.Contain("reparse")
                    .IgnoreCase
            ))

    [<Test>]
    member _.``runtime bundle tampering fails closed without rewriting the bundle``() =
        withFakeHost (fun _ _ hostConfig _ ->
            let bundle =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok value -> value
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            let supervisorPath =
                Path.Combine(
                    bundle.Directory,
                    "terminal-job-supervisor.ps1"
                )

            File.AppendAllText(
                supervisorPath,
                $"{Environment.NewLine}# tampered"
            )

            let tampered = File.ReadAllText supervisorPath

            match
                EmbeddedTerminal.materializeRuntimeBundle
                    hostConfig
            with
            | Ok _ ->
                Assert.Fail(
                    "A tampered immutable runtime bundle was accepted"
                )
            | Error error ->
                Assert.That(
                    error,
                    Does.Contain("hash mismatch")
                        .IgnoreCase
                )

            Assert.That(
                File.ReadAllText supervisorPath,
                Is.EqualTo tampered
            ))

    [<TestCase("ttyd.exe")>]
    [<TestCase("node_modules/ws/wrapper.mjs")>]
    member _.``runtime dependency tampering fails closed``(
        relativePath: string
    ) =
        withFakeHost (fun _ _ hostConfig _ ->
            let bundle =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok value -> value
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            let target =
                relativePath.Split('/')
                |> Array.fold (fun parent child ->
                    Path.Combine(parent, child)) bundle.Directory

            File.AppendAllText(target, "tampered")
            let tampered = File.ReadAllBytes target

            match
                EmbeddedTerminal.materializeRuntimeBundle
                    hostConfig
            with
            | Ok _ ->
                Assert.Fail(
                    "A tampered immutable runtime dependency was accepted"
                )
            | Error error ->
                Assert.That(
                    error,
                    Does.Contain("hash mismatch")
                        .IgnoreCase
                )

            Assert.That(
                File.ReadAllBytes target,
                Is.EqualTo tampered
            ))

    [<Test>]
    [<Platform("Win")>]
    member _.``runtime launch locks reject mutation before spawn and throughout host life``() =
        let runtimeFiles bundleDirectory =
            [ "bundle.json"
              "durable-terminal-host.mjs"
              "terminal-job-supervisor.ps1"
              "terminate-owned-process.ps1"
              "terminal-runtime-lock.ps1"
              "ttyd.exe"
              "node_modules/ws/wrapper.mjs" ]
            |> List.map (fun relativePath ->
                relativePath.Split('/')
                |> Array.fold (fun parent child ->
                    Path.Combine(parent, child)) bundleDirectory)

        let assertLocked path =
            Assert.Throws<IOException>(fun () ->
                use _ =
                    new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Read
                    )

                ())
            |> ignore

            Assert.Throws<IOException>(fun () ->
                File.Delete path)
            |> ignore

        let assertReleased path =
            use _ =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None
                )

            ()

        withFakeHost (fun tempDir _ hostConfig manager ->
            let bundle =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok value -> value
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            let launchLocks =
                match
                    EmbeddedTerminal.openRuntimeBundleLaunchLocks
                        bundle
                with
                | Ok value -> value
                | Error error ->
                    Assert.Fail(error)
                    []

            try
                runtimeFiles bundle.Directory
                |> List.iter assertLocked
            finally
                launchLocks |> List.iter _.Dispose()

            runtimeFiles bundle.Directory
            |> List.iter assertReleased

            let worktreePath =
                Path.Combine(tempDir, "lock lifetime worktree")
            let worktree = canonical worktreePath

            Directory.CreateDirectory worktreePath
            |> ignore

            Assert.That(
                start manager worktree
                |> endpointFor worktree
                |> String.IsNullOrWhiteSpace,
                Is.False
            )

            runtimeFiles bundle.Directory
            |> List.iter assertLocked

            match EmbeddedTerminal.shutdownHost manager |> run with
            | Error error -> Assert.Fail(error)
            | Ok () -> ()

            waitUntil
                "runtime lock handles to close"
                (fun () ->
                    try
                        runtimeFiles bundle.Directory
                        |> List.iter assertReleased

                        true
                    with :? IOException ->
                        false))

    [<Test>]
    [<Platform("Win")>]
    member _.``runtime lock owner crash makes the host unusable and releases every bundle handle``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "lock-owner-crash")
            let worktree = canonical worktreePath

            Directory.CreateDirectory worktreePath
            |> ignore

            start manager worktree |> ignore

            use manifest =
                Path.Combine(stateDirectory, "host.json")
                |> File.ReadAllText
                |> JsonDocument.Parse

            let root = manifest.RootElement
            let hostPid = root.GetProperty("pid").GetInt32()
            let lockOwnerPid =
                root.GetProperty("runtimeLockOwnerPid").GetInt32()
            let lockOwnerStartTicks =
                root
                    .GetProperty(
                        "runtimeLockOwnerProcessStartTicks"
                    )
                    .GetString()
                |> Int64.Parse

            let bundleDirectory =
                Path.Combine(
                    stateDirectory,
                    "terminal-runtime-bundles",
                    root
                        .GetProperty("extendedRuntime")
                        .GetProperty("bundleHash")
                        .GetString()
                )
            let hostPath =
                Path.Combine(
                    bundleDirectory,
                    "durable-terminal-host.mjs"
                )

            use owner = Process.GetProcessById lockOwnerPid

            Assert.That(
                owner.StartTime.ToUniversalTime().Ticks,
                Is.EqualTo lockOwnerStartTicks
            )

            owner.Kill true
            Assert.That(owner.WaitForExit 5000, Is.True)

            waitUntil
                "host exit after runtime lock loss"
                (fun () -> not (processIsAlive hostPid))

            waitUntil
                "bundle handles to release after lock-owner crash"
                (fun () ->
                    try
                        use _ =
                            new FileStream(
                                hostPath,
                                FileMode.Open,
                                FileAccess.ReadWrite,
                                FileShare.None
                            )

                        true
                    with :? IOException ->
                        false)

            let interrupted =
                EmbeddedTerminal.get manager |> run

            Assert.That(
                interruptedErrorFor worktree interrupted,
                Does.Contain("unavailable")
                    .IgnoreCase
            ))

    [<Test>]
    member _.``abandoned runtime bundle deletion claim is discarded and rematerialized``() =
        withFakeHost (fun _ _ hostConfig _ ->
            let original =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok bundle -> bundle
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            let claim =
                $"{original.Directory}.2147483000.{Guid.NewGuid():N}.reclaim"

            Directory.Move(original.Directory, claim)

            Assert.That(
                Directory.Exists original.Directory,
                Is.False
            )

            let recovered =
                match
                    EmbeddedTerminal.materializeRuntimeBundle
                        hostConfig
                with
                | Ok bundle -> bundle
                | Error error ->
                    Assert.Fail(error)
                    Unchecked.defaultof<_>

            Assert.Multiple(fun () ->
                Assert.That(
                    recovered.Identity,
                    Is.EqualTo original.Identity
                )
                Assert.That(
                    recovered.Directory,
                    Is.EqualTo original.Directory
                )
                Assert.That(
                    Directory.Exists recovered.Directory,
                    Is.True
                )
                Assert.That(
                    Directory.Exists claim,
                    Is.False
                )))

    [<Test>]
    member _.``concurrent startup compacts only excess unreferenced runtime bundles``() =
        withFakeHost (fun tempDir stateDirectory hostConfig firstManager ->
            let bundles =
                [ 0..10 ]
                |> List.map (fun index ->
                    File.AppendAllText(
                        hostConfig.HostScriptPath,
                        $"{Environment.NewLine}// bundle {index}"
                    )

                    match
                        EmbeddedTerminal.materializeRuntimeBundle
                            hostConfig
                    with
                    | Ok bundle -> bundle
                    | Error error ->
                        Assert.Fail(error)
                        Unchecked.defaultof<_>)

            let currentBundle =
                bundles |> List.last

            let secondManager =
                EmbeddedTerminal.createWithConfig
                    hostConfig

            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath

            let results =
                [ EmbeddedTerminal.start
                    firstManager
                    worktree
                  EmbeddedTerminal.start
                    secondManager
                    worktree ]
                |> Async.Parallel
                |> run

            let endpoints =
                results
                |> Array.map (function
                    | Ok snapshot ->
                        endpointFor worktree snapshot
                    | Error error ->
                        Assert.Fail(error)
                        "")

            let bundleDirectories =
                Directory.GetDirectories(
                    Path.Combine(
                        stateDirectory,
                        "terminal-runtime-bundles"
                    )
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    endpoints |> Array.distinct,
                    Has.Length.EqualTo(1)
                )
                Assert.That(
                    bundleDirectories,
                    Has.Length.EqualTo(9)
                )
                Assert.That(
                    Directory.Exists currentBundle.Directory,
                    Is.True
                )))

    [<Test>]
    member _.``protocol-two host drains before a changed deployment starts a bundled generation``() =
        withFakeHostConfig
            (fun original ->
                let directory =
                    Path.GetDirectoryName
                        original.HostScriptPath

                let supervisor =
                    Path.Combine(
                        directory,
                        "candidate-supervisor.ps1"
                    )

                let processHelper =
                    Path.Combine(
                        directory,
                        "candidate-process-helper.ps1"
                    )

                File.Copy(
                    original.SupervisorScriptPath,
                    supervisor
                )

                File.Copy(
                    original.ProcessIdentityHelperPath,
                    processHelper
                )

                { original with
                    SupervisorScriptPath = supervisor
                    ProcessIdentityHelperPath =
                        processHelper })
            (fun tempDir stateDirectory hostConfig manager ->
                let oldPath =
                    Path.Combine(tempDir, "old-session")

                let newPath =
                    Path.Combine(tempDir, "new-session")

                [ oldPath; newPath ]
                |> List.iter (Directory.CreateDirectory >> ignore)

                writeBehavior
                    stateDirectory
                    (JsonSerializer.Serialize(
                        {| protocolVersion = 2
                           upgradeAfterDrain = true
                           initialWorktreePaths =
                            [| oldPath |] |}
                    ))

                let oldWorktree = canonical oldPath
                let newWorktree = canonical newPath
                let oldSnapshot = start manager oldWorktree
                let oldEndpoint =
                    endpointFor
                        oldWorktree
                        oldSnapshot

                let bundleRoot =
                    Path.Combine(
                        stateDirectory,
                        "terminal-runtime-bundles"
                    )

                let oldBundle =
                    Directory.GetDirectories(bundleRoot)
                    |> Array.exactlyOne

                File.AppendAllText(
                    hostConfig.HostScriptPath,
                    $"{Environment.NewLine}// changed publish"
                )

                File.AppendAllText(
                    hostConfig.SupervisorScriptPath,
                    $"{Environment.NewLine}# changed publish"
                )

                let rejected =
                    EmbeddedTerminal.start
                        manager
                        newWorktree
                    |> run

                let retained =
                    EmbeddedTerminal.get manager
                    |> run

                Assert.Multiple(fun () ->
                    Assert.That(
                        endpointFor oldWorktree retained,
                        Is.EqualTo oldEndpoint
                    )
                    match rejected with
                    | Ok _ ->
                        Assert.Fail(
                            "Protocol-two host accepted an incompatible new start"
                        )
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain("drain-only")
                                .IgnoreCase
                        )
                    Assert.That(
                        File.ReadAllLines(
                            Path.Combine(
                                stateDirectory,
                                "launches.txt"
                            )
                        ).Length,
                        Is.EqualTo(1)
                    ))

                let afterClose =
                    EmbeddedTerminal.close
                        manager
                        oldWorktree
                    |> run

                let current = start manager newWorktree

                Assert.Multiple(fun () ->
                    Assert.That(
                        tryFindTab oldWorktree afterClose,
                        Is.EqualTo None
                    )
                    Assert.That(
                        endpointFor newWorktree current,
                        Is.Not.Empty
                    )
                    Assert.That(
                        readHostVersion stateDirectory,
                        Is.EqualTo 3
                    )
                    Assert.That(
                        Directory.Exists oldBundle,
                        Is.True
                    )
                    Assert.That(
                        Directory.GetDirectories(bundleRoot).Length,
                        Is.EqualTo 2
                    )
                    Assert.That(
                        File.ReadAllLines(
                            Path.Combine(
                                stateDirectory,
                                "launches.txt"
                            )
                        ).Length,
                        Is.EqualTo 2
                    )))

    [<Test>]
    member _.``changed current bundle drains before a new generation starts``() =
        withFakeHostConfig
            (fun original ->
                let directory =
                    Path.GetDirectoryName
                        original.HostScriptPath

                let supervisor =
                    Path.Combine(
                        directory,
                        "current-supervisor.ps1"
                    )

                let processHelper =
                    Path.Combine(
                        directory,
                        "current-process-helper.ps1"
                    )

                File.Copy(
                    original.SupervisorScriptPath,
                    supervisor
                )

                File.Copy(
                    original.ProcessIdentityHelperPath,
                    processHelper
                )

                { original with
                    SupervisorScriptPath = supervisor
                    ProcessIdentityHelperPath =
                        processHelper })
            (fun tempDir stateDirectory hostConfig manager ->
                let oldPath =
                    Path.Combine(tempDir, "old-current")

                let newPath =
                    Path.Combine(tempDir, "new-current")

                [ oldPath; newPath ]
                |> List.iter (Directory.CreateDirectory >> ignore)

                let oldWorktree = canonical oldPath
                let newWorktree = canonical newPath
                let oldSnapshot = start manager oldWorktree
                let oldEndpoint =
                    endpointFor oldWorktree oldSnapshot

                let bundleRoot =
                    Path.Combine(
                        stateDirectory,
                        "terminal-runtime-bundles"
                    )

                let oldBundle =
                    Directory.GetDirectories(bundleRoot)
                    |> Array.exactlyOne

                File.AppendAllText(
                    hostConfig.HostScriptPath,
                    $"{Environment.NewLine}// replacement bundle"
                )

                File.AppendAllText(
                    hostConfig.SupervisorScriptPath,
                    $"{Environment.NewLine}# replacement bundle"
                )

                let rejected =
                    EmbeddedTerminal.start
                        manager
                        newWorktree
                    |> run

                let retained =
                    EmbeddedTerminal.get manager |> run

                Assert.Multiple(fun () ->
                    match rejected with
                    | Ok _ ->
                        Assert.Fail(
                            "A changed deployment reused the old runtime for a new key"
                        )
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain(
                                "different immutable runtime bundle"
                            ).IgnoreCase
                        )

                    Assert.That(
                        endpointFor oldWorktree retained,
                        Is.EqualTo oldEndpoint
                    )
                    Assert.That(
                        File.ReadAllLines(
                            Path.Combine(
                                stateDirectory,
                                "launches.txt"
                            )
                        ).Length,
                        Is.EqualTo 1
                    ))

                EmbeddedTerminal.close
                    manager
                    oldWorktree
                |> run
                |> ignore

                let current = start manager newWorktree

                Assert.Multiple(fun () ->
                    Assert.That(
                        endpointFor newWorktree current,
                        Is.Not.Empty
                    )
                    Assert.That(
                        Directory.Exists oldBundle,
                        Is.True
                    )
                    Assert.That(
                        Directory.GetDirectories(bundleRoot).Length,
                        Is.EqualTo 2
                    )
                    Assert.That(
                        File.ReadAllLines(
                            Path.Combine(
                                stateDirectory,
                                "launches.txt"
                            )
                        ).Length,
                        Is.EqualTo 2
                    )))

    [<TestCase("ttyd")>]
    [<TestCase("ws")>]
    member _.``dependency-only deployment drains the old immutable bundle``(
        dependency
    ) =
        withFakeHostConfig
            (fun original ->
                let webSocketCopy =
                    Path.Combine(
                        Path.GetDirectoryName
                            original.HostScriptPath,
                        "ws-runtime"
                    )

                copyDirectory
                    original.WebSocketPackagePath
                    webSocketCopy

                { original with
                    WebSocketPackagePath =
                        webSocketCopy })
            (fun tempDir stateDirectory hostConfig manager ->
                let oldPath =
                    Path.Combine(
                        tempDir,
                        $"old-{dependency}"
                    )

                let newPath =
                    Path.Combine(
                        tempDir,
                        $"new-{dependency}"
                    )

                [ oldPath; newPath ]
                |> List.iter (Directory.CreateDirectory >> ignore)

                let oldWorktree = canonical oldPath
                let newWorktree = canonical newPath
                let oldSnapshot = start manager oldWorktree
                let oldEndpoint =
                    endpointFor oldWorktree oldSnapshot

                let bundleRoot =
                    Path.Combine(
                        stateDirectory,
                        "terminal-runtime-bundles"
                    )

                let oldBundle =
                    Directory.GetDirectories(bundleRoot)
                    |> Array.exactlyOne

                match dependency with
                | "ttyd" ->
                    File.AppendAllText(
                        hostConfig.TtydExecutablePath,
                        "replacement ttyd"
                    )
                | _ ->
                    File.AppendAllText(
                        Path.Combine(
                            hostConfig.WebSocketPackagePath,
                            "wrapper.mjs"
                        ),
                        $"{Environment.NewLine}// replacement ws"
                    )

                let rejected =
                    EmbeddedTerminal.start
                        manager
                        newWorktree
                    |> run

                let retained =
                    EmbeddedTerminal.get manager
                    |> run

                Assert.Multiple(fun () ->
                    match rejected with
                    | Ok _ ->
                        Assert.Fail(
                            "A dependency-only deployment reused the old immutable runtime"
                        )
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain(
                                "different immutable runtime bundle"
                            ).IgnoreCase
                        )

                    Assert.That(
                        endpointFor oldWorktree retained,
                        Is.EqualTo oldEndpoint
                    ))

                EmbeddedTerminal.close
                    manager
                    oldWorktree
                |> run
                |> ignore

                let current = start manager newWorktree

                Assert.Multiple(fun () ->
                    Assert.That(
                        endpointFor newWorktree current,
                        Is.Not.Empty
                    )
                    Assert.That(
                        Directory.Exists oldBundle,
                        Is.True
                    )
                    Assert.That(
                        Directory.GetDirectories(bundleRoot),
                        Has.Length.EqualTo(2)
                    )))

    [<Test>]
    member _.``protocol-two reservation closes only its key and preserves other live sessions``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let paths =
                [ "legacy-two-a"; "legacy-two-b" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path
                    |> ignore
                    path)

            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| protocolVersion = 2
                       initialWorktreePaths =
                        paths |> List.toArray |}
                ))

            let first = canonical paths[0]
            let second = canonical paths[1]
            let discovered = start manager first
            let secondEndpoint =
                endpointFor second discovered

            let legacyPid = readHostPid stateDirectory

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    first
                    (fun () ->
                        async {
                            Assert.That(
                                processIsAlive legacyPid,
                                Is.True
                            )

                            return Ok ()
                        })
                |> run

            let after = EmbeddedTerminal.get manager |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () -> ()
                | Error error -> Assert.Fail(error)

                Assert.That(
                    tryFindTab first after,
                    Is.EqualTo None
                )
                Assert.That(
                    endpointFor second after,
                    Is.EqualTo secondEndpoint
                )
                Assert.That(
                    processIsAlive legacyPid,
                    Is.True
                )
                Assert.That(
                    readHostVersion stateDirectory,
                    Is.EqualTo 2
                ))

            EmbeddedTerminal.close manager second
            |> run
            |> ignore)

    [<Test>]
    member _.``capability-bearing protocol-one host remains discoverable reusable and closable``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "legacy-worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| version = 1
                       protocolVersion = 1
                       initialWorktreePaths = [| worktreePath |] |}
                ))

            let discovered = start manager worktree
            let endpoint = endpointFor worktree discovered
            let reused = start manager worktree
            let version = readHostVersion stateDirectory
            let closed = EmbeddedTerminal.close manager worktree |> run

            Assert.Multiple(fun () ->
                Assert.That(version, Is.EqualTo(1))
                Assert.That(endpointFor worktree reused, Is.EqualTo endpoint)
                Assert.That(closed, Is.EqualTo EmbeddedTerminalSnapshot.empty)
                Assert.That(
                    File.Exists(
                        Path.Combine(stateDirectory, "host.json")
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``dead protocol-one state blocks replacement without cleanup proof``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            Directory.CreateDirectory stateDirectory |> ignore
            let stale =
                JsonSerializer.Serialize(
                    {| version = 1
                       pid = Environment.ProcessId
                       controlPort = 41234
                       controlToken = "legacy"
                       startedAt =
                        DateTimeOffset.UnixEpoch.ToString("O") |}
                )

            File.WriteAllText(
                Path.Combine(stateDirectory, "host.json"),
                stale
            )

            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let result =
                EmbeddedTerminal.start
                    manager
                    (canonical worktreePath)
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok _ ->
                    Assert.Fail(
                        "Dead protocol-one evidence authorized a replacement start"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("retired protocol-1")
                            .IgnoreCase
                    )

                Assert.That(
                    Directory.GetFiles(
                        Path.Combine(
                            stateDirectory,
                            "terminal-generations"
                        ),
                        "*.json"
                    )
                    |> Array.exists (fun path ->
                        use document =
                            File.ReadAllText path
                            |> JsonDocument.Parse

                        let root =
                            document.RootElement

                        root.GetProperty(
                            "hostProtocolVersion"
                        ).GetInt32() = 1
                        && root.GetProperty(
                            "sessionsUnknown"
                        ).GetBoolean()),
                    Is.True
                )))

    [<Test>]
    member _.``legacy cleanup compare does not delete a replacement manifest``() =
        Tests.TestUtils.withTempDir
            "legacy-terminal-replacement"
            (fun tempDir ->
                let statePath = Path.Combine(tempDir, "host.json")
                let legacy =
                    JsonSerializer.Serialize(
                        {| version = 1
                           pid = 701
                           controlPort = 41234
                           controlToken = "legacy"
                           startedAt =
                            DateTimeOffset.UnixEpoch.AddSeconds(1.0).ToString("O") |}
                    )

                let replacement =
                    JsonSerializer.Serialize(
                        {| version = 2
                           generation = "replacement"
                           pid = 701
                           processStartTicks = "200"
                           processStartExact = true
                           controlPort = 41235
                           controlToken = "replacement-token"
                           startedAt =
                            DateTimeOffset.UnixEpoch.AddSeconds(2.0).ToString("O") |}
                    )

                File.WriteAllText(statePath, replacement)
                let removed =
                    EmbeddedTerminal.removeManifestIfOwned
                        statePath
                        legacy

                Assert.Multiple(fun () ->
                    match removed with
                    | Ok value -> Assert.That(value, Is.False)
                    | Error error -> Assert.Fail(error)

                    Assert.That(
                        File.ReadAllText statePath,
                        Is.EqualTo replacement
                    )))

    [<Test>]
    member _.``capability-bearing legacy cleanup drains the host while mutation is reserved``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let paths =
                [ "legacy-a"; "legacy-b" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path |> ignore
                    path)

            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| protocolVersion = 1
                       upgradeAfterDrain = true
                       initialWorktreePaths = paths |> List.toArray |}
                ))

            let first = canonical paths[0]
            let second = canonical paths[1]
            let discovered = start manager first
            Assert.That(discovered.Tabs.Length, Is.EqualTo(2))
            let legacyPid = readHostPid stateDirectory

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    first
                    (fun () ->
                        async {
                            Assert.That(
                                processIsAlive legacyPid,
                                Is.False
                            )

                            return Ok ()
                        })
                |> run

            let after = EmbeddedTerminal.get manager |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () -> ()
                | Error error -> Assert.Fail(error)

                Assert.That(
                    interruptedErrorFor second after,
                    Does.Contain("protocol-1").IgnoreCase
                )
                Assert.That(tryFindTab first after, Is.EqualTo(None))))

    [<TestCase("delete")>]
    [<TestCase("archive")>]
    member _.``valid replacement wins capability-bearing legacy drain before strict mutation``(
        mutation
    ) =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let mainPath = Path.Combine(tempDir, "main")
            let targetPath = Path.Combine(tempDir, "target")
            let otherPath = Path.Combine(tempDir, "other")
            [ mainPath; targetPath; otherPath ]
            |> List.iter (Directory.CreateDirectory >> ignore)

            let target = canonical targetPath
            let other = canonical otherPath
            let replacementStateDirectory =
                Path.Combine(tempDir, "replacement-state")

            let replacementManager =
                EmbeddedTerminal.createWithConfig
                    { hostConfig with
                        HostStateDirectory =
                            replacementStateDirectory }

            try
                start replacementManager target |> ignore
                let replacementStatePath =
                    Path.Combine(
                        replacementStateDirectory,
                        "host.json"
                    )

                let replacementManifest =
                    File.ReadAllText replacementStatePath

                let replacementGeneration =
                    readHostGeneration replacementStateDirectory

                writeBehavior
                    stateDirectory
                    (JsonSerializer.Serialize(
                        {| protocolVersion = 1
                           initialWorktreePaths =
                            [| targetPath; otherPath |]
                           replacementManifestPath =
                            replacementStatePath |}
                    ))

                start manager target |> ignore

                let agent = SchedulerState.createAgent ()
                let repoId =
                    PathUtils.toRepoId (Path.GetFullPath tempDir)

                let worktrees: WorktreeInfo list =
                    [ { Path = mainPath
                        Head = "main-head"
                        Branch = Some "main" }
                      { Path = targetPath
                        Head = "target-head"
                        Branch = Some "feature" } ]

                agent.Post(
                    SchedulerState.StateMsg.UpdateWorktreeList(
                        repoId,
                        worktrees
                    )
                )

                agent.PostAndAsyncReply(
                    SchedulerState.StateMsg.GetState
                )
                |> run
                |> ignore

                let rootPaths =
                    Map.ofList [ repoId, tempDir ]

                let result =
                    match mutation with
                    | "delete" ->
                        WorktreeApi.deleteWorktreeWith
                            (fun _ _ _ -> async.Return(Ok()))
                            (EmbeddedTerminal.withReservedCleanup manager)
                            (fun _ -> async.Return())
                            agent
                            rootPaths
                            target
                        |> run
                    | "archive" ->
                        WorktreeApi.updateArchivedBranchesWith
                            agent
                            rootPaths
                            (EmbeddedTerminal.withReservedCleanup manager)
                            Set.add
                            target
                        |> run
                    | unexpected ->
                        failwith
                            $"Unexpected mutation '{unexpected}'"

                let state =
                    agent.PostAndAsyncReply(
                        SchedulerState.StateMsg.GetState
                    )
                    |> run

                let mutationObserved =
                    match mutation with
                    | "delete" ->
                        state.Repos[repoId].WorktreeList
                        |> List.exists (fun worktree ->
                            Shared.PathUtils.pathEquals
                                worktree.Path
                                targetPath)
                        |> not
                    | _ ->
                        TreemonConfig.readArchivedBranches tempDir
                        |> List.contains "feature"

                let after = EmbeddedTerminal.get manager |> run
                let replacementAfter =
                    EmbeddedTerminal.get replacementManager |> run

                Assert.Multiple(fun () ->
                    match result with
                    | Ok () -> ()
                    | Error error -> Assert.Fail(error)

                    Assert.That(mutationObserved, Is.True)
                    Assert.That(
                        readHostGeneration stateDirectory,
                        Is.EqualTo replacementGeneration
                    )
                    Assert.That(
                        File.ReadAllText(
                            Path.Combine(stateDirectory, "host.json")
                        ),
                        Is.EqualTo replacementManifest
                    )
                    Assert.That(
                        File.Exists replacementStatePath,
                        Is.True
                    )
                    Assert.That(
                        tryFindTab target after,
                        Is.EqualTo(None)
                    )
                    Assert.That(
                        interruptedErrorFor other after,
                        Does.Contain("protocol-1").IgnoreCase
                    )
                    Assert.That(
                        tryFindTab target replacementAfter,
                        Is.EqualTo(None)
                    ))
            finally
                EmbeddedTerminal.shutdownHost replacementManager
                |> run
                |> ignore)

    [<Test>]
    member _.``replacement generation preserves old tabs until each key is explicitly resolved``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let paths =
                [ "alpha"; "beta"; "delete-target"; "archive-target" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path |> ignore
                    canonical path)

            paths |> List.iter (start manager >> ignore)
            let oldManifestPath =
                Path.Combine(stateDirectory, "host.json")

            let oldManifest =
                File.ReadAllText oldManifestPath

            let oldPid = readHostPid stateDirectory
            let replacementStateDirectory =
                Path.Combine(tempDir, "replacement-state")

            let replacementManager =
                EmbeddedTerminal.createWithConfig
                    { hostConfig with
                        HostStateDirectory =
                            replacementStateDirectory }

            try
                start replacementManager paths[0] |> ignore
                let replacementManifest =
                    Path.Combine(
                        replacementStateDirectory,
                        "host.json"
                    )
                    |> File.ReadAllText

                File.WriteAllText(
                    oldManifestPath,
                    replacementManifest
                )

                let polled =
                    EmbeddedTerminal.get manager |> run

                let blockedMutation =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let blocked =
                    EmbeddedTerminal.withReservedCleanup
                        manager
                        paths[2]
                        (fun () ->
                            async {
                                blockedMutation.TrySetResult(())
                                |> ignore

                                return Ok ()
                            })
                    |> run

                let dismissed =
                    EmbeddedTerminal.close manager paths[1]
                    |> run

                crashFakeHostManifest oldManifest
                waitUntil "prior generation to exit" (fun () ->
                    processIsAlive oldPid |> not)

                let restarted = start manager paths[0]
                let agent = SchedulerState.createAgent ()
                let repoId =
                    PathUtils.toRepoId (Path.GetFullPath tempDir)

                let worktrees: WorktreeInfo list =
                    [ { Path = Path.Combine(tempDir, "main")
                        Head = "main-head"
                        Branch = Some "main" }
                      { Path = WorktreePath.value paths[2]
                        Head = "delete-head"
                        Branch = Some "delete-branch" }
                      { Path = WorktreePath.value paths[3]
                        Head = "archive-head"
                        Branch = Some "archive-branch" } ]

                agent.Post(
                    SchedulerState.StateMsg.UpdateWorktreeList(
                        repoId,
                        worktrees
                    )
                )

                agent.PostAndAsyncReply(
                    SchedulerState.StateMsg.GetState
                )
                |> run
                |> ignore

                let rootPaths =
                    Map.ofList [ repoId, tempDir ]

                let deleted =
                    WorktreeApi.deleteWorktreeWith
                        (fun _ _ _ -> async.Return(Ok()))
                        (EmbeddedTerminal.withReservedCleanup manager)
                        (fun _ -> async.Return())
                        agent
                        rootPaths
                        paths[2]
                    |> run

                let archived =
                    WorktreeApi.updateArchivedBranchesWith
                        agent
                        rootPaths
                        (EmbeddedTerminal.withReservedCleanup manager)
                        Set.add
                        paths[3]
                    |> run

                let after =
                    EmbeddedTerminal.get manager |> run

                Assert.Multiple(fun () ->
                    Assert.That(
                        polled.Tabs
                        |> List.filter (fun tab ->
                            match tab.Lifecycle with
                            | EmbeddedTerminalLifecycle.Interrupted _ ->
                                true
                            | _ -> false)
                        |> List.length,
                        Is.EqualTo(paths.Length)
                    )

                    Assert.That(
                        interruptedErrorFor paths[0] polled,
                        Does.Contain("generation changed").IgnoreCase
                    )

                    match blocked with
                    | Ok () ->
                        Assert.Fail(
                            "Replacement registry absence must not authorize cleanup while the prior host is alive"
                        )
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain("prior durable host generation")
                                .IgnoreCase
                        )

                    Assert.That(
                        blockedMutation.Task.IsCompleted,
                        Is.False
                    )
                    Assert.That(
                        tryFindTab paths[1] dismissed,
                        Is.EqualTo(None)
                    )
                    Assert.That(
                        endpointFor paths[0] restarted,
                        Is.Not.Empty
                    )

                    match deleted with
                    | Error error -> Assert.Fail(error)
                    | Ok () -> ()

                    match archived with
                    | Error error -> Assert.Fail(error)
                    | Ok () -> ()

                    Assert.That(
                        TreemonConfig.readArchivedBranches tempDir,
                        Does.Contain("archive-branch")
                    )
                    Assert.That(
                        after.Tabs |> List.map _.Worktree,
                        Is.EqualTo([ paths[0] ])
                    )
                    Assert.That(
                        File.ReadAllText oldManifestPath,
                        Is.EqualTo(replacementManifest)
                    ))
            finally
                EmbeddedTerminal.shutdownHost replacementManager
                |> run
                |> ignore)

    [<Test>]
    member _.``prior host death cannot bypass a still-running Job Object supervisor``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            use currentProcess = Process.GetCurrentProcess()
            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| supervisorPid = Environment.ProcessId
                       supervisorStartTimeUtcTicks =
                        string (
                            currentProcess.StartTime
                                .ToUniversalTime()
                                .Ticks
                        ) |}
                ))

            start manager worktree |> ignore
            let oldManifest =
                Path.Combine(stateDirectory, "host.json")
                |> File.ReadAllText

            let oldPid = readHostPid stateDirectory
            let replacementStateDirectory =
                Path.Combine(tempDir, "replacement-state")

            let replacementManager =
                EmbeddedTerminal.createWithConfig
                    { hostConfig with
                        HostStateDirectory =
                            replacementStateDirectory }

            try
                let replacementPath =
                    Path.Combine(
                        replacementStateDirectory,
                        "host.json"
                    )

                start replacementManager worktree |> ignore
                File.WriteAllText(
                    Path.Combine(stateDirectory, "host.json"),
                    File.ReadAllText replacementPath
                )

                crashFakeHostManifest oldManifest
                waitUntil "prior host to exit" (fun () ->
                    processIsAlive oldPid |> not)

                EmbeddedTerminal.get manager |> run |> ignore
                let mutationEntered =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let result =
                    EmbeddedTerminal.withReservedCleanup
                        manager
                        worktree
                        (fun () ->
                            async {
                                mutationEntered.TrySetResult(())
                                |> ignore

                                return Ok ()
                            })
                    |> run

                Assert.Multiple(fun () ->
                    match result with
                    | Ok () ->
                        Assert.Fail(
                            "A live prior supervisor must block strict cleanup"
                        )
                    | Error error ->
                        Assert.That(
                            error,
                            Does.Contain(
                                "Job Object supervisor is still running"
                            ).IgnoreCase
                        )

                    Assert.That(
                        mutationEntered.Task.IsCompleted,
                        Is.False
                    )
                    Assert.That(
                        File.Exists replacementPath,
                        Is.True
                    ))
            finally
                EmbeddedTerminal.shutdownHost replacementManager
                |> run
                |> ignore)

    [<Test>]
    member _.``fresh manager starts an unrelated replacement but rejects a live retired supervisor``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            use currentProcess = Process.GetCurrentProcess()

            writeBehavior
                stateDirectory
                (JsonSerializer.Serialize(
                    {| supervisorPid = Environment.ProcessId
                       supervisorStartTimeUtcTicks =
                        string (
                            currentProcess.StartTime
                                .ToUniversalTime()
                                .Ticks
                        ) |}
                ))

            start manager worktree |> ignore
            let retiredPid = readHostPid stateDirectory
            crashFakeHost stateDirectory

            waitUntil "retired host to exit" (fun () ->
                processIsAlive retiredPid |> not)

            let freshManager =
                EmbeddedTerminal.createWithConfig hostConfig

            let replacementPath =
                Path.Combine(tempDir, "replacement")

            Directory.CreateDirectory replacementPath
            |> ignore

            let replacementWorktree =
                canonical replacementPath

            let replacement =
                start freshManager replacementWorktree

            let replacementPid = readHostPid stateDirectory
            let rejectedStart =
                EmbeddedTerminal.start
                    freshManager
                    worktree
                |> run
            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let result =
                EmbeddedTerminal.withReservedCleanup
                    freshManager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                Assert.That(
                    endpointFor
                        replacementWorktree
                        replacement,
                    Is.Not.Empty
                )
                Assert.That(
                    replacementPid,
                    Is.Not.EqualTo retiredPid
                )

                match rejectedStart with
                | Ok _ ->
                    Assert.Fail(
                        "A live retired supervisor authorized a same-key replacement start"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("supervisor is still running")
                            .IgnoreCase
                    )

                match result with
                | Ok () ->
                    Assert.Fail(
                        "A fresh manager must not reserve cleanup while the retired supervisor is alive"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("supervisor is still running")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                )))

    [<Test>]
    member _.``fresh manager accepts a witness that arrives after an initial refusal``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let generation = "retired-late-witness"

            let session =
                fixtureSession
                    "late"
                    worktreePath
                    2_000_000_001
                    12345L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_002
                12344L
                3
                false
                [ session ]

            let replacementPath =
                Path.Combine(tempDir, "replacement-worktree")

            Directory.CreateDirectory replacementPath
            |> ignore

            let replacementWorktree =
                canonical replacementPath

            let replacement =
                start manager replacementWorktree
            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let mutate () =
                async {
                    mutationEntered.TrySetResult(())
                    |> ignore

                    return Ok ()
                }

            let refused =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    mutate
                |> run

            writeEmptyWitness
                stateDirectory
                generation
                session

            let accepted =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    mutate
                |> run

            Assert.Multiple(fun () ->
                Assert.That(
                    endpointFor
                        replacementWorktree
                        replacement,
                    Is.Not.Empty
                )

                match refused with
                | Ok () ->
                    Assert.Fail(
                        "Cleanup without the durable witness must fail"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("has not arrived")
                            .IgnoreCase
                    )

                match accepted with
                | Error error -> Assert.Fail(error)
                | Ok () -> ()

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.True
                )
                Assert.That(
                    File.Exists(
                        generationRecordPath
                            stateDirectory
                            generation
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``witness without terminal trusted promotion cannot authorize cleanup``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "unpromoted-worktree")

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath
            let generation = "retired-unpromoted-witness"

            let session =
                { fixtureSession
                    "unpromoted"
                    worktreePath
                    2_000_000_005
                    15001L with
                    TrustState = "in-progress" }

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_006
                15002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let rejectedStart =
                EmbeddedTerminal.start
                    manager
                    worktree
                |> run

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () ->
                    Assert.Fail(
                        "An unpromoted witness authorized cleanup"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("trusted-empty")
                            .IgnoreCase
                    )

                match rejectedStart with
                | Ok _ ->
                    Assert.Fail(
                        "An unpromoted witness authorized a replacement start"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("trusted-empty")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                )
                Assert.That(
                    File.Exists(
                        generationRecordPath
                            stateDirectory
                            generation
                    ),
                    Is.True
                )))

    [<Test>]
    member _.``retired witness reparse path cannot authorize cleanup``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "reparse-worktree")

            let outside =
                Path.Combine(tempDir, "outside-witness")

            Directory.CreateDirectory worktreePath
            |> ignore

            Directory.CreateDirectory outside |> ignore

            let worktree = canonical worktreePath
            let generation = "retired-reparse-witness"

            let session =
                fixtureSession
                    "reparse"
                    worktreePath
                    2_000_000_007
                    16001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_008
                16002L
                3
                false
                [ session ]

            let witnessRoot =
                Path.Combine(
                    stateDirectory,
                    "terminal-empty-witnesses"
                )

            Directory.CreateDirectory witnessRoot
            |> ignore

            try
                Directory.CreateSymbolicLink(
                    Path.Combine(witnessRoot, generation),
                    outside
                )
                |> ignore
            with
            | :? UnauthorizedAccessException ->
                Assert.Ignore(
                    "Directory symbolic links are unavailable"
                )
            | :? IOException ->
                Assert.Ignore(
                    "Directory symbolic links are unavailable"
                )

            writeEmptyWitness
                stateDirectory
                generation
                session

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () ->
                    Assert.Fail(
                        "A witness reached through a reparse point authorized cleanup"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("reparse")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                )))

    [<Test>]
    member _.``strict reservation requires every matching retired supervisor witness``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath

            let records =
                [ "retired-first", "first", 2_000_000_011, 21001L
                  "retired-second", "second", 2_000_000_012, 21002L ]
                |> List.map (fun (generation, suffix, pid, ticks) ->
                    let session =
                        fixtureSession
                            suffix
                            worktreePath
                            pid
                            ticks

                    writeGenerationRecord
                        stateDirectory
                        hostConfig
                        generation
                        (pid + 100)
                        (ticks + 100L)
                        3
                        false
                        [ session ]

                    generation, session)

            records
            |> List.head
            |> fun (generation, session) ->
                writeEmptyWitness
                    stateDirectory
                    generation
                    session

            let firstAttempt =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async.Return(Ok()))
                |> run

            records
            |> List.last
            |> fun (generation, session) ->
                writeEmptyWitness
                    stateDirectory
                    generation
                    session

            let secondAttempt =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async.Return(Ok()))
                |> run

            Assert.Multiple(fun () ->
                match firstAttempt with
                | Ok () ->
                    Assert.Fail(
                        "One of two retired supervisors was still unwitnessed"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("has not arrived")
                            .IgnoreCase
                    )

                match secondAttempt with
                | Error error -> Assert.Fail(error)
                | Ok () -> ()

                records
                |> List.iter (fun (generation, _) ->
                    Assert.That(
                        File.Exists(
                            generationRecordPath
                                stateDirectory
                                generation
                        ),
                        Is.False
                    ))))

    [<TestCase("delete")>]
    [<TestCase("archive")>]
    member _.``delete and archive wait for every exact retired witness``(
        mutation
    ) =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let mainPath = Path.Combine(tempDir, "main")
            let targetPath = Path.Combine(tempDir, "target")
            [ mainPath; targetPath ]
            |> List.iter (Directory.CreateDirectory >> ignore)

            let target = canonical targetPath
            let generation = $"retired-{mutation}-gate"

            let session =
                fixtureSession
                    mutation
                    targetPath
                    2_000_000_015
                    25001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_016
                25002L
                3
                false
                [ session ]

            let agent = SchedulerState.createAgent ()
            let repoId =
                PathUtils.toRepoId (Path.GetFullPath tempDir)

            agent.Post(
                SchedulerState.StateMsg.UpdateWorktreeList(
                    repoId,
                    [ { Path = mainPath
                        Head = "main-head"
                        Branch = Some "main" }
                      { Path = targetPath
                        Head = "target-head"
                        Branch = Some "feature" } ]
                )
            )

            agent.PostAndAsyncReply(
                SchedulerState.StateMsg.GetState
            )
            |> run
            |> ignore

            let rootPaths =
                Map.ofList [ repoId, tempDir ]

            let deleteInvoked =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let execute () =
                match mutation with
                | "delete" ->
                    WorktreeApi.deleteWorktreeWith
                        (fun _ _ _ ->
                            async {
                                deleteInvoked.TrySetResult(())
                                |> ignore

                                return Ok ()
                            })
                        (EmbeddedTerminal.withReservedCleanup manager)
                        (fun _ -> async.Return())
                        agent
                        rootPaths
                        target
                | _ ->
                    WorktreeApi.updateArchivedBranchesWith
                        agent
                        rootPaths
                        (EmbeddedTerminal.withReservedCleanup manager)
                        Set.add
                        target

            let refused = execute () |> run
            let deleteInvokedBefore =
                deleteInvoked.Task.IsCompleted
            let archivedBefore =
                TreemonConfig.readArchivedBranches tempDir

            writeEmptyWitness
                stateDirectory
                generation
                session

            let accepted = execute () |> run

            Assert.Multiple(fun () ->
                match refused with
                | Ok () ->
                    Assert.Fail(
                        $"{mutation} must wait for retired empty proof"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("has not arrived")
                            .IgnoreCase
                    )

                match accepted with
                | Error error -> Assert.Fail(error)
                | Ok () -> ()

                match mutation with
                | "delete" ->
                    Assert.That(
                        deleteInvokedBefore,
                        Is.False
                    )
                    Assert.That(
                        deleteInvoked.Task.IsCompleted,
                        Is.True
                    )
                | _ ->
                    Assert.That(
                        archivedBefore,
                        Does.Not.Contain("feature")
                    )
                    Assert.That(
                        TreemonConfig.readArchivedBranches tempDir,
                        Does.Contain("feature")
                    )))

    [<TestCase("nonce")>]
    [<TestCase("generation")>]
    [<TestCase("pid")>]
    member _.``forged retired witness cannot authorize cleanup``(
        mismatch
    ) =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, $"worktree-{mismatch}")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let generation = $"retired-forged-{mismatch}"

            let session =
                fixtureSession
                    mismatch
                    worktreePath
                    2_000_000_021
                    31001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_022
                31002L
                3
                false
                [ session ]

            match mismatch with
            | "nonce" ->
                writeEmptyWitness
                    stateDirectory
                    generation
                    { session with
                        WitnessNonce =
                            "forged-witness-nonce-000000000000" }
            | "generation" ->
                writeEmptyWitnessAs
                    stateDirectory
                    generation
                    "different-generation"
                    session
            | _ ->
                writeEmptyWitness
                    stateDirectory
                    generation
                    { session with
                        SupervisorPid =
                            session.SupervisorPid - 1 }

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () ->
                    Assert.Fail(
                        "A forged retired witness must not authorize cleanup"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("does not match")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                )))

    [<Test>]
    member _.``sticky supervisor protocol failure rejects an otherwise valid witness``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "mixed-transcript")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let generation = "retired-mixed-transcript"

            let session =
                { fixtureSession
                    "mixed"
                    worktreePath
                    2_000_000_025
                    35001L with
                    TrustState = "quarantined" }

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_026
                35002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async.Return(Ok()))
                |> run

            match result with
            | Ok () ->
                Assert.Fail(
                    "A mixed-trust supervisor transcript must remain terminal"
                )
            | Error error ->
                Assert.That(
                    error,
                    Does.Contain("sticky protocol failure")
                        .IgnoreCase
                ))

    [<Test>]
    member _.``dead protocol-one generation fails closed with manual-drain guidance``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            Directory.CreateDirectory stateDirectory |> ignore

            File.WriteAllText(
                Path.Combine(stateDirectory, "host.json"),
                JsonSerializer.Serialize(
                    {| version = 1
                       pid = Environment.ProcessId
                       controlPort = 41234
                       controlToken = "legacy"
                       startedAt =
                        DateTimeOffset.UnixEpoch.ToString("O") |}
                )
            )

            let worktreePath =
                Path.Combine(tempDir, "legacy-worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult(())
                            |> ignore

                            return Ok ()
                        })
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () ->
                    Assert.Fail(
                        "Dead protocol-one evidence must fail closed"
                    )
                | Error error ->
                    Assert.That(
                        error,
                        Does.Contain("protocol-1")
                            .And.Contain("manually drain")
                            .IgnoreCase
                    )

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False
                )))

    [<Test>]
    member _.``fully witnessed generation compaction is race-idempotent``() =
        withFakeHost (fun tempDir stateDirectory hostConfig firstManager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let generation = "retired-compaction-race"

            let session =
                fixtureSession
                    "race"
                    worktreePath
                    2_000_000_031
                    41001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_032
                41002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let secondManager =
                EmbeddedTerminal.createWithConfig hostConfig

            let results =
                [ EmbeddedTerminal.closeStrict
                    firstManager
                    worktree
                  EmbeddedTerminal.closeStrict
                    secondManager
                    worktree ]
                |> Async.Parallel
                |> run

            Assert.Multiple(fun () ->
                results
                |> Array.iter (function
                    | Ok _ -> ()
                    | Error error -> Assert.Fail(error))

                Assert.That(
                    File.Exists(
                        generationRecordPath
                            stateDirectory
                            generation
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``abandoned generation compaction claim is recovered before cleanup``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "claim-recovery")

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath
            let generation = "retired-claim-recovery"

            let session =
                fixtureSession
                    "claim"
                    worktreePath
                    2_000_000_041
                    51001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_042
                51002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let record =
                generationRecordPath
                    stateDirectory
                    generation

            let claim =
                $"{record}.{Environment.ProcessId}.{Guid.NewGuid():N}.reclaim"

            File.Move(record, claim)

            let result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    worktree
                    (fun () -> async { return Ok () })
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok () -> ()
                | Error error -> Assert.Fail(error)

                Assert.That(File.Exists record, Is.False)
                Assert.That(File.Exists claim, Is.False)))

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``manager recovers current and legacy host generation claims``(
        legacy
    ) =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(
                    tempDir,
                    if legacy then
                        "legacy-host-claim"
                    else
                        "current-host-claim"
                )

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath
            let generation =
                if legacy then
                    "legacy-host-generation"
                else
                    "current-host-generation"

            let session =
                fixtureSession
                    (if legacy then "legacy-host" else "current-host")
                    worktreePath
                    2_000_000_043
                    52001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_044
                52002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let record =
                generationRecordPath
                    stateDirectory
                    generation

            let directory = Path.GetDirectoryName record
            let claimName =
                if legacy then
                    $"{generation}.json.2000000045.legacy_nonce_123456.reclaim.json"
                else
                    $"{generation}.json.dead-owner.2000000045.52003.current_nonce_123456.reclaim"

            let claim =
                Path.Combine(directory, claimName)

            File.Move(record, claim)

            let result =
                EmbeddedTerminal.closeStrict
                    manager
                    worktree
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail(error)

                Assert.That(File.Exists record, Is.False)
                Assert.That(File.Exists claim, Is.False)
                Assert.That(
                    File.Exists(
                        witnessPath
                            stateDirectory
                            generation
                            session.SessionId
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``parallel manager never reclaims a live compactor claim``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let worktreePath =
                Path.Combine(tempDir, "live-compactor")

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath
            let generation = "live-compactor-generation"
            let session =
                fixtureSession
                    "live-compactor"
                    worktreePath
                    2_000_000_046
                    53001L

            writeGenerationRecord
                stateDirectory
                hostConfig
                generation
                2_000_000_047
                53002L
                3
                false
                [ session ]

            writeEmptyWitness
                stateDirectory
                generation
                session

            let ownerInfo =
                ProcessStartInfo(
                    FileName = "node",
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            ownerInfo.ArgumentList.Add("-e")
            ownerInfo.ArgumentList.Add(
                "setInterval(() => {}, 1000)"
            )

            use owner = Process.Start ownerInfo
            let startTicks =
                owner.StartTime.ToUniversalTime().Ticks
            let record =
                generationRecordPath
                    stateDirectory
                    generation
            let claim =
                Path.Combine(
                    Path.GetDirectoryName record,
                    $"{generation}.json.active-owner.{owner.Id}.{startTicks}.active_nonce_123456.reclaim"
                )

            File.Move(record, claim)

            let stopOwner () =
                if not owner.HasExited then
                    owner.Kill(true)
                    owner.WaitForExit()

            try
                let pending =
                    EmbeddedTerminal.closeStrict
                        manager
                        worktree
                    |> Async.StartAsTask

                Thread.Sleep 100

                Assert.Multiple(fun () ->
                    Assert.That(pending.IsCompleted, Is.False)
                    Assert.That(File.Exists record, Is.False)
                    Assert.That(File.Exists claim, Is.True))

                stopOwner ()

                match pending.GetAwaiter().GetResult() with
                | Error error -> Assert.Fail(error)
                | Ok _ ->
                    Assert.Multiple(fun () ->
                        Assert.That(File.Exists record, Is.False)
                        Assert.That(File.Exists claim, Is.False)
                        Assert.That(
                            File.Exists(
                                witnessPath
                                    stateDirectory
                                    generation
                                    session.SessionId
                            ),
                            Is.False
                        ))
            finally
                stopOwner ())

    [<Test>]
    member _.``generation compaction faults preserve proof until record removal commits``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            let stages =
                [ EmbeddedTerminal.GenerationCompactionStage.BeforeRename
                  EmbeddedTerminal.GenerationCompactionStage.AfterRename
                  EmbeddedTerminal.GenerationCompactionStage.BeforeClaimDeletion
                  EmbeddedTerminal.GenerationCompactionStage.AfterClaimDeletion
                  EmbeddedTerminal.GenerationCompactionStage.DuringWitnessCleanup ]

            stages
            |> List.iteri (fun index stage ->
                let worktreePath =
                    Path.Combine(
                        tempDir,
                        $"compaction-stage-{index}"
                    )

                Directory.CreateDirectory worktreePath
                |> ignore

                let worktree = canonical worktreePath
                let generation = $"compaction-stage-{index}"
                let sessions =
                    [ fixtureSession
                        $"stage-{index}-first"
                        worktreePath
                        (2_000_000_100 + index * 10)
                        (54000L + int64 (index * 10))
                      fixtureSession
                        $"stage-{index}-second"
                        worktreePath
                        (2_000_000_101 + index * 10)
                        (54001L + int64 (index * 10)) ]

                writeGenerationRecord
                    stateDirectory
                    hostConfig
                    generation
                    (2_000_000_102 + index * 10)
                    (54002L + int64 (index * 10))
                    3
                    false
                    sessions

                sessions
                |> List.iter (writeEmptyWitness stateDirectory generation)

                let record =
                    generationRecordPath
                        stateDirectory
                        generation

                EmbeddedTerminal.compactGenerationForTest
                    hostConfig
                    generation
                    ((=) stage)
                |> ignore

                let claims =
                    Directory.GetFiles(
                        Path.GetDirectoryName record,
                        $"{generation}.json.*.reclaim"
                    )

                match stage with
                | EmbeddedTerminal.GenerationCompactionStage.BeforeRename ->
                    Assert.That(File.Exists record, Is.True)
                    Assert.That(claims, Is.Empty)
                | EmbeddedTerminal.GenerationCompactionStage.AfterRename
                | EmbeddedTerminal.GenerationCompactionStage.BeforeClaimDeletion ->
                    Assert.That(File.Exists record, Is.False)
                    Assert.That(claims, Has.Length.EqualTo(1))
                    Assert.That(
                        sessions
                        |> List.forall (fun session ->
                            File.Exists(
                                witnessPath
                                    stateDirectory
                                    generation
                                    session.SessionId
                            )),
                        Is.True
                    )
                    File.Move(claims[0], record)
                | EmbeddedTerminal.GenerationCompactionStage.AfterClaimDeletion ->
                    Assert.That(File.Exists record, Is.False)
                    Assert.That(claims, Is.Empty)
                    Assert.That(
                        sessions
                        |> List.forall (fun session ->
                            File.Exists(
                                witnessPath
                                    stateDirectory
                                    generation
                                    session.SessionId
                            )),
                        Is.True
                    )
                | EmbeddedTerminal.GenerationCompactionStage.DuringWitnessCleanup ->
                    Assert.That(File.Exists record, Is.False)
                    Assert.That(claims, Is.Empty)
                    Assert.That(
                        sessions
                        |> List.filter (fun session ->
                            File.Exists(
                                witnessPath
                                    stateDirectory
                                    generation
                                    session.SessionId
                            )),
                        Has.Length.EqualTo(1)
                    )

                match
                    EmbeddedTerminal.closeStrict
                        manager
                        worktree
                    |> run
                with
                | Error error -> Assert.Fail(error)
                | Ok _ ->
                    Assert.That(File.Exists record, Is.False)
                    Assert.That(
                        Directory.Exists(
                            Path.Combine(
                                stateDirectory,
                                "terminal-empty-witnesses",
                                generation
                            )
                        ),
                        Is.False
                    )))

    [<Test>]
    member _.``unresolved generation retention is bounded without discarding evidence``() =
        withFakeHost (fun tempDir stateDirectory hostConfig manager ->
            [ 1..64 ]
            |> List.iter (fun index ->
                writeGenerationRecord
                    stateDirectory
                    hostConfig
                    $"unresolved-{index}"
                    (1_900_000_000 + index)
                    (int64 index)
                    3
                    true
                    [])

            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore

            let result =
                EmbeddedTerminal.start
                    manager
                    (canonical worktreePath)
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Ok snapshot ->
                    Assert.That(
                        errorFor
                            (canonical worktreePath)
                            snapshot,
                        Does.Contain("retention reached 64")
                            .IgnoreCase
                    )
                | Error error ->
                    Assert.Fail(error)

                Assert.That(
                    Directory.GetFiles(
                        Path.Combine(
                            stateDirectory,
                            "terminal-generations"
                        ),
                        "*.json"
                    ).Length,
                    Is.EqualTo(64)
                )
                Assert.That(
                    File.Exists(
                        Path.Combine(
                            stateDirectory,
                            "launches.txt"
                        )
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``dead known host remains visible as interrupted and failed key recovers``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            start manager worktree |> ignore
            let deadPid = readHostPid stateDirectory

            crashFakeHost stateDirectory
            waitUntil "fixture host to exit" (fun () ->
                processIsAlive deadPid |> not)

            let interrupted = EmbeddedTerminal.get manager |> run
            Assert.That(
                interruptedErrorFor worktree interrupted,
                Does.Contain("unavailable").IgnoreCase
            )

            let recovered = start manager worktree
            Assert.Multiple(fun () ->
                Assert.That(
                    endpointFor worktree recovered,
                    Is.Not.Empty
                )
                Assert.That(
                    readHostPid stateDirectory,
                    Is.Not.EqualTo(deadPid)
                )))

    [<Test>]
    member _.``shutdown of an already-dead host retains its unresolved generation``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            writeBehavior
                stateDirectory
                """{"omitCrashWitness":true}"""
            start manager worktree |> ignore
            let generation =
                readHostGeneration stateDirectory
            let deadPid = readHostPid stateDirectory

            crashFakeHost stateDirectory
            waitUntil "fixture host to exit" (fun () ->
                processIsAlive deadPid |> not)

            let result =
                EmbeddedTerminal.shutdownHost manager
                |> run

            Assert.Multiple(fun () ->
                match result with
                | Error error -> Assert.Fail(error)
                | Ok () -> ()

                Assert.That(
                    File.Exists(
                        generationRecordPath
                            stateDirectory
                            generation
                    ),
                    Is.True
                )
                Assert.That(
                    File.Exists(
                        Path.Combine(
                            stateDirectory,
                            "host.json"
                        )
                    ),
                    Is.False
                )))

    [<Test>]
    member _.``unknown current generation retains its immutable bundle identity``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let worktreePath =
                Path.Combine(tempDir, "worktree")

            Directory.CreateDirectory worktreePath
            |> ignore

            let worktree = canonical worktreePath
            writeBehavior
                stateDirectory
                """{"omitCrashWitness":true,"omitCrashGeneration":true}"""

            start manager worktree |> ignore

            let generation =
                readHostGeneration stateDirectory

            use manifest =
                Path.Combine(stateDirectory, "host.json")
                |> File.ReadAllText
                |> JsonDocument.Parse

            let bundleHash =
                manifest.RootElement
                    .GetProperty("bundleHash")
                    .GetString()
            let extendedBundleHash =
                manifest.RootElement
                    .GetProperty("extendedRuntime")
                    .GetProperty("bundleHash")
                    .GetString()

            File.Delete(
                generationRecordPath
                    stateDirectory
                    generation
            )

            let deadPid = readHostPid stateDirectory
            crashFakeHost stateDirectory

            waitUntil "fixture host to exit" (fun () ->
                processIsAlive deadPid |> not)

            EmbeddedTerminal.get manager |> run |> ignore

            use retained =
                generationRecordPath
                    stateDirectory
                    generation
                |> File.ReadAllText
                |> JsonDocument.Parse

            Assert.Multiple(fun () ->
                Assert.That(
                    retained.RootElement
                        .GetProperty("version")
                        .GetInt32(),
                    Is.EqualTo 2
                )
                Assert.That(
                    retained.RootElement
                        .GetProperty("sessionsUnknown")
                        .GetBoolean(),
                    Is.True
                )
                Assert.That(
                    retained.RootElement
                        .GetProperty("bundleHash")
                        .GetString(),
                    Is.EqualTo bundleHash
                )
                Assert.That(
                    retained.RootElement
                        .GetProperty("extendedRuntime")
                        .GetProperty("bundleHash")
                        .GetString(),
                    Is.EqualTo extendedBundleHash
                )
                Assert.That(
                    Directory.Exists(
                        Path.Combine(
                            stateDirectory,
                            "terminal-runtime-bundles",
                            extendedBundleHash
                        )
                    ),
                    Is.True
                )))

    [<Test>]
    member _.``interrupted tabs dismiss restart and poll independently``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let paths =
                [ "alpha"; "bravo"; "charlie" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path |> ignore
                    canonical path)

            paths |> List.iter (start manager >> ignore)
            let deadPid = readHostPid stateDirectory
            crashFakeHost stateDirectory
            waitUntil "fixture host to exit" (fun () ->
                processIsAlive deadPid |> not)

            let interrupted = EmbeddedTerminal.get manager |> run
            let dismissed =
                EmbeddedTerminal.close manager paths[1] |> run
            let restarted = start manager paths[0]
            let polled = EmbeddedTerminal.get manager |> run
            let afterLastDismiss =
                EmbeddedTerminal.close manager paths[2] |> run

            Assert.Multiple(fun () ->
                Assert.That(
                    interrupted.Tabs
                    |> List.filter (fun tab ->
                        match tab.Lifecycle with
                        | EmbeddedTerminalLifecycle.Interrupted _ ->
                            true
                        | _ -> false)
                    |> List.length,
                    Is.EqualTo(3)
                )
                Assert.That(
                    tryFindTab paths[1] dismissed,
                    Is.EqualTo(None)
                )
                Assert.That(endpointFor paths[0] restarted, Is.Not.Empty)
                Assert.That(
                    interruptedErrorFor paths[2] restarted,
                    Does.Contain("unavailable").IgnoreCase
                )
                Assert.That(
                    polled.Tabs |> List.map _.Worktree,
                    Is.EqualTo([ paths[0]; paths[2] ])
                )
                Assert.That(
                    polled.Tabs
                    |> List.distinctBy (fun tab ->
                        WorktreePath.value tab.Worktree
                        |> PathUtils.normalizePath)
                    |> List.length,
                    Is.EqualTo(polled.Tabs.Length)
                )
                Assert.That(
                    afterLastDismiss.Tabs
                    |> List.map _.Worktree,
                    Is.EqualTo([ paths[0] ])
                )))

    [<Test>]
    member _.``duplicate interrupted closes stay dismissed without changing other tabs``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            let paths =
                [ "alpha"; "bravo"; "charlie" ]
                |> List.map (fun name ->
                    let path = Path.Combine(tempDir, name)
                    Directory.CreateDirectory path |> ignore
                    canonical path)

            paths |> List.iter (start manager >> ignore)
            let deadPid = readHostPid stateDirectory
            crashFakeHost stateDirectory
            waitUntil "fixture host to exit" (fun () ->
                processIsAlive deadPid |> not)

            EmbeddedTerminal.get manager |> run |> ignore
            let afterFirst =
                EmbeddedTerminal.close manager paths[1] |> run
            let afterDuplicate =
                EmbeddedTerminal.close manager paths[1] |> run
            let afterOther =
                EmbeddedTerminal.close manager paths[0] |> run
            let afterOtherDuplicate =
                EmbeddedTerminal.close manager paths[0] |> run

            Assert.Multiple(fun () ->
                Assert.That(afterDuplicate, Is.EqualTo afterFirst)
                Assert.That(
                    afterOtherDuplicate,
                    Is.EqualTo afterOther
                )
                Assert.That(
                    afterOther.Tabs |> List.map _.Worktree,
                    Is.EqualTo([ paths[2] ])
                )
                Assert.That(
                    interruptedErrorFor paths[2] afterOther,
                    Does.Contain("unavailable").IgnoreCase
                )
                Assert.That(
                    tryFindTab paths[0] afterOther,
                    Is.EqualTo(None)
                )
                Assert.That(
                    tryFindTab paths[1] afterOther,
                    Is.EqualTo(None)
                )))

[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalPortableRuntimeTests() =
    let materialize hostConfig =
        match EmbeddedTerminal.materializeRuntimeBundle hostConfig with
        | Ok bundle -> bundle
        | Error error ->
            Assert.Fail(error)
            Unchecked.defaultof<_>

    [<Test>]
    member _.``protocol-three manifest keeps the previous compatibility view and additive locked identity``() =
        withFakeHost (fun _ _ hostConfig _ ->
            let bundle = materialize hostConfig

            use manifest =
                Path.Combine(bundle.Directory, "bundle.json")
                |> File.ReadAllText
                |> JsonDocument.Parse

            let root = manifest.RootElement
            let compatibilityFiles =
                root.GetProperty("files").EnumerateArray()
                |> Seq.map (fun file ->
                    file.GetProperty("name").GetString())
                |> Seq.toList
            let compatibilityCapabilities =
                root
                    .GetProperty("capabilities")
                    .EnumerateArray()
                |> Seq.map _.GetString()
                |> Set.ofSeq
            let extended =
                root.GetProperty("extendedRuntime")
            let extendedFiles =
                extended.GetProperty("files").EnumerateArray()
                |> Seq.map (fun file ->
                    file.GetProperty("name").GetString())
                |> Set.ofSeq

            Assert.Multiple(fun () ->
                Assert.That(
                    root
                        .GetProperty("runtimeBundleVersion")
                        .GetInt32(),
                    Is.EqualTo 1
                )
                Assert.That(
                    compatibilityCapabilities,
                    Is.EqualTo(
                        set
                            [ "immutable-runtime-bundle-v1"
                              "strict-evidence-paths-v1"
                              "trusted-empty-supervisor-v1" ]
                    )
                )
                Assert.That(
                    compatibilityFiles,
                    Is.EqualTo(
                        [ "durable-terminal-host.mjs"
                          "terminal-job-supervisor.ps1"
                          "terminate-owned-process.ps1" ]
                    )
                )
                Assert.That(
                    extended.GetProperty("version").GetInt32(),
                    Is.EqualTo 3
                )
                Assert.That(
                    extendedFiles,
                    Does.Contain("terminal-runtime-lock.ps1")
                )
                Assert.That(
                    extendedFiles,
                    Does.Contain("ttyd.exe")
                )
                Assert.That(
                    extendedFiles,
                    Does.Contain(
                        "node_modules/ws/wrapper.mjs"
                    )
                )))

    [<Test>]
    member _.``materialization rename faults recover without trusting staging``() =
        [ EmbeddedTerminal.RuntimeBundleStage.BeforeCanonicalRename
          EmbeddedTerminal.RuntimeBundleStage.AfterCanonicalRename ]
        |> List.iter (fun stage ->
            withFakeHost (fun _ stateDirectory hostConfig _ ->
                let failed =
                    EmbeddedTerminal.materializeRuntimeBundleWithFault
                        hostConfig
                        (fun current ->
                            if current = stage then
                                raise (
                                    IOException(
                                        $"Injected {stage}"
                                    )
                                ))

                match failed with
                | Ok _ ->
                    Assert.Fail(
                        $"Injected materialization fault {stage} was ignored"
                    )
                | Error _ -> ()

                let recovered = materialize hostConfig
                let artifacts =
                    Directory.GetDirectories(
                        Path.Combine(
                            stateDirectory,
                            "terminal-runtime-bundles"
                        ),
                        "*",
                        SearchOption.TopDirectoryOnly
                    )
                    |> Array.filter (fun path ->
                        path.EndsWith(
                            ".pending",
                            StringComparison.Ordinal
                        )
                        || path.EndsWith(
                            ".tombstone",
                            StringComparison.Ordinal
                        ))

                Assert.Multiple(fun () ->
                    Assert.That(
                        Directory.Exists recovered.Directory,
                        Is.True
                    )
                    Assert.That(artifacts, Is.Empty))))

    [<Test>]
    [<Platform("Win")>]
    member _.``active materializers and deletions coexist while stale partial artifacts are discarded``() =
        withFakeHost (fun _ stateDirectory hostConfig _ ->
            let original = materialize hostConfig
            let root = Path.GetDirectoryName original.Directory
            let hash = Path.GetFileName original.Directory
            let ownerStartTicks =
                Process
                    .GetCurrentProcess()
                    .StartTime
                    .ToUniversalTime()
                    .Ticks
            let activeTombstone =
                Path.Combine(
                    root,
                    $"{hash}.delete.{Environment.ProcessId}.{ownerStartTicks}.{Guid.NewGuid():N}.tombstone"
                )
            let activePending =
                Path.Combine(
                    root,
                    $"{hash}.stage.{Environment.ProcessId}.{ownerStartTicks}.{Guid.NewGuid():N}.pending"
                )
            let activeTombstoneLease =
                new FileStream(
                    $"{activeTombstone}.lease",
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            let activePendingLease =
                new FileStream(
                    $"{activePending}.lease",
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None
                )

            Directory.Move(
                original.Directory,
                activeTombstone
            )
            Directory.CreateDirectory activePending
            |> ignore
            File.WriteAllText(
                Path.Combine(activePending, "partial"),
                "not a bundle"
            )

            let rematerialized = materialize hostConfig

            Assert.Multiple(fun () ->
                Assert.That(
                    Directory.Exists rematerialized.Directory,
                    Is.True
                )
                Assert.That(
                    Directory.Exists activeTombstone,
                    Is.True
                )
                Assert.That(
                    Directory.Exists activePending,
                    Is.True
                ))

            activeTombstoneLease.Dispose()
            activePendingLease.Dispose()
            File.Delete $"{activeTombstone}.lease"
            File.Delete $"{activePending}.lease"

            File.Delete(
                Path.Combine(
                    activeTombstone,
                    "node_modules",
                    "ws",
                    "wrapper.mjs"
                )
            )

            let staleName phase suffix =
                Path.Combine(
                    root,
                    $"{hash}.{phase}.2147483000.1.{Guid.NewGuid():N}.{suffix}"
                )

            let staleTombstone =
                staleName
                    "delete"
                    "tombstone"
            let stalePending =
                staleName
                    "stage"
                    "pending"

            Directory.Move(activeTombstone, staleTombstone)
            Directory.Move(activePending, stalePending)
            materialize hostConfig |> ignore

            Assert.Multiple(fun () ->
                Assert.That(
                    Directory.Exists staleTombstone,
                    Is.False
                )
                Assert.That(
                    Directory.Exists stalePending,
                    Is.False
                )
                Assert.That(
                    Directory.Exists rematerialized.Directory,
                    Is.True
                )))

    [<Test>]
    member _.``parallel materializers converge on one verified canonical bundle``() =
        withFakeHost (fun _ stateDirectory hostConfig _ ->
            let tasks =
                [| 1..2 |]
                |> Array.map (fun _ ->
                    Task.Run(fun () ->
                        EmbeddedTerminal.materializeRuntimeBundle
                            hostConfig))

            let results =
                Task.WhenAll(tasks)
                    .GetAwaiter()
                    .GetResult()

            let bundles =
                results
                |> Array.map (function
                    | Ok bundle -> bundle
                    | Error error ->
                        Assert.Fail(error)
                        Unchecked.defaultof<_>)

            let canonicalDirectories =
                Directory.GetDirectories(
                    Path.Combine(
                        stateDirectory,
                        "terminal-runtime-bundles"
                    )
                )
                |> Array.filter (fun path ->
                    let name = Path.GetFileName path
                    name.Length = 64
                    && name |> Seq.forall Uri.IsHexDigit)

            Assert.Multiple(fun () ->
                Assert.That(
                    bundles
                    |> Array.map _.Identity
                    |> Array.distinct,
                    Has.Length.EqualTo(1)
                )
                Assert.That(
                    bundles
                    |> Array.map _.Directory
                    |> Array.distinct,
                    Has.Length.EqualTo(1)
                )
                Assert.That(
                    canonicalDirectories,
                    Has.Length.EqualTo(1)
                )))

    [<Test>]
    member _.``partial deletion tombstones never become canonical and rollback rematerializes``() =
        withFakeHost (fun _ _ hostConfig _ ->
            let original = materialize hostConfig
            let faulted =
                EmbeddedTerminal.compactRuntimeBundleWithFault
                    hostConfig
                    (fun stage ->
                        if
                            stage
                            = EmbeddedTerminal.RuntimeBundleStage.DuringTombstoneDeletion
                        then
                            raise (
                                IOException(
                                    "Injected recursive deletion crash"
                                )
                            ))
                    original

            match faulted with
            | Ok () ->
                Assert.Fail(
                    "Injected recursive deletion fault was ignored"
                )
            | Error _ -> ()

            let tombstone =
                Directory.GetDirectories(
                    Path.GetDirectoryName original.Directory,
                    "*.tombstone"
                )
                |> Array.exactlyOne

            File.Delete(
                Path.Combine(
                    tombstone,
                    "terminal-job-supervisor.ps1"
                )
            )

            let recovered = materialize hostConfig

            Assert.Multiple(fun () ->
                Assert.That(
                    Directory.Exists tombstone,
                    Is.False
                )
                Assert.That(
                    Directory.Exists recovered.Directory,
                    Is.True
                ))

            match
                EmbeddedTerminal.compactRuntimeBundleWithFault
                    hostConfig
                    ignore
                    recovered
            with
            | Error error -> Assert.Fail(error)
            | Ok () -> ()

            Assert.That(
                Directory.Exists recovered.Directory,
                Is.False
            )

            let rollback = materialize hostConfig

            Assert.Multiple(fun () ->
                Assert.That(
                    rollback.Identity,
                    Is.EqualTo original.Identity
                )
                Assert.That(
                    Directory.Exists rollback.Directory,
                    Is.True
                )))

[<Category("DurableRollback")>]
[<Platform("Win")>]
[<NonParallelizable>]
type EmbeddedTerminalRollbackCompatibilityTests() =
    let runExternal
        (workingDirectory: string)
        (fileName: string)
        (arguments: string list)
        (timeout: TimeSpan)
        =
        let psi =
            ProcessStartInfo(
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        arguments |> List.iter psi.ArgumentList.Add
        use proc = new Process(StartInfo = psi)

        if not (proc.Start()) then
            Assert.Fail($"Could not start {fileName}")

        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()

        if not (proc.WaitForExit(int timeout.TotalMilliseconds)) then
            proc.Kill true
            proc.WaitForExit 5000 |> ignore
            Assert.Fail($"{fileName} timed out")

        Task.WhenAll(stdout, stderr)
            .GetAwaiter()
            .GetResult()
        |> ignore

        let output = stdout.Result
        let error = stderr.Result

        if proc.ExitCode <> 0 then
            Assert.Fail(
                $"{fileName} exited {proc.ExitCode}{Environment.NewLine}{output}{Environment.NewLine}{error}"
            )

        output

    let previousRunnerSource =
        """
module PreviousRollbackClient

open System
open System.IO
open Server
open Shared

let run workflow = Async.RunSynchronously workflow

[<EntryPoint>]
let main arguments =
    if arguments.Length <> 4 then
        eprintfn "Expected state, source, worktree, and ttyd paths"
        2
    else
        let stateDirectory = Path.GetFullPath arguments[0]
        let sourceDirectory = Path.GetFullPath arguments[1]
        let worktreePath = Path.GetFullPath arguments[2]
        let ttydPath = Path.GetFullPath arguments[3]
        let config: EmbeddedTerminal.Config =
            { NodeExecutable = "node"
              HostScriptPath =
                Path.Combine(sourceDirectory, "scripts", "durable-terminal-host.mjs")
              SupervisorScriptPath =
                Path.Combine(sourceDirectory, "scripts", "terminal-job-supervisor.ps1")
              ProcessIdentityHelperPath =
                Path.Combine(sourceDirectory, "scripts", "terminate-owned-process.ps1")
              HostStateDirectory = stateDirectory
              TtydExecutablePath = ttydPath
              ShellCommand = "pwsh"
              StartupTimeout = TimeSpan.FromSeconds 30.0
              ControlRequestTimeout = TimeSpan.FromSeconds 30.0
              ProbeInterval = TimeSpan.FromMilliseconds 50.0
              ReservationRenewalInterval = TimeSpan.FromSeconds 30.0 }

        match EmbeddedTerminal.materializeRuntimeBundle config with
        | Error error ->
            eprintfn "Previous bundle materialization failed: %s" error
            3
        | Ok _ ->
            let manager = EmbeddedTerminal.createWithConfig config
            let worktree = Server.PathUtils.toWorktreePath worktreePath
            let listed = EmbeddedTerminal.get manager |> run
            let found =
                listed.Tabs
                |> List.filter (fun tab ->
                    Shared.PathUtils.pathEquals
                        (WorktreePath.value tab.Worktree)
                        worktreePath)

            if found.Length <> 1 then
                eprintfn "Previous client listed %d matching sessions" found.Length
                4
            else
                let closed = EmbeddedTerminal.close manager worktree |> run

                if not closed.Tabs.IsEmpty then
                    eprintfn "Previous client close retained %d sessions" closed.Tabs.Length
                    5
                else
                    match EmbeddedTerminal.shutdownHost manager |> run with
                    | Error error ->
                        eprintfn "Previous client drain failed: %s" error
                        6
                    | Ok () ->
                        printfn "previous-client:list=1;close=0;drain=ok"
                        0
"""

    let previousRunnerProject =
        """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Tests</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <RollForward>LatestMajor</RollForward>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
    <ProjectReference Include="..\source\src\Server\Server.fsproj" />
  </ItemGroup>
</Project>
"""

    [<Test>]
    member _.``previous protocol-three binary lists closes and drains the current locked host``() =
        let repositoryRoot =
            Path.GetFullPath(
                Path.Combine(
                    __SOURCE_DIRECTORY__,
                    "..",
                    ".."
                )
            )
        let fixture =
            Path.Combine(
                repositoryRoot,
                ".agents",
                "rollback-compat-tests",
                Guid.NewGuid().ToString("N")
            )
        let source = Path.Combine(fixture, "source")
        let runner = Path.Combine(fixture, "runner")
        let output = Path.Combine(fixture, "output")
        let stateDirectory =
            Path.Combine(fixture, "state with spaces")
        let worktreePath =
            Path.Combine(fixture, "worktree with spaces")
        let archive = Path.Combine(fixture, "parent.tar")
        let ttydPath =
            Path.Combine(
                repositoryRoot,
                ".tools",
                "ttyd",
                "1.7.7",
                "ttyd.exe"
            )

        Directory.CreateDirectory source |> ignore
        Directory.CreateDirectory runner |> ignore
        Directory.CreateDirectory worktreePath |> ignore

        // The fixture retains owned processes across failures so finally can stop only their exact PIDs.
        let mutable manager: EmbeddedTerminal.Manager option = None
        let mutable hostPid: int option = None
        let mutable lockOwnerPid: int option = None

        try
            runExternal
                repositoryRoot
                "git"
                [ "archive"
                  "--format=tar"
                  $"--output={archive}"
                  "5af773d0" ]
                (TimeSpan.FromMinutes 1.0)
            |> ignore

            runExternal
                repositoryRoot
                "tar"
                [ "-xf"; archive; "-C"; source ]
                (TimeSpan.FromMinutes 1.0)
            |> ignore

            File.Delete archive
            File.WriteAllText(
                Path.Combine(runner, "Program.fs"),
                previousRunnerSource
            )
            let runnerProject =
                Path.Combine(
                    runner,
                    "PreviousRollbackClient.fsproj"
                )
            File.WriteAllText(
                runnerProject,
                previousRunnerProject
            )

            runExternal
                repositoryRoot
                "dotnet"
                [ "publish"
                  runnerProject
                  "-c"
                  "Release"
                  "-o"
                  output
                  "--nologo" ]
                (TimeSpan.FromMinutes 5.0)
            |> ignore

            let currentConfig: EmbeddedTerminal.Config =
                { NodeExecutable = "node"
                  HostScriptPath =
                    Path.Combine(
                        repositoryRoot,
                        "scripts",
                        "durable-terminal-host.mjs"
                    )
                  SupervisorScriptPath =
                    Path.Combine(
                        repositoryRoot,
                        "scripts",
                        "terminal-job-supervisor.ps1"
                    )
                  ProcessIdentityHelperPath =
                    Path.Combine(
                        repositoryRoot,
                        "scripts",
                        "terminate-owned-process.ps1"
                    )
                  RuntimeLockHelperPath =
                    Path.Combine(
                        repositoryRoot,
                        "scripts",
                        "terminal-runtime-lock.ps1"
                    )
                  WebSocketPackagePath =
                    Path.Combine(
                        repositoryRoot,
                        "node_modules",
                        "ws"
                    )
                  HostStateDirectory = stateDirectory
                  TtydExecutablePath = ttydPath
                  TtydExpectedHash = None
                  ShellCommand = "pwsh"
                  StartupTimeout = TimeSpan.FromSeconds 30.0
                  ControlRequestTimeout = TimeSpan.FromSeconds 30.0
                  ProbeInterval = TimeSpan.FromMilliseconds 50.0
                  ReservationRenewalInterval =
                    TimeSpan.FromSeconds 30.0 }

            let currentManager =
                EmbeddedTerminal.createWithConfig
                    currentConfig
            manager <- Some currentManager
            let worktree = canonical worktreePath

            match EmbeddedTerminal.start currentManager worktree |> run with
            | Error error -> Assert.Fail(error)
            | Ok snapshot ->
                match
                    snapshot
                    |> tryFindTab worktree
                    |> Option.map _.Lifecycle
                with
                | Some (EmbeddedTerminalLifecycle.Running endpoint) ->
                    Assert.That(endpoint, Is.Not.Empty)
                | lifecycle ->
                    let diagnosticsPath =
                        Path.Combine(
                            stateDirectory,
                            "diagnostics.jsonl"
                        )
                    let diagnostics =
                        if File.Exists diagnosticsPath then
                            File.ReadAllText diagnosticsPath
                        else
                            ""
                    let runtimeStatus =
                        Directory.GetFiles(
                            stateDirectory,
                            "runtime-ready-*.status.json"
                        )
                        |> Array.map File.ReadAllText
                        |> String.concat Environment.NewLine

                    Assert.Fail(
                        $"Current host startup failed: {lifecycle}{Environment.NewLine}{runtimeStatus}{Environment.NewLine}{diagnostics}"
                    )

            use manifest =
                Path.Combine(stateDirectory, "host.json")
                |> File.ReadAllText
                |> JsonDocument.Parse

            hostPid <-
                Some(
                    manifest.RootElement
                        .GetProperty("pid")
                        .GetInt32()
                )
            lockOwnerPid <-
                Some(
                    manifest.RootElement
                        .GetProperty("runtimeLockOwnerPid")
                        .GetInt32()
                )

            let previousOutput =
                runExternal
                    repositoryRoot
                    "dotnet"
                    [ Path.Combine(output, "Tests.dll")
                      stateDirectory
                      source
                      worktreePath
                      ttydPath ]
                    (TimeSpan.FromMinutes 2.0)

            Assert.That(
                previousOutput,
                Does.Contain(
                    "previous-client:list=1;close=0;drain=ok"
                )
            )

            waitUntil
                "current host and lock owner to drain"
                (fun () ->
                    (hostPid
                     |> Option.forall (
                         processIsAlive >> not
                     ))
                    && (lockOwnerPid
                        |> Option.forall (
                            processIsAlive >> not
                        )))
        finally
            manager
            |> Option.iter (fun currentManager ->
                EmbeddedTerminal.shutdownHost currentManager
                |> run
                |> ignore)

            hostPid
            |> Option.iter (fun pid ->
                waitUntil
                    "rollback fixture host cleanup"
                    (fun () -> not (processIsAlive pid)))

            lockOwnerPid
            |> Option.iter (fun pid ->
                waitUntil
                    "rollback fixture lock-owner cleanup"
                    (fun () -> not (processIsAlive pid)))

            if Directory.Exists fixture then
                Directory.Delete(fixture, true)
