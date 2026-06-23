module Tests.TestUtils

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open NUnit.Framework

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

let withTempFile (prefix: string) (content: string) (action: string -> 'a) =
    let tempFile = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid()}.jsonl")
    try
        File.WriteAllText(tempFile, content)
        action tempFile
    finally
        if File.Exists(tempFile) then File.Delete(tempFile)

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
/// the orphan roots.json lookup) off the real ~/.treemon. `prefix` names the temp dir for debugging,
/// mirroring withTempFile. TREEMON_CONFIG_DIR is process-global, so callers must stay non-parallel.
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

let runAsync (a: Async<'T>) =
    Async.RunSynchronously(a, timeout = 30_000)

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
