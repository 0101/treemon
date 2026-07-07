module Tests.ServerFixture

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading.Tasks
open NUnit.Framework

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath =
    Path.Combine(repoRoot, "src", "Server")

let private fixturesPath =
    Path.Combine(repoRoot, "src", "Tests", "fixtures", "worktrees.json")

let private worktreeRoots = [ repoRoot ]

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

// Reserve three distinct free loopback ports up front (TestUtils.getFreeTcpPorts) for the API server,
// the canvas-doc server, and Vite — so the E2E stack never collides with a running production Treemon
// (which owns 5000/5002) or a previous test run, and never has to kill another process to free a port.
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

let private tryGet (client: HttpClient) (url: string) =
    async {
        try
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return int response.StatusCode < 500
        with
        | _ -> return false
    }

let rec private pollUntilReady (client: HttpClient) (url: string) (deadline: DateTime) =
    async {
        if DateTime.UtcNow > deadline then
            failwithf "Timed out waiting for %s" url
        else
            let! ok = tryGet client url

            if not ok then
                do! Async.Sleep(500)
                return! pollUntilReady client url deadline
    }

let private waitForUrl (url: string) (timeoutMs: int) : Task =
    let work =
        async {
            use client = new HttpClient()
            let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
            do! pollUntilReady client url deadline
        }

    work |> Async.StartAsTask :> Task

let private startProcess fileName args workingDir envVars redirectOutput =
    TestUtils.startProcess fileName args workingDir envVars redirectOutput

let startServer () =
    task {
        let rootArgs = worktreeRoots |> List.map (fun r -> $"\"{r}\"") |> String.concat " "

        let proc =
            startProcess
                "dotnet"
                $"""run --project "{serverProjectPath}" -- {rootArgs} --port {apiPort} --canvas-port {canvasPort} --test-fixtures "{fixturesPath}" """
                repoRoot
                []
                false

        serverProcess.Value <- Some proc
        do! waitForUrl serverUrl 30000
    }

let compileFable () =
    task {
        let clientDir = Path.Combine("src", "Client")
        let outDir = Path.Combine(clientDir, "output")

        let proc =
            startProcess "dotnet" $"fable {clientDir} --outDir {outDir}" repoRoot [] true

        let! stdout = proc.StandardOutput.ReadToEndAsync()
        let! stderr = proc.StandardError.ReadToEndAsync()
        let exited = proc.WaitForExit(60_000)

        if not exited then
            proc.Kill(entireProcessTree = true)
            failwith "Fable compilation timed out after 60s"

        TestContext.Out.WriteLine($"Fable compilation output:{Environment.NewLine}{stdout}")

        if proc.ExitCode <> 0 then
            failwithf "Fable compilation failed (exit code %d): %s" proc.ExitCode stderr
    }

let startVite () =
    task {
        let proc =
            startProcess
                "npx"
                "vite --host"
                repoRoot
                [ "VITE_PORT", string vitePort
                  "API_PORT", string apiPort
                  "CANVAS_PORT", string canvasPort
                  "NODE_OPTIONS", "--max-old-space-size=512" ]
                false

        viteProcess.Value <- Some proc
        do! waitForUrl viteUrl 15000
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
