// Cross-platform Treemon lifecycle. Run it through the treemon.sh / treemon.ps1 wrappers, or
// directly as `dotnet fsi treemon.fsx <command>`.
//
// `deploy` still lives in treemon.ps1: its TerminalHost staged-replacement preflight is not ported
// yet. Everything else lives here so both platforms share one implementation.

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.NetworkInformation
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open System.Threading

let scriptDir = __SOURCE_DIRECTORY__
let pidFile = Path.Combine(scriptDir, ".treemon.pid")
let logDir = Path.Combine(scriptDir, "logs")
let publishDir = Path.Combine(scriptDir, ".publish")
let wwwRoot = Path.Combine(scriptDir, "wwwroot")
let distDir = Path.Combine(scriptDir, "dist")

let isWindows = OperatingSystem.IsWindows()

let private portFromEnvironment name fallback =
    match Environment.GetEnvironmentVariable(name: string) with
    | null | "" -> fallback
    | value ->
        match Int32.TryParse value with
        | true, port -> port
        | _ -> fallback

let defaultPort = portFromEnvironment "TREEMON_PORT" 5000

/// The server binds a second Kestrel host for canvas documents, and failing to bind it is fatal to
/// the whole process rather than a degraded canvas. It defaults to Program.fs's `defaultCanvasPort`,
/// and is passed explicitly so that moving TREEMON_PORT is enough to run a second instance: leaving
/// it implicit means every instance fights over 5002 and the loser dies at startup.
let canvasPort = portFromEnvironment "TREEMON_CANVAS_PORT" 5002

let serverExecutable =
    Path.Combine(publishDir, if isWindows then "Treemon.exe" else "Treemon")

module Out =
    let private write (color: ConsoleColor) (text: string) =
        let previous = System.Console.ForegroundColor
        System.Console.ForegroundColor <- color
        System.Console.Out.WriteLine text
        System.Console.ForegroundColor <- previous

    let info text = write ConsoleColor.Cyan text
    let good text = write ConsoleColor.Green text
    let warn text = write ConsoleColor.Yellow text
    let bad text = write ConsoleColor.Red text
    let plain (text: string) = System.Console.Out.WriteLine text

module Exec =
    /// npm ships its executables as `.cmd` shims on Windows, which Process.Start cannot exec without
    /// the extension.
    let resolve (name: string) =
        if isWindows && Path.GetExtension name = "" then
            let shim = $"{name}.cmd"

            Environment.GetEnvironmentVariable "PATH"
            |> Option.ofObj
            |> Option.map (fun path -> path.Split Path.PathSeparator)
            |> Option.defaultValue [||]
            |> Array.tryFind (fun directory -> File.Exists(Path.Combine(directory, shim)))
            |> Option.map (fun directory -> Path.Combine(directory, shim))
            |> Option.defaultValue name
        else
            name

    let startInfo (name: string) (arguments: string list) =
        let info =
            ProcessStartInfo(
                FileName = resolve name,
                WorkingDirectory = scriptDir,
                UseShellExecute = false)

        arguments |> List.iter info.ArgumentList.Add
        info

    /// Runs a command attached to this console and returns its exit code.
    let run name arguments =
        use child = Process.Start(startInfo name arguments)
        child.WaitForExit()
        child.ExitCode

    let runOrFail name arguments =
        match run name arguments with
        | 0 -> ()
        | code -> failwith $"""{name} {String.concat " " arguments} failed with exit code {code}"""

    let onPath (name: string) =
        let candidates =
            if isWindows then [ $"{name}.exe"; $"{name}.cmd"; name ] else [ name ]

        Environment.GetEnvironmentVariable "PATH"
        |> Option.ofObj
        |> Option.map (fun path -> path.Split Path.PathSeparator)
        |> Option.defaultValue [||]
        |> Seq.collect (fun directory -> candidates |> List.map (fun candidate -> Path.Combine(directory, candidate)))
        |> Seq.tryFind File.Exists

    /// Runs a command and returns its exit code together with everything it printed.
    let capture name arguments =
        let info = startInfo name arguments
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        use child = Process.Start info
        let output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, output.Trim()

module Ports =
    let private listening () =
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        |> Array.map _.Port
        |> Set.ofArray

    let isFree port = listening () |> Set.contains port |> not

    let waitUntilFree port (timeout: TimeSpan) =
        let deadline = DateTimeOffset.UtcNow + timeout

        let rec wait () =
            if isFree port then true
            elif DateTimeOffset.UtcNow >= deadline then false
            else
                Thread.Sleep 250
                wait ()

        wait ()

module Ttyd =
    let private version = "1.7.7"

    /// Where the runtime comes from. Upstream publishes Windows and Linux builds only, so macOS has
    /// to adopt whatever ttyd the machine already has - Homebrew's, normally - which cannot be
    /// checksum-pinned the way a release asset can.
    type private Source =
        | Download of asset: string * sha256: string * executableName: string
        | AdoptInstalled of executableName: string

    /// Checksums pinned from https://github.com/tsl0922/ttyd/releases/tag/1.7.7. The Windows build is
    /// 32-bit and runs on every Windows architecture, so only Linux varies by CPU.
    let private source () =
        if isWindows then
            Ok(Download("ttyd.win32.exe", "e33a27501b10b96981335bcba938b1145c7f52551a343e72160f00ab71832b37", "ttyd.exe"))
        elif OperatingSystem.IsMacOS() then
            Ok(AdoptInstalled "ttyd")
        elif not (OperatingSystem.IsLinux()) then
            // Selecting a build by architecture alone would hand a BSD host a Linux ELF and call it
            // installed.
            Error $"ttyd {version} publishes no build for this OS, so embedded terminals are not supported here."
        else
            match RuntimeInformation.OSArchitecture with
            | Architecture.X64 ->
                Ok(Download("ttyd.x86_64", "8a217c968aba172e0dbf3f34447218dc015bc4d5e59bf51db2f2cd12b7be4f55", "ttyd"))
            | Architecture.Arm64 ->
                Ok(Download("ttyd.aarch64", "b38acadd89d1d396a0f5649aa52c539edbad07f4bc7348b27b4f4b7219dd4165", "ttyd"))
            | other ->
                Error $"No ttyd {version} artifact is pinned for Linux {other}. Embedded terminals need x64 or arm64."

    let private sha256 path =
        use stream = File.OpenRead(path: string)
        use hash = SHA256.Create()
        Convert.ToHexString(hash.ComputeHash stream).ToLowerInvariant()

    let private markExecutable (path: string) =
        if not isWindows then
            // A release asset arrives without the execute bit, and ttyd is spawned directly rather
            // than through a shell, so without this every terminal launch fails with EACCES.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

    let private installDirectory = Path.Combine(scriptDir, ".tools", "ttyd", version)
    let private licenseSource = Path.Combine(scriptDir, "scripts", "third-party", "ttyd-LICENSE.txt")

    let private copyLicense () =
        File.Copy(licenseSource, Path.Combine(installDirectory, "LICENSE.txt"), overwrite = true)

    let private download assetName expectedSha256 (executablePath: string) =
        if File.Exists executablePath && sha256 executablePath = expectedSha256 then
            markExecutable executablePath
            copyLicense ()
            Out.good $"ttyd {version} is already installed at {executablePath}"
            0
        else
            let uri = $"https://github.com/tsl0922/ttyd/releases/download/{version}/{assetName}"
            let temporaryPath = Path.Combine(installDirectory, $"{assetName}.download")
            Out.plain $"Downloading ttyd {version} from {uri}"

            try
                use client = new HttpClient()

                do
                    use response = client.GetStreamAsync(uri) |> Async.AwaitTask |> Async.RunSynchronously
                    use file = File.Create temporaryPath
                    response.CopyTo file

                let actual = sha256 temporaryPath

                if actual <> expectedSha256 then
                    failwith
                        $"Downloaded ttyd checksum mismatch. Expected {expectedSha256} but received {actual}. Verify the {version} release at https://github.com/tsl0922/ttyd/releases/tag/{version}."

                File.Move(temporaryPath, executablePath, overwrite = true)
                markExecutable executablePath
                copyLicense ()
                Out.good $"Installed ttyd {version} at {executablePath}"
                0
            with error ->
                if File.Exists temporaryPath then
                    File.Delete temporaryPath

                Out.bad $"Unable to install ttyd {version}. Check access to GitHub, then retry. {error.Message}"
                1

    /// An adopted binary is whatever the package manager installed, so it is copied in rather than
    /// checksum-matched, and its version is only reported: the proxy speaks ttyd's protocol, and a
    /// build far from the pinned one is the first thing to suspect if terminals misbehave.
    let private adopt (executablePath: string) =
        match Exec.onPath "ttyd" with
        | None ->
            Out.bad $"ttyd was not found on PATH. Install it first (Homebrew: brew install ttyd), then rerun setup-ttyd."
            1
        | Some installed ->
            let reported =
                match Exec.capture installed [ "--version" ] with
                | 0, output when output <> "" -> output
                | _ -> "unknown version"

            // Copying a file onto itself throws, which is reachable once the install directory is on
            // PATH - exactly what happens when setup-ttyd is run a second time.
            if Path.GetFullPath installed <> Path.GetFullPath executablePath then
                File.Copy(installed, executablePath, overwrite = true)

            markExecutable executablePath
            copyLicense ()
            Out.good $"Adopted {installed} ({reported}) into {executablePath}"

            if not (reported.Contains version) then
                Out.warn $"  Treemon pins ttyd {version}; this build reports '{reported}'."

            0

    let install () =
        match source () with
        | Error message ->
            Out.bad message
            1
        | Ok chosen ->
            Directory.CreateDirectory installDirectory |> ignore

            match chosen with
            | Download(assetName, expectedSha256, executableName) ->
                download assetName expectedSha256 (Path.Combine(installDirectory, executableName))
            | AdoptInstalled executableName ->
                adopt (Path.Combine(installDirectory, executableName))

/// The pre-global-config file the PowerShell lifecycle used to own. The server migrates `roots.json`
/// but not this, so an existing Windows install that switches to these commands would otherwise come
/// up watching nothing.
module LegacyConfig =
    let private path = Path.Combine(scriptDir, ".treemon.config")

    /// `Some roots` when the file parsed - possibly to no roots at all, which is different from a
    /// file that could not be read and must therefore be left alone rather than deleted.
    let read () =
        if not (File.Exists path) then
            Some []
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                let stringsOf (name: string) =
                    match root.TryGetProperty name with
                    | true, element when element.ValueKind = JsonValueKind.Array ->
                        element.EnumerateArray()
                        |> Seq.choose (fun item -> item.GetString() |> Option.ofObj)
                        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                        |> Seq.toList
                        |> Some
                    | true, element when element.ValueKind = JsonValueKind.String ->
                        element.GetString()
                        |> Option.ofObj
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.map List.singleton
                    | _ -> None

                // Versions before multi-repo wrote the singular key.
                match stringsOf "WorktreeRoots" with
                | Some roots -> Some roots
                | None -> stringsOf "WorktreeRoot" |> Option.orElse (Some [])
            with
            | :? JsonException
            | :? IOException
            | :? UnauthorizedAccessException -> None

    /// Retired only once the server is up - it has persisted the roots by then - and only when every
    /// root it declared was actually handed over. A file that could not be parsed, or one whose roots
    /// this run ignored, is kept so nothing is silently lost.
    let retireIfFullyMigrated (migrated: string list) =
        if File.Exists path then
            match read () with
            | None -> Out.warn "Warning: .treemon.config could not be parsed; leaving it in place to avoid data loss."
            | Some declared ->
                let normalise (value: string) =
                    value.TrimEnd('\\', '/').ToLowerInvariant()

                let handedOver = migrated |> List.map normalise |> Set.ofList
                let missed = declared |> List.filter (fun root -> not (handedOver.Contains(normalise root)))

                if List.isEmpty missed then
                    try
                        File.Delete path
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> ()
                else
                    Out.warn
                        $"""Warning: .treemon.config still declares roots this run did not migrate ({String.concat ", " missed}); leaving it in place."""

module Server =
    let private aliveProcess processId =
        try
            let child = Process.GetProcessById(processId: int)

            if child.HasExited then
                child.Dispose()
                None
            else
                Some child
        with
        | :? ArgumentException
        | :? InvalidOperationException -> None

    let private pathComparison =
        if isWindows then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    /// Linux reports a running process whose executable has been replaced as "<path> (deleted)", and
    /// that is what MainModule.FileName hands back. `publish` replaces the binary under the running
    /// server every single time, so without trimming this the deploy path defeats itself: publishing
    /// is precisely what stops `stop`, `restart` and `status` from recognising the server they are
    /// about to act on. They then report it as not running, and `start` launches into a port the old
    /// server still holds - where it dies, while the bound port reads as a successful startup.
    let private deletedMarker = " (deleted)"

    let private executableOf (mainModule: ProcessModule) =
        let name = mainModule.FileName

        if name.EndsWith(deletedMarker, StringComparison.Ordinal) then
            name.Substring(0, name.Length - deletedMarker.Length)
        else
            name

    /// Whether a process is this checkout's server rather than whatever inherited its pid. The pid
    /// file outlives crashes and reboots, and `stop` kills what it names, so the number alone is not
    /// enough to act on.
    let internal isServerProcess (candidate: Process) =
        try
            candidate.MainModule
            |> Option.ofObj
            |> Option.exists (fun mainModule ->
                String.Equals(
                    Path.GetFullPath(executableOf mainModule),
                    Path.GetFullPath serverExecutable,
                    pathComparison))
        with
        | :? InvalidOperationException
        | :? System.ComponentModel.Win32Exception
        | :? NotSupportedException -> false

    /// treemon.ps1 records the server itself, and `stop` has to mean the same thing in both scripts.
    /// On Windows the launcher is a cmd wrapper whose pid is not the server's, so the server is found
    /// by its executable instead of taken from Process.Start.
    /// The recorded pid, only when the process wearing it is still this checkout's server. The file
    /// outlives crashes and reboots, so "alive" alone is not evidence: a reused pid would otherwise
    /// make `start` refuse to run and `status` report a stranger as Treemon.
    let runningPid () =
        if not (File.Exists pidFile) then
            None
        else
            match Int32.TryParse((File.ReadAllText pidFile).Trim()) with
            | true, processId ->
                match aliveProcess processId with
                | Some child ->
                    use child = child
                    if isServerProcess child then Some processId else None
                | None -> None
            | _ -> None

    let private resolveServerProcess () =
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension serverExecutable)
        |> Array.fold
            (fun found candidate ->
                match found with
                | Some _ ->
                    candidate.Dispose()
                    found
                | None when isServerProcess candidate -> Some candidate
                | None ->
                    candidate.Dispose()
                    None)
            None

    /// The recorded pid is the fast path, not the truth. The file is written by `start`, so it does
    /// not survive a machine restart, a crash, or a server launched some other way - and next to a
    /// stale file there can still be a perfectly live server of this checkout holding the port.
    ///
    /// Both callers were wrong without this, in the same incident. `start` refuses when a server is
    /// already running but consulted only the file, so against a stale one it launched a second
    /// server that died on the bound port - while `waitForBinding` saw the *old* server's port and
    /// reported "Treemon is running". `stop` cleared the file and said nothing was running, so
    /// `restart` silently left the old build serving.
    ///
    /// resolveServerProcess matches on this checkout's executable path, so the fallback stays as
    /// per-checkout as the pid file it backs up.
    let livePid () =
        match runningPid () with
        | Some existing -> Some existing
        | None ->
            match resolveServerProcess () with
            | Some server ->
                use server = server
                Some server.Id
            | None -> None

    /// A production server started from inside a Treemon embedded terminal inherits that terminal's
    /// shutdown boundary - on Windows its Job Object - so closing the tab kills production. The
    /// PowerShell lifecycle refused these commands for that reason, and this has to as well.
    let internal startedFromEmbeddedTerminal () =
        Environment.GetEnvironmentVariable "TREEMON_TERMINAL_SESSION_ID"
        |> String.IsNullOrWhiteSpace
        |> not

    let refuseFromEmbeddedTerminal (action: string) =
        if startedFromEmbeddedTerminal () then
            failwith
                $"Cannot {action} production from a Treemon embedded terminal, because the server would inherit that terminal's shutdown boundary and die when the tab closes. Run this from an external terminal."

    let private runLogs () =
        if not (Directory.Exists logDir) then
            [||]
        else
            Directory.GetFiles(logDir, "treemon-prod.*.log")
            |> Array.sortByDescending File.GetLastWriteTimeUtc

    /// Every run writes its own log, so without pruning `logs/` grows for as long as the server is
    /// ever restarted. Best effort: a log still held open simply survives to the next attempt.
    let private pruneOldRunLogs keep =
        runLogs ()
        |> Array.skip (min keep (runLogs ()).Length)
        |> Array.iter (fun path ->
            try
                File.Delete path
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ())

    let currentLogFile () =
        if not (Directory.Exists logDir) then
            None
        else
            Directory.GetFiles(logDir, "treemon-prod.*.log")
            |> Array.sortByDescending File.GetLastWriteTimeUtc
            |> Array.tryHead

    /// The child has to keep writing its log after this script exits, and .NET cannot hand a file
    /// handle to a child the way PowerShell's -RedirectStandardOutput does: redirecting through pipes
    /// here would block the server as soon as nobody drains them. So the redirection is delegated to
    /// the platform shell. On Windows that shell is a cmd wrapper whose pid is not the server's, so
    /// the pid to record is resolved separately rather than taken from here.
    /// Everything here is interpolated into a shell command line, and the roots arrive straight off
    /// this script's own command line, so it is escaped for that shell rather than merely wrapped in
    /// quotes: inside POSIX double quotes $, ` and \ are all still live.
    let private escapedForShell (value: string) =
        if isWindows then
            // Inside cmd's double quotes & | < > ^ are already literal, but %VAR% still expands with
            // no escape available, and a quote cannot be escaped at all. Neither character is legal
            // in a Windows path, so refusing them is honest where escaping is impossible.
            if value.Contains '%' || value.Contains '"' then
                failwith $"'{value}' contains a quote or %%, which cmd cannot be made to treat literally."

            "\"" + value + "\""
        else
            "'" + value.Replace("'", @"'\''") + "'"

    let private launch (arguments: string list) (logPath: string) =
        let commandLine =
            (serverExecutable :: arguments |> List.map escapedForShell |> String.concat " ")
            + " > " + escapedForShell logPath + " 2>&1"

        let info =
            if isWindows then
                // ShellExecute rather than CreateProcess, because CreateProcess hands the child every
                // inheritable handle this script holds. The server would keep a copy of our stdout
                // open for its whole life even though the shell has pointed its own output at the log,
                // which leaves whoever invoked us waiting on a pipe that never closes.
                // cmd also strips the outer quote pair when /c's argument starts with one, so the
                // command line needs a pair of its own or the quoted executable path is eaten.
                ProcessStartInfo(
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + commandLine + "\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden)
            else
                // The server has to outlive the shell that started it: without this, one started
                // over `wsl.exe <command>` dies with that session. setsid gives it a session of its
                // own, but that is util-linux and macOS does not ship it, so nohup is the fallback -
                // weaker, since it only ignores SIGHUP, but enough to survive the launching shell.
                // Both exec in place, as does sh here, so the recorded pid stays the server's own and
                // nothing is left holding an inherited descriptor.
                let detach =
                    match Exec.onPath "setsid" with
                    | Some _ -> "setsid "
                    | None -> "nohup "

                let info = ProcessStartInfo(FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true)
                info.ArgumentList.Add "-c"
                info.ArgumentList.Add("exec " + detach + commandLine)
                info

        info.WorkingDirectory <- scriptDir
        Process.Start info

    /// `C:\` must survive: trimming it to `C:` leaves a drive-relative reference to the current
    /// directory rather than the drive root.
    let private trimmedRoot (root: string) =
        let trimmed = root.TrimEnd('\\', '/')

        if trimmed.EndsWith ':' then root else trimmed

    /// The startup path proper, once it is known that nothing is already running.
    let private startFresh (roots: string list) =
        Directory.CreateDirectory logDir |> ignore
        let stamp = DateTime.Now.ToString "yyyyMMdd-HHmmss"
        let logPath = Path.Combine(logDir, $"treemon-prod.{stamp}.log")

        if not (Ports.waitUntilFree canvasPort (TimeSpan.FromSeconds 10.0)) then
            Out.warn $"Warning: port {canvasPort} is still in use after 10s, and the server exits if it cannot bind it."
            Out.warn $"  Set TREEMON_CANVAS_PORT to move the canvas doc server off {canvasPort}."

        // Launching into an occupied port is what produced a "Treemon is running" for a server that
        // had already died: the new one exits on the bind, and waitForBinding cannot tell its port
        // from the one whatever else holds it is serving. Refusing says so instead of guessing.
        if not (Ports.waitUntilFree defaultPort (TimeSpan.FromSeconds 10.0)) then
            failwith
                $"Port {defaultPort} is still in use after 10s, so nothing was started: a server that cannot bind its port exits at once, and the port staying bound would make that look like success. Stop whatever holds it first."

        // An explicit path wins; otherwise the legacy file's roots are carried over so an install
        // that predates the global config does not come up watching nothing.
        let effectiveRoots =
            match roots with
            | [] -> LegacyConfig.read () |> Option.defaultValue []
            | given -> given

        if List.isEmpty roots && not (List.isEmpty effectiveRoots) then
            Out.plain "Migrating worktree roots from .treemon.config into the global config."

        let arguments =
            (effectiveRoots |> List.map trimmedRoot)
            @ [ "--port"; string defaultPort; "--canvas-port"; string canvasPort ]

        Out.info $"Starting production server on port {defaultPort}..."
        use launcher = launch arguments logPath

        // Waiting a fixed moment and declaring failure left a server that was merely slow running
        // untracked, with nothing recording its pid and `stop` unable to find it. Poll instead, and
        // give up only once the launcher itself has died or the budget is spent.
        let readinessBudget = TimeSpan.FromSeconds 30.0
        let deadline = DateTimeOffset.UtcNow + readinessBudget

        let rec waitForBinding () =
            if not (Ports.isFree defaultPort) then true
            elif launcher.HasExited then false
            elif DateTimeOffset.UtcNow >= deadline then false
            else
                Thread.Sleep 250
                waitForBinding ()

        if not (waitForBinding ()) then
            // Whatever was launched must not be left behind: it could still bind after this returns,
            // and by then nothing would know its pid.
            (try
                if not launcher.HasExited then
                    launcher.Kill true
                    launcher.WaitForExit 5_000 |> ignore
             with
             | :? InvalidOperationException
             | :? System.ComponentModel.Win32Exception
             | :? NotSupportedException -> ())

            Out.bad $"Server did not bind port {defaultPort} within {int readinessBudget.TotalSeconds}s and was stopped. Log: {logPath}"
            1
        else
            match resolveServerProcess () with
            | Some server ->
                use server = server
                File.WriteAllText(pidFile, string server.Id)
                pruneOldRunLogs 10
                LegacyConfig.retireIfFullyMigrated effectiveRoots
                Out.good $"Treemon is running on http://localhost:{defaultPort}"
                Out.plain $"Log: {logPath}"
                0
            | None ->
                // Recording the launcher instead would make `stop` kill the wrong thing, and on
                // Windows that pid is a cmd wrapper rather than the server at all.
                Out.bad $"Port {defaultPort} is bound but no '{serverExecutable}' process could be identified, so nothing was recorded to stop later. Log: {logPath}"
                ignore launcher.Id
                1

    let start (roots: string list) =
        if not (File.Exists serverExecutable) then
            failwith $"No published server at '{serverExecutable}'. Run 'publish' first."

        match livePid () with
        | Some existing ->
            // Without this the launch still "succeeds": the old server keeps the port bound, so the
            // post-start check passes while the process just started has already died on the port.
            // Deliberately no URL: the pid file records the process, not the port it was given, so
            // the port asked for now may not be the one it is serving. It is also per checkout, which
            // is why a second instance needs a second checkout rather than only its own ports.
            Out.warn $"Treemon is already running (PID {existing}); this checkout tracks one server."
            Out.plain "Use 'stop' first, or 'restart'."
            1
        | None ->
            startFresh roots

    let stop () =
        // livePid is the single gate: it refuses a pid the server no longer owns, so a reused pid
        // arrives here as None and is cleared rather than killed - and it still finds a server this
        // checkout owns when the file naming it has gone stale.
        match livePid () |> Option.bind aliveProcess with
        | None ->
            if File.Exists pidFile then
                File.Delete pidFile
                Out.plain "Treemon is not running; cleared a stale pid file."
            else
                Out.plain "Treemon is not running."

            0
        | Some child ->
            use child = child
            // Only the server, matching treemon.ps1. Killing the tree would take TerminalHost and
            // every embedded terminal with it, and the whole point of TerminalHost outliving a
            // restart is that the user's terminals survive one.
            child.Kill()
            child.WaitForExit 10_000 |> ignore
            File.Delete pidFile
            Out.good $"Stopped Treemon (PID {child.Id})."
            0

module Frontend =
    let build () =
        Exec.runOrFail "dotnet" [ "tool"; "restore" ]
        Exec.runOrFail "npm" [ "install"; "--no-audit"; "--no-fund" ]
        Exec.runOrFail "npm" [ "run"; "build" ]

        if not (Directory.Exists distDir) then
            failwith "dist/ not found after the frontend build"

        if Directory.Exists wwwRoot then
            Directory.Delete(wwwRoot, recursive = true)

        Directory.CreateDirectory wwwRoot |> ignore

        Directory.GetFiles(distDir, "*", SearchOption.AllDirectories)
        |> Array.iter (fun source ->
            let target = Path.Combine(wwwRoot, Path.GetRelativePath(distDir, source))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(source, target, overwrite = true))

        Out.good "Frontend built into wwwroot/"

    let ensure () =
        let populated =
            Directory.Exists wwwRoot
            && Directory.GetFiles(wwwRoot, "*", SearchOption.AllDirectories).Length > 0

        if not populated then
            Out.warn "wwwroot/ is empty, building the frontend..."
            build ()

/// Publishing is how a Linux install takes an update, and there is no `deploy` there to rebuild the
/// client for it. Rebuilding here rather than relying on `ensure` - which only asks whether wwwroot
/// has anything in it at all - is what stops a server update from serving last release's assets.
let private publish () =
    Frontend.build ()

    Exec.runOrFail
        "dotnet"
        [ "publish"
          Path.Combine(scriptDir, "src", "Server", "Server.fsproj")
          "-c"
          "Release"
          "-o"
          publishDir ]

    Out.good $"Server published to {publishDir}"

let private invokeTm (arguments: string list) =
    Exec.run
        "dotnet"
        ([ "run"; "--project"; Path.Combine(scriptDir, "src", "Cli", "Cli.fsproj"); "--" ]
         @ arguments
         @ [ "--port"; string defaultPort ])

/// A root change only reaches the dashboard on the next start, so a running server is restarted for
/// it - the wrapper contract the PowerShell lifecycle documents. The CLI reports a tri-state: 0 all
/// applied, 2 partially applied, 1 nothing persisted, and only the first two are worth restarting
/// for. Inside an embedded terminal the change is still saved but the restart is deferred, because
/// production must not be started from a terminal it would die with.
let private changeRoots (arguments: string list) =
    let exitCode = invokeTm arguments

    if exitCode = 0 || exitCode = 2 then
        match Server.livePid () with
        | None -> ()
        | Some _ when Server.startedFromEmbeddedTerminal () ->
            Out.warn "Production was not restarted because this is a Treemon embedded terminal."
            Out.plain "The change is saved; run 'restart' from an external terminal to apply it."
        | Some _ ->
            // Restarted with no roots: the server re-reads the persisted set at startup, and passing
            // the just-changed paths again would pin this run to them instead.
            Server.stop () |> ignore
            Server.start [] |> ignore

    exitCode

let private showStatus () =
    match Server.livePid () with
    | None ->
        Out.plain "Treemon is not running."
        0
    | Some processId ->
        Out.good $"Treemon is running (PID {processId}) on http://localhost:{defaultPort}"

        if Ports.isFree defaultPort then
            Out.warn $"  but nothing is listening on port {defaultPort}"

        if Ports.isFree canvasPort then
            Out.warn $"  the canvas doc server is not listening on port {canvasPort}"

        Server.currentLogFile () |> Option.iter (fun path -> Out.plain $"Log: {path}")
        // The roots the CLI reports are the persisted ones. A server started with roots on the
        // command line is watching those instead, so the two can differ.
        Out.plain "Configured roots (the running server may have been given others):"
        invokeTm [ "roots" ]

let private showLog () =
    match Server.currentLogFile () with
    | None ->
        Out.plain "No server log yet."
        0
    | Some path ->
        if (Server.livePid ()).IsNone then
            // currentLogFile picks the newest file, which after a stop belongs to a run that is over.
            Out.warn "Treemon is not running; this is the log of the last run."

        Out.plain $"--- {path} ---"
        // The server still has the log open for writing, and on Windows a plain read is refused
        // unless the reader agrees to share with a writer.
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream)

        let rec readAll () =
            match reader.ReadLine() with
            | null -> ()
            | line ->
                Out.plain line
                readAll ()

        readAll ()
        0

let private startDev (roots: string list) =
    let devApiPort = 5001
    let devVitePort = 5174

    let terminalHostExecutable =
        [ "Debug"; "Release" ]
        |> List.map (fun configuration ->
            Path.Combine(
                scriptDir, "src", "TerminalHost", "bin", configuration, "net10.0",
                (if isWindows then "TerminalHost.exe" else "TerminalHost")))
        |> List.tryFind File.Exists

    // The reporting extension defaults to production's 5000, so without TREEMON_PORTS every session
    // started from a dev terminal would report its activity to production instead.
    // Never the production state directory. A dev server that shares it discovers, replaces and
    // shuts down the TerminalHost a running production instance owns - taking that user's terminals
    // with it. An explicit override still wins, the way the executable override does.
    let devStateDirectory =
        match Environment.GetEnvironmentVariable "TREEMON_TERMINAL_HOST_STATE_DIR" with
        | null | "" ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
                "Treemon",
                "TerminalHost-Dev")
            |> Path.GetFullPath
        | configured -> Path.GetFullPath configured

    Directory.CreateDirectory devStateDirectory |> ignore

    let environment =
        [ "VITE_PORT", string devVitePort
          "API_PORT", string devApiPort
          "TREEMON_PORTS", string devApiPort
          "TREEMON_TERMINAL_HOST_STATE_DIR", devStateDirectory ]
        @ (terminalHostExecutable
           |> Option.map (fun path -> [ ("TREEMON_TERMINAL_HOST_EXECUTABLE", path) ])
           |> Option.defaultValue [])

    let spawn name arguments =
        let info = Exec.startInfo name arguments
        environment |> List.iter (fun (key, value) -> info.Environment[key] <- value)
        Process.Start info

    Out.info "Starting dev mode..."
    Out.plain $"  Server: http://localhost:{devApiPort} (dotnet watch)"
    Out.plain $"  Vite:   http://localhost:{devVitePort}"
    Out.plain "  Press Ctrl+C to stop both processes"

    if terminalHostExecutable.IsNone then
        Out.warn "  No built TerminalHost found; embedded terminals will not start."

    use server =
        spawn
            "dotnet"
            ([ "watch"; "run"; "--project"; Path.Combine(scriptDir, "src", "Server"); "--" ]
             @ roots
             @ [ "--port"; string devApiPort; "--dashboard-port"; string devVitePort ])

    // Starting Vite can fail outright - npx missing is the documented first-run state on Ubuntu -
    // and the server is already running by then. Disposing it would not stop it, so it has to be
    // killed here or it keeps port 5001 with nothing recording its pid.
    use vite =
        try
            spawn "npx" [ "vite"; "--port"; string devVitePort ]
        with error ->
            server.Kill true
            server.WaitForExit 5_000 |> ignore
            reraise ()

    let rec waitForEither () =
        if server.HasExited || vite.HasExited then
            ()
        else
            Thread.Sleep 500
            waitForEither ()

    try
        waitForEither ()
    finally
        Out.warn "Shutting down dev processes..."

        [ server; vite ]
        |> List.iter (fun child ->
            if not child.HasExited then
                try
                    child.Kill true
                    child.WaitForExit 5_000 |> ignore
                with :? InvalidOperationException -> ())

    0

let private usage () =
    Out.info "Usage: treemon <command> [worktree-root...]"
    Out.plain ""
    Out.plain "  build              Build the frontend into wwwroot/"
    Out.plain "  publish            Publish the server into .publish/"
    Out.plain "  start [<path>...]  Start the production server (builds the frontend if missing)"
    Out.plain "  stop               Stop the production server"
    Out.plain "  restart [<path>...]  Stop, then start. Roots are not remembered - repeat them here"
    Out.plain "  status             Show PID, ports and watched roots"
    Out.plain "  log                Print the current server log"
    Out.plain "  dev [<path>...]    Server on 5001 plus Vite on 5174"
    Out.plain "  setup-ttyd         Install the pinned ttyd for embedded terminals"
    Out.plain "  add/remove/roots   Manage watched roots through the tm CLI"
    Out.plain ""
    Out.plain "'deploy' is not ported yet - use treemon.ps1 deploy on Windows."
    1

let private arguments = fsi.CommandLineArgs |> Array.toList |> List.skip 1

let exitCode =
    try
        match arguments with
        | "build" :: _ ->
            Frontend.build ()
            0
        | "publish" :: _ ->
            publish ()
            0
        | "start" :: roots ->
            // Before ensure: building the frontend first would make an embedded terminal wait
            // through a full build only to be told the command is refused there.
            Server.refuseFromEmbeddedTerminal "start"
            Frontend.ensure ()
            Server.start roots
        | "stop" :: _ -> Server.stop ()
        | "restart" :: roots ->
            Server.refuseFromEmbeddedTerminal "restart"
            Server.stop () |> ignore
            Frontend.ensure ()
            Server.start roots
        | "status" :: _ -> showStatus ()
        | "log" :: _ -> showLog ()
        | "dev" :: roots -> startDev roots
        | "setup-ttyd" :: _ -> Ttyd.install ()
        | "roots" :: _ -> invokeTm arguments
        | ("add" | "remove") :: _ -> changeRoots arguments
        | [] -> usage ()
        | unknown :: _ ->
            // Printing bare usage for a typo left it indistinguishable from asking for help, and
            // `stop now` in particular looked like a no-op while the server kept running.
            Out.bad $"Unknown command '{unknown}'."
            usage ()
    with error ->
        Out.bad error.Message
        1

exit exitCode
