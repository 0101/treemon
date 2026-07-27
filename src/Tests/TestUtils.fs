module Tests.TestUtils

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server.SessionActivity
open Server.SessionManager

/// Parse an ISO-8601 timestamp string as a DateTimeOffset using the invariant culture. Shared by the
/// SessionActivity domain/store/service tests, which all build fixtures from literal timestamps.
let ts (s: string) : DateTimeOffset = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture)

/// Build a push-model `Message` (domain record) from body text and an ISO-8601 timestamp string.
let msg (text: string) (t: string) : Message = { Text = text; At = ts t }

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

/// Run `action` with the process CWD swapped to a throwaway temp directory, then
/// restore and delete it. Tests that persist relative to the current directory
/// (e.g. CanvasDocOwnership.attribute writes data/canvas-owners.json under CWD) use
/// this so they never touch the real data file. CWD is process-global, so callers
/// must stay non-parallel (the canvas fixtures are [<NonParallelizable>]).
let withTempCwd (action: unit -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"treemon-cwd-test-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    let original = Environment.CurrentDirectory
    Environment.CurrentDirectory <- tempDir

    try
        action ()
    finally
        Environment.CurrentDirectory <- original
        try Directory.Delete(tempDir, recursive = true) with _ -> ()

/// Run `action` with the machine-level Treemon config dir redirected to a throwaway temp dir via
/// the TREEMON_CONFIG_DIR override, then restore the previous value and delete the dir. Required,
/// not merely convenient: on Windows Environment.GetFolderPath(UserProfile) ignores USERPROFILE/HOME,
/// so the override is the only way to keep in-process config tests (the global read/write helpers and
/// the orphan roots.json lookup) off the real ~/.treemon. `prefix` names the temp dir for debugging.
/// TREEMON_CONFIG_DIR is process-global, so callers must stay non-parallel.
let withTempConfigDir (prefix: string) (action: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid()}")
    Directory.CreateDirectory(tempDir) |> ignore
    let original = Environment.GetEnvironmentVariable("TREEMON_CONFIG_DIR")
    Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", tempDir)

    try
        action tempDir
    finally
        Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", original)
        try Directory.Delete(tempDir, recursive = true) with _ -> ()

let runAsyncWithTimeout timeoutMs (a: Async<'T>) =
    Async.RunSynchronously(a, timeout = timeoutMs)

let runAsync (a: Async<'T>) =
    runAsyncWithTimeout 30_000 a

let private queryPowerShellProcesses () =
    let script =
        "Get-CimInstance Win32_Process -Filter \"Name = 'pwsh.exe'\""
        + " | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }"

    let psi =
        ProcessStartInfo(
            "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    [ "-NoProfile"; "-NonInteractive"; "-Command"; script ]
    |> List.iter psi.ArgumentList.Add

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()

    if not (proc.WaitForExit(10_000)) then
        proc.Kill(entireProcessTree = true)
        Error "Timed out while enumerating PowerShell processes"
    elif proc.ExitCode <> 0 then
        Error $"Failed to enumerate PowerShell processes: {stderr.Trim()}"
    else
        stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            match line.Split('\t', 2) with
            | [| pid; commandLine |] ->
                match Int32.TryParse(pid) with
                | true, value -> Some(value, commandLine)
                | false, _ -> None
            | _ -> None)
        |> Array.toList
        |> Ok

let internal tryOwnedPowerShellPid (worktreePath: string) (pid: int, commandLine: string) =
    let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)
    let expectedScriptPrefix = buildScript nativePath None
    let encodedCommand = Regex(@"(?i)-EncodedCommand\s+([^\s]+)")
    let matched = encodedCommand.Match(commandLine)

    if not matched.Success then
        None
    else
        try
            let decoded =
                matched.Groups[1].Value
                |> Convert.FromBase64String
                |> Text.Encoding.Unicode.GetString

            if decoded.StartsWith(expectedScriptPrefix, StringComparison.OrdinalIgnoreCase) then
                Some pid
            else
                None
        with :? FormatException ->
            None

let private ownedPowerShellPids (worktreePath: string) =
    queryPowerShellProcesses ()
    |> Result.map (List.choose (tryOwnedPowerShellPid worktreePath))

let private stopOwnedPowerShellProcesses worktreePath =
    let stopPid pid =
        try
            use proc = Process.GetProcessById(pid)

            if not proc.HasExited then
                proc.Kill(entireProcessTree = true)

            Ok()
        with
        | :? ArgumentException -> Ok()
        | ex -> Error $"PID {pid}: {ex.Message}"

    let rec waitForExit remaining =
        async {
            match ownedPowerShellPids worktreePath with
            | Error message -> return Error message
            | Ok [] -> return Ok()
            | Ok pids when remaining = 0 ->
                let pidList = pids |> List.map string |> String.concat ", "
                return Error $"Fixture PowerShell processes did not exit: {pidList}"
            | Ok _ ->
                do! Async.Sleep 100
                return! waitForExit (remaining - 1)
        }

    async {
        match ownedPowerShellPids worktreePath with
        | Error message -> return Error message
        | Ok pids ->
            let failures =
                pids
                |> List.map stopPid
                |> List.choose (function
                    | Ok () -> None
                    | Error message -> Some message)

            match failures with
            | _ :: _ -> return Error(String.concat Environment.NewLine failures)
            | [] -> return! waitForExit 50
    }

let private requestSessionClosure (agent: SessionAgent) =
    async {
        let! sessions = getActiveSessions agent

        return!
            sessions
            |> Map.toList
            |> List.map (fun (path, _) ->
                async {
                    try
                        return! killSession agent (WorktreePath path)
                    with ex ->
                        return Error ex.Message
                })
            |> Async.Sequential
            |> Async.Ignore
    }

/// Close every session tracked by an isolated test agent. WM_CLOSE is the primary path; if Windows
/// Terminal refuses it, force-stop only pwsh processes whose decoded launch script starts in the
/// fixture's unique worktree. WindowsTerminal.exe is a shared host and is never a valid kill target.
let closeTrackedTestSessions (agent: SessionAgent) (worktreePath: string) =
    async {
        do! requestSessionClosure agent
        let! fallback = stopOwnedPowerShellProcesses worktreePath
        do! requestSessionClosure agent
        let! remainingSessions = getActiveSessions agent
        let remainingProcesses = ownedPowerShellPids worktreePath

        return
            match fallback, remainingProcesses, remainingSessions.IsEmpty with
            | Ok (), Ok [], true -> Ok()
            | Error message, _, _ -> Error message
            | _, Error message, _ -> Error message
            | _, Ok pids, false ->
                let paths =
                    remainingSessions
                    |> Map.toList
                    |> List.map fst
                    |> String.concat ", "

                let pidList = pids |> List.map string |> String.concat ", "
                Error $"Tracked sessions remain ({paths}); PowerShell PIDs: {pidList}"
            | _, Ok pids, true ->
                let pidList = pids |> List.map string |> String.concat ", "
                Error $"Fixture PowerShell processes remain: {pidList}"
    }

let cleanupTerminalTestEnvironment
    (agent: SessionAgent option)
    (originalCwd: string)
    (tempRoot: string)
    (worktreePath: string)
    =
    let sessionCleanup =
        match agent with
        | Some activeAgent ->
            try
                closeTrackedTestSessions activeAgent worktreePath
                |> runAsyncWithTimeout 60_000
            with ex ->
                Error ex.Message
        | None -> Ok()

    Environment.CurrentDirectory <- originalCwd

    let directoryCleanup =
        try
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, recursive = true)

            Ok()
        with ex ->
            Error ex.Message

    match sessionCleanup, directoryCleanup with
    | Ok (), Ok () -> Ok()
    | Error sessionError, Ok () -> Error $"Session cleanup failed: {sessionError}"
    | Ok (), Error directoryError -> Error $"Fixture cleanup failed: {directoryError}"
    | Error sessionError, Error directoryError ->
        Error $"Session cleanup failed: {sessionError}{Environment.NewLine}Fixture cleanup failed: {directoryError}"

/// Asserts a `Result<unit, string>` is `Ok`, prefixing `message` to the surfaced error on failure.
/// Prefer this over `Is.EqualTo(Ok())`: the literal's error type infers as `obj`, so NUnit's
/// structural compare never matches the actual `Result<unit, string>` even when both are `Ok ()`.
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

/// Reserve `count` distinct free loopback TCP ports by briefly binding ephemeral sockets
/// (port 0 lets the OS assign a free port). All listeners are held open at once so the ports
/// returned by a single call are guaranteed distinct from each other, then released for the
/// caller to bind. Distinctness is only guaranteed within one call, so fixtures that each
/// reserve ports must not run in parallel (the smoke fixtures are [<NonParallelizable>]).
/// Use this instead of hardcoded ports so test servers never collide with a running production
/// instance — and never need to free a port by killing another process.
let getFreeTcpPorts (count: int) : int list =
    let listeners =
        List.init count (fun _ ->
            let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
            listener.Start()
            listener)

    let ports =
        listeners |> List.map (fun l -> (l.LocalEndpoint :?> Net.IPEndPoint).Port)

    listeners |> List.iter (fun l -> l.Stop())
    ports

let getFreeTcpPort () = getFreeTcpPorts 1 |> List.head

let private tryGet (client: HttpClient) (url: string) =
    async {
        try
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return int response.StatusCode < 500
        with _ ->
            return false
    }

let rec private pollUntilReady (client: HttpClient) (url: string) (deadline: DateTime) =
    async {
        if DateTime.UtcNow > deadline then
            failwith $"Timed out waiting for {url}"
        else
            let! ok = tryGet client url
            if not ok then
                do! Async.Sleep(500)
                return! pollUntilReady client url deadline
    }

/// Poll `url` until it answers (HTTP status < 500) or `timeoutMs` elapses, failing on timeout.
/// Shared by the E2E fixtures that boot their own server+vite on isolated ports.
let waitForUrl (url: string) (timeoutMs: int) : Task =
    async {
        use client = new HttpClient()
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        do! pollUntilReady client url deadline
    }
    |> Async.StartAsTask
    :> Task

/// Launch the Treemon API server process for an E2E fixture. `rootArgs` is the already-quoted,
/// space-joined worktree-root list; each fixture keeps its own port / orphan-kill / fixture policy
/// but shares this launch command.
let startServerProcess (serverProjectPath: string) (repoRoot: string) (rootArgs: string) (port: int) (canvasPort: int) (fixturePath: string) : Process =
    startProcess
        "dotnet"
        $"""run --project "{serverProjectPath}" -- {rootArgs} --port {port} --canvas-port {canvasPort} --test-fixtures "{fixturePath}" """
        repoRoot
        []
        false

/// Launch a Vite dev-server process wired to the given API/canvas ports for an E2E fixture.
let startViteProcess (repoRoot: string) (vitePort: int) (apiPort: int) (canvasPort: int) : Process =
    startProcess
        "npx"
        "vite --host"
        repoRoot
        [ "VITE_PORT", string vitePort
          "API_PORT", string apiPort
          "CANVAS_PORT", string canvasPort
          "NODE_OPTIONS", "--max-old-space-size=512" ]
        false
