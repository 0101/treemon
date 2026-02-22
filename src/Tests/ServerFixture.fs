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

let private worktreeRoots = [ @"Q:\code\AITestAgent"; repoRoot ]

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

let serverUrl = "http://localhost:5001"
let viteUrl = "http://localhost:5174"

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

let private killOrphansOnPort port =
    TestUtils.killOrphansOnPort port

let startServer () =
    task {
        let rootArgs = worktreeRoots |> List.map (fun r -> $"\"{r}\"") |> String.concat " "

        let proc =
            startProcess
                "dotnet"
                $"""run --project "{serverProjectPath}" -- {rootArgs} --port 5001 --test-fixtures "{fixturesPath}" """
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
                [ "VITE_PORT", "5174"
                  "API_PORT", "5001"
                  "NODE_OPTIONS", "--max-old-space-size=512" ]
                false

        viteProcess.Value <- Some proc
        do! waitForUrl viteUrl 15000
    }

let private killProc procOpt =
    TestUtils.killProc procOpt

let private formatBytes (bytes: int64) =
    sprintf "%.1f MB" (float bytes / (1024.0 * 1024.0))

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
            killOrphansOnPort 5001
            killOrphansOnPort 5174
            do! startServer ()
            do! compileFable ()
            do! startVite ()
            TestContext.Out.WriteLine("Server, Fable, and Vite started successfully")
        }

    [<OneTimeTearDown>]
    member _.TearDown() = stopAll ()
