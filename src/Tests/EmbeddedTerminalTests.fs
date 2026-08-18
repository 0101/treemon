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
import { randomBytes } from "node:crypto";
import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { resolve, join } from "node:path";

const args = process.argv.slice(2);
const stateDirectory = resolve(args[args.indexOf("--state-dir") + 1]);
const generation = args[args.indexOf("--generation") + 1];
const statePath = join(stateDirectory, "host.json");
const lockPath = join(stateDirectory, "host.lock");
const eventPath = join(stateDirectory, "events.json");
const behaviorPath = join(stateDirectory, "behavior.json");
const launchesPath = join(stateDirectory, "launches.txt");
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

const manifestVersion = behavior().protocolVersion ?? 2;

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
  const temporary = `${path}.${process.pid}.tmp`;
  writeFileSync(temporary, JSON.stringify(value));
  renameSync(temporary, path);
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
        ? { version: 1, pid: process.pid, startedAt }
        : {
            version: 2,
            generation,
            pid: process.pid,
            processStartTicks,
            processStartExact: true,
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
    if (!existing) appendSession(body.worktreePath);
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
      sessions = sessions.filter((session) => session.id !== id);
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
    sessions = sessions.filter((session) => session.key !== key);
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
      } else if (manifestVersion === 1 && currentBehavior.upgradeAfterDrain) {
        writeJson(behaviorPath, {
          ...currentBehavior,
          protocolVersion: 2,
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
    setImmediate(() => server.close(() => process.exit(0)));
  } else {
    send(response, 404, { error: "Not found" });
  }
});

mkdirSync(stateDirectory, { recursive: true });
server.listen(0, "127.0.0.1", async () => {
  const { port } = server.address();
  const claim = await startupClaim();
  processStartTicks = claim.hostProcessStartTicks;
  if (existsSync(statePath)) process.exit(3);
  appendFileSync(launchesPath, `${process.pid}\n`);
  const manifest =
    manifestVersion === 1
      ? {
          version: 1,
          pid: process.pid,
          controlPort: port,
          controlToken: token,
          startedAt,
        }
      : {
          version: 2,
          generation,
          pid: process.pid,
          processStartTicks,
          processStartExact: true,
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
      HostStateDirectory = stateDirectory
      TtydExecutablePath = ttydPath
      ShellCommand = "pwsh"
      StartupTimeout = TimeSpan.FromSeconds 5.0
      ControlRequestTimeout = TimeSpan.FromSeconds 5.0
      ProbeInterval = TimeSpan.FromMilliseconds 25.0
      ReservationRenewalInterval = TimeSpan.FromSeconds 30.0 }

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

let private crashFakeHost stateDirectory =
    use document =
        Path.Combine(stateDirectory, "host.json")
        |> File.ReadAllText
        |> JsonDocument.Parse

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
                Directory.CreateDirectory worktreePath |> ignore
                let worktree = canonical worktreePath
                start manager worktree |> ignore
                writeBehavior
                    stateDirectory
                    """{"renewStatus":500}"""

                let mutationEntered =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let releaseMutation =
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
                                do!
                                    releaseMutation.Task
                                    |> Async.AwaitTask

                                return Ok ()
                            })
                    |> Async.StartAsTask

                mutationEntered.Task.GetAwaiter().GetResult()

                try
                    waitUntil
                        "reservation renewal failure"
                        (fun () ->
                            Path.Combine(
                                stateDirectory,
                                "reservation-renewals.txt"
                            )
                            |> File.Exists)

                    Assert.That(
                        canAcquireWorktreeLock
                            stateDirectory
                            worktree,
                        Is.False
                    )
                finally
                    releaseMutation.TrySetResult(()) |> ignore

                let result = reserved.GetAwaiter().GetResult()

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

                    Assert.That(
                        canAcquireWorktreeLock
                            stateDirectory
                            worktree,
                        Is.True
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
    member _.``stale manifest with a reused PID is replaced by a fresh generation``() =
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
            let started = start manager worktree

            Assert.Multiple(fun () ->
                Assert.That(started.Tabs.Length, Is.EqualTo(1))
                Assert.That(
                    readHostGeneration stateDirectory,
                    Is.Not.EqualTo(staleGeneration)
                )))

    [<Test>]
    member _.``live protocol-one host remains discoverable reusable and closable``() =
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
    member _.``dead protocol-one state is reclaimed after process identity mismatch``() =
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
            let started = start manager (canonical worktreePath)

            Assert.Multiple(fun () ->
                Assert.That(started.Tabs.Length, Is.EqualTo(1))
                Assert.That(readHostVersion stateDirectory, Is.EqualTo(2))))

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
    member _.``strict legacy cleanup drains the host while mutation is reserved``() =
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
    member _.``valid replacement wins legacy drain before strict worktree mutation``(
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
