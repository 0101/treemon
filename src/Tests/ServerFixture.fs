module Tests.ServerFixture

open System
open System.Diagnostics
open System.IO
open System.Text
open NUnit.Framework
open Server

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath =
    Path.Combine(repoRoot, "src", "Server")

let private fixturesPath =
    Path.Combine(repoRoot, "src", "Tests", "fixtures", "worktrees.json")

let private worktreeRoots = [ repoRoot ]

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None
let private terminalHostStateDirectory = TestUtils.terminalHostStateDirectory ()

// Pick three distinct free loopback ports up front (TestUtils.getFreeTcpPorts binds :0, reads the
// assigned ports, then releases them) for the API server, the canvas-doc server, and Vite — so the
// E2E stack avoids a running production Treemon (which owns 5000/5002) or a previous test run, and
// never has to kill another process to free a port.
// The canvas port is threaded into the client build via CANVAS_PORT -> Vite `define` so the client's
// iframe origin (CanvasPane.CanvasOrigin) matches this fixture's canvas-doc server.
let private apiPort, canvasPort, vitePort =
    match TestUtils.getFreeTcpPorts 3 with
    | [ a; c; v ] -> a, c, v
    | other -> failwith $"Expected 3 free ports, got {List.length other}"

let serverUrl = $"http://localhost:{apiPort}"
let viteUrl = $"http://localhost:{vitePort}"
let canvasUrl = $"http://127.0.0.1:{canvasPort}"

let private memoryThreshold = 2L * 1024L * 1024L * 1024L

type ProcessMemoryStats =
    { Name: string
      PeakWorkingSet: int64
      ExceededThreshold: bool }

let private readMemoryStats (name: string) (procOpt: Process option) =
    procOpt
    |> Option.bind (fun p ->
        try
            if not p.HasExited then
                p.Refresh()
            let peak = p.PeakWorkingSet64
            Some { Name = name; PeakWorkingSet = peak; ExceededThreshold = peak > memoryThreshold }
        with _ ->
            None)

let getMemoryStats () =
    [ readMemoryStats "Server" serverProcess.Value
      readMemoryStats "Vite" viteProcess.Value ]
    |> List.choose id

let startServer () =
    task {
        let rootArgs = worktreeRoots |> List.map (fun r -> $"\"{r}\"") |> String.concat " "

        let proc =
            TestUtils.startServerProcess serverProjectPath repoRoot rootArgs apiPort canvasPort fixturesPath terminalHostStateDirectory

        serverProcess.Value <- Some proc
        do! TestUtils.waitForUrl serverUrl 30000
    }

let internal runFableCompile
    (capture:
        ProcessRunner.Spawn
            -> string list
            -> Async<Result<ProcessRunner.ArgumentListOutput, ProcessRunner.ArgumentListFailure>>)
    =
    task {
        let clientDir = Path.Combine("src", "Client")
        let outDir = Path.Combine(clientDir, "output")

        let spawn =
            { ProcessRunner.Spawn.create "dotnet" with
                Context = "Fable compilation"
                Deadline = ProcessRunner.Timeout 60_000
                WorkingDirectory = Some repoRoot }

        let! result =
            capture
                spawn
                [ "fable"; clientDir; "--outDir"; outDir ]
            |> Async.StartAsTask

        match result with
        | Error ProcessRunner.TimedOut ->
            failwith "Fable compilation timed out after 60s"
        | Error(ProcessRunner.StartFailed message) ->
            failwith $"Fable compilation failed to start: {message}"
        | Ok output ->
            let stdout = Encoding.UTF8.GetString(output.Stdout)
            let stderr = Encoding.UTF8.GetString(output.Stderr)

            TestContext.Out.WriteLine($"Fable compilation output:{Environment.NewLine}{stdout}")

            if output.ExitCode <> 0 then
                failwith $"Fable compilation failed (exit code {output.ExitCode}): {stderr}"
    }

let compileFable () =
    runFableCompile ProcessRunner.capture

let startVite () =
    task {
        let proc =
            TestUtils.startViteProcess repoRoot vitePort apiPort canvasPort

        viteProcess.Value <- Some proc
        do! TestUtils.waitForUrl viteUrl 15000
    }

let private killProc procOpt =
    TestUtils.killProc procOpt

let private formatBytes (bytes: int64) =
    $"%.1f{float bytes / (1024.0 * 1024.0)} MB"

let stopAll () =
    let stats = getMemoryStats ()

    stats
    |> List.iter (fun s ->
        let status = if s.ExceededThreshold then "EXCEEDED THRESHOLD" else "OK"
        TestContext.Out.WriteLine(
            $"[Memory] {s.Name}: peak {formatBytes s.PeakWorkingSet} ({status})"))

    killProc serverProcess.Value
    killProc viteProcess.Value
    serverProcess.Value <- None
    viteProcess.Value <- None
    TestUtils.stopTerminalHostState terminalHostStateDirectory
    |> fun result ->
        TestUtils.assertOk result "Dashboard TerminalHost cleanup failed"

[<SetUpFixture>]
type GlobalSetup() =
    [<OneTimeSetUp>]
    member _.Setup() =
        task {
            do! startServer ()
            do! compileFable ()
            do! startVite ()
            TestContext.Out.WriteLine(
                $"Server ({serverUrl}), canvas-doc ({canvasUrl}), Fable, and Vite ({viteUrl}) started successfully")
        }

    [<OneTimeTearDown>]
    member _.TearDown() = stopAll ()
