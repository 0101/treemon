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

let private worktreeRoot = @"Q:\code\AITestAgent"

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

let serverUrl = "http://localhost:5001"
let viteUrl = "http://localhost:5174"

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

let private resolveCmdShim (fileName: string) =
    match Path.GetExtension(fileName) with
    | "" ->
        let cmdPath = sprintf "%s.cmd" fileName

        match
            (Environment.GetEnvironmentVariable("PATH") |> Option.ofObj)
            |> Option.map (fun p -> p.Split(Path.PathSeparator))
            |> Option.defaultValue [||]
            |> Array.tryFind (fun dir ->
                File.Exists(Path.Combine(dir, cmdPath)))
        with
        | Some dir -> Path.Combine(dir, cmdPath)
        | None -> fileName
    | _ -> fileName

let private startProcess (fileName: string) (args: string) (workingDir: string) (envVars: (string * string) list) =
    let resolved = resolveCmdShim fileName

    let psi =
        ProcessStartInfo(
            FileName = resolved,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    envVars |> List.iter (fun (k, v) -> psi.Environment.[k] <- v)
    Process.Start(psi)

let startServer () =
    task {
        let proc =
            startProcess
                "dotnet"
                (sprintf "run --project \"%s\" -- \"%s\" --port 5001" serverProjectPath worktreeRoot)
                repoRoot
                []

        serverProcess.Value <- Some proc
        do! waitForUrl serverUrl 30000
    }

let startVite () =
    task {
        let proc =
            startProcess "npx" "vite --host" repoRoot [ "VITE_PORT", "5174"; "API_PORT", "5001" ]

        viteProcess.Value <- Some proc
        do! waitForUrl viteUrl 15000
    }

let private killProc (procOpt: Process option) =
    procOpt
    |> Option.iter (fun p ->
        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)

                match p.WaitForExit(10000) with
                | true -> ()
                | false ->
                    TestContext.Error.WriteLine(
                        sprintf "Process %d did not exit within 10s after Kill" p.Id)

            p.Dispose()
        with ex ->
            TestContext.Error.WriteLine(sprintf "Failed to kill process: %s" ex.Message))

let stopAll () =
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
            do! startVite ()
            TestContext.Out.WriteLine("Server and Vite started successfully")
        }

    [<OneTimeTearDown>]
    member _.TearDown() = stopAll ()
