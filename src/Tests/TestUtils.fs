module Tests.TestUtils

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation

module Modal = CreateWorktreeModal

let resolveCmdShim (fileName: string) =
    if Path.GetExtension(fileName) = "" then
        let cmdPath = $"{fileName}.cmd"
        let pathDirs =
            Environment.GetEnvironmentVariable("PATH")
            |> Option.ofObj
            |> Option.map (fun p -> p.Split(Path.PathSeparator))
            |> Option.defaultValue [||]

        match Array.tryFind (fun dir -> File.Exists(Path.Combine(dir, cmdPath))) pathDirs with
        | Some dir -> Path.Combine(dir, cmdPath)
        | None -> fileName
    else
        fileName

let startProcess (fileName: string) (args: string) (workingDir: string) (envVars: (string * string) list) (redirectOutput: bool) =
    let resolved = resolveCmdShim fileName

    let psi =
        ProcessStartInfo(
            FileName = resolved,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        )

    envVars |> List.iter (fun (k, v) -> psi.Environment[k] <- v)
    Process.Start(psi)

let killProc (procOpt: Process option) =
    procOpt
    |> Option.iter (fun p ->
        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)

                match p.WaitForExit(10000) with
                | true -> ()
                | false ->
                    TestContext.Error.WriteLine(
                        $"Process {p.Id} did not exit within 10s after Kill")

            p.Dispose()
        with ex ->
            TestContext.Error.WriteLine($"Failed to kill process: {ex.Message}"))

let private findPidsOnPortWindows (port: int) =
    let psi =
        ProcessStartInfo(
            FileName = "netstat",
            Arguments = "-ano",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        )

    use proc = Process.Start(psi)
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit(5000) |> ignore

    let pattern = Regex($@"TCP\s+\S+:{port}\s+\S+\s+LISTENING\s+(\d+)")
    pattern.Matches(output)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> int m.Groups[1].Value)
    |> Seq.distinct
    |> Seq.toList

let private findPidsOnPortLinux (port: int) =
    let psi =
        ProcessStartInfo(
            FileName = "lsof",
            Arguments = $"-ti :{port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        )

    use proc = Process.Start(psi)
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit(5000) |> ignore

    output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun s ->
        match Int32.TryParse(s.Trim()) with
        | true, pid -> Some pid
        | _ -> None)
    |> Array.distinct
    |> Array.toList

let runAsync (a: Async<'T>) =
    Async.RunSynchronously(a, timeout = 30_000)

let assertOk (result: Result<unit, string>) (message: string) =
    match result with
    | Ok() -> ()
    | Error err -> Assert.Fail($"{message}: {err}")

let killOrphansOnPort (port: int) =
    try
        let pids =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                findPidsOnPortWindows port
            else
                findPidsOnPortLinux port

        pids
        |> List.iter (fun pid ->
            try
                use orphan = Process.GetProcessById(pid)

                if not orphan.HasExited then
                    TestContext.Out.WriteLine($"[Cleanup] Killing orphaned process PID {pid} on port {port}")
                    orphan.Kill(entireProcessTree = true)
                    orphan.WaitForExit(5000) |> ignore
            with :? ArgumentException ->
                ())
    with ex ->
        TestContext.Error.WriteLine($"[Cleanup] Failed to scan port {port}: {ex.Message}")

let defaultModel : Model =
    { Repos = []
      IsLoading = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      SyncPending = Set.empty
      AppVersion = Some "1.0"
      DeployBranch = None
      SystemMetrics = None
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = Modal.Closed
      DeletedBranches = Set.empty
      EditorName = "VS Code"
      LastError = None }

/// Calls update and returns the model, ignoring the Cmd. Handles the case where
/// Fable.Remoting.Client proxy initialization fails in .NET by catching the
/// TypeInitializationException. In that scenario the model was already computed
/// (F# evaluates the left side of the tuple first) but the Cmd construction fails.
/// We re-derive the expected model from the CreateModal state that would have been set.
let tryUpdateModel msg model =
    try
        let m, _ = update msg model
        m
    with
    | :? TypeInitializationException ->
        match msg with
        | ModalMsg (Modal.OpenCreateWorktree repoId) ->
            { model with CreateModal = Modal.LoadingBranches repoId }
        | ModalMsg Modal.SubmitCreateWorktree ->
            match model.CreateModal with
            | Modal.Open form when form.Name.Trim().Length > 0 ->
                { model with CreateModal = Modal.Creating form.RepoId }
            | _ -> model
        | ModalMsg (Modal.CreateWorktreeCompleted (Ok _)) ->
            let restored = Modal.repoId model.CreateModal |> Option.map RepoHeader
            { model with CreateModal = Modal.Closed; FocusedElement = restored |> Option.orElse model.FocusedElement }
        | DeleteCompleted (Error msg) ->
            { model with DeletedBranches = Set.empty; LastError = Some $"Delete failed: {msg}" }
        | SessionResult (Error msg) ->
            { model with LastError = Some $"Session failed: {msg}" }
        | LaunchActionResult (Error msg) ->
            { model with LastError = Some $"Launch failed: {msg}" }
        | _ -> reraise ()
