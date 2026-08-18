module Tests.EmbeddedTerminalTests

open System
open System.Diagnostics
open System.IO
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
import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { resolve, join } from "node:path";

const args = process.argv.slice(2);
const stateDirectory = resolve(args[args.indexOf("--state-dir") + 1]);
const statePath = join(stateDirectory, "host.json");
const eventPath = join(stateDirectory, "events.json");
const token = randomBytes(16).toString("hex");
const startedAt = new Date().toISOString();
let sessions = [];
let nextOrder = 0;

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
    send(response, 200, { version: 1, pid: process.pid, startedAt });
  } else if (request.method === "GET" && url.pathname === "/sessions") {
    send(response, 200, { sessions });
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
    send(response, 200, { sessions });
  } else if (request.method === "DELETE" && url.pathname.startsWith("/sessions/")) {
    const id = decodeURIComponent(url.pathname.slice("/sessions/".length));
    sessions = sessions.filter((session) => session.id !== id);
    send(response, 200, { sessions });
  } else if (request.method === "POST" && url.pathname === "/events") {
    const body = await readBody(request);
    const events = existsSync(eventPath)
      ? JSON.parse(readFileSync(eventPath, "utf8"))
      : [];
    writeJson(eventPath, [...events, body]);
    send(response, 200, { recorded: true });
  } else if (request.method === "POST" && url.pathname === "/shutdown") {
    send(response, 202, { stopping: true });
    setImmediate(() => server.close(() => {
      rmSync(statePath, { force: true });
      process.exit(0);
    }));
  } else {
    send(response, 404, { error: "Not found" });
  }
});

mkdirSync(stateDirectory, { recursive: true });
server.listen(0, "127.0.0.1", () => {
  const { port } = server.address();
  writeJson(statePath, {
    version: 1,
    pid: process.pid,
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
      ProbeInterval = TimeSpan.FromMilliseconds 25.0 }

let private readHostPid stateDirectory =
    use document =
        Path.Combine(stateDirectory, "host.json")
        |> File.ReadAllText
        |> JsonDocument.Parse

    document.RootElement.GetProperty("pid").GetInt32()

let private withFakeHost test =
    Tests.TestUtils.withTempDir "durable-terminal-host" (fun tempDir ->
        let stateDirectory = Path.Combine(tempDir, "state")
        let hostScript = Path.Combine(tempDir, "fake-host.mjs")
        let ttydPath = Path.Combine(tempDir, "fake-ttyd.exe")
        File.WriteAllText(hostScript, fakeHostScript)
        File.WriteAllText(ttydPath, "")
        let hostConfig = config stateDirectory hostScript ttydPath
        let manager = EmbeddedTerminal.createWithConfig hostConfig

        try
            test tempDir stateDirectory hostConfig manager
        finally
            EmbeddedTerminal.shutdownHost manager |> run |> ignore)

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
