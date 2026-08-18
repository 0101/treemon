module Tests.EmbeddedTerminalTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open NUnit.Framework
open Shared
open Server

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

const delay = (milliseconds) =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));

const behavior = () =>
  existsSync(behaviorPath)
    ? JSON.parse(readFileSync(behaviorPath, "utf8"))
    : {};

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
    send(response, current.healthStatus ?? 200, {
      version: 2,
      generation,
      pid: process.pid,
      processStartTicks,
      processStartExact: true,
      startedAt,
    });
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
      sessions = [
        ...sessions,
        {
          id: randomBytes(8).toString("hex"),
          key,
          worktreePath: body.worktreePath,
          lifecycle: "running",
          endpoint: `http://127.0.0.1:${42000 + nextOrder}/?cap=fake`,
          error: null,
          order: nextOrder++,
        },
      ];
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
      sessions = sessions.filter((session) => session.id !== id);
    }
    if (current.deleteTransportFailure) {
      request.socket.destroy();
    } else if (current.deleteMalformedResponse) {
      response.end("{");
    } else {
      send(response, current.deleteStatus ?? 200, { sessions });
    }
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
      if (existsSync(statePath)) {
        const current = JSON.parse(readFileSync(statePath, "utf8"));
        if (current.generation === generation && current.processStartTicks === processStartTicks) {
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
  writeJson(statePath, {
    version: 2,
    generation,
    pid: process.pid,
    processStartTicks,
    processStartExact: true,
    controlPort: port,
    controlToken: token,
    startedAt,
  });
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
      ProbeInterval = TimeSpan.FromMilliseconds 25.0 }

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
    member _.``unsupported manifest is preserved instead of being reclaimed without ownership``() =
        withFakeHost (fun tempDir stateDirectory _ manager ->
            Directory.CreateDirectory stateDirectory |> ignore
            let statePath =
                Path.Combine(stateDirectory, "host.json")

            let legacyState =
                JsonSerializer.Serialize(
                    {| version = 1
                       pid = Environment.ProcessId
                       controlPort = 41234
                       controlToken = "legacy"
                       startedAt = DateTimeOffset.UnixEpoch.ToString("O") |}
                )

            File.WriteAllText(statePath, legacyState)
            let worktreePath = Path.Combine(tempDir, "worktree")
            Directory.CreateDirectory worktreePath |> ignore
            let worktree = canonical worktreePath
            let failed = start manager worktree

            Assert.Multiple(fun () ->
                Assert.That(
                    errorFor worktree failed,
                    Does.Contain("protocol version 1")
                )
                Assert.That(
                    File.ReadAllText(statePath),
                    Is.EqualTo(legacyState)
                )
                Assert.That(
                    File.Exists(
                        Path.Combine(stateDirectory, "launches.txt")
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
                errorFor worktree interrupted,
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
