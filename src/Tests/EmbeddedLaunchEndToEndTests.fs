module Tests.EmbeddedLaunchEndToEndTests

open System
open System.Collections.Concurrent
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Tests.GitTestHelpers
open Treemon.TerminalHosting

type private ProcessIdentity =
    { Pid: int
      StartTimeUtcTicks: int64
      Name: string }

type private RunningProcess =
    { Process: Process
      Identity: ProcessIdentity
      Output: ConcurrentQueue<string> }

type private ProcessRow =
    { Pid: int
      ParentPid: int
      StartTimeUtcTicks: int64
      Name: string }

type private HostManifest =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      Endpoint: string
      BearerToken: string }

type private RegistryTerminal =
    { SessionId: string
      WorktreePath: string
      AttachmentEndpoint: string }

type private RegistryEvidence =
    { Raw: string
      Revision: int64
      Terminals: RegistryTerminal list }

type private RecorderEvidence =
    { Raw: string
      TerminalSessionId: string
      WorktreePath: string
      Arguments: string list }

type private ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

type private FixturePaths =
    { Root: string
      Repository: string
      Origin: string
      RoutesWorktree: string
      CanvasWorktree: string
      CliWorktree: string
      AutoSyncWorktree: string
      RuntimeDirectory: string
      ConfigDirectory: string
      HostStateDirectory: string
      RecorderPath: string
      PromptFilePath: string
      CreatedBranch: string
      CreatedWorktree: string }

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

#if DEBUG
let private configuration = "Debug"
#else
let private configuration = "Release"
#endif

let private serverExecutable =
    Path.Combine(
        repoRoot,
        "src",
        "Server",
        "bin",
        configuration,
        "net10.0",
        "Treemon.exe"
    )

let private hostExecutable =
    Path.Combine(
        repoRoot,
        "src",
        "TerminalHost",
        "bin",
        configuration,
        "net10.0",
        "TerminalHost.exe"
    )

let private ttydExecutable =
    Path.Combine(Path.GetDirectoryName(hostExecutable), TerminalHostLayout.TtydExecutableName)

let private recorderExecutable =
    Path.Combine(
        repoRoot,
        "src",
        "TestAgentRecorder",
        "bin",
        configuration,
        "net10.0",
        "copilot.exe"
    )

let private emit (message: string) =
    TestContext.Progress.WriteLine(message)

let private ensure (condition: bool) (message: string) =
    if not condition then
        raise (InvalidOperationException(message))

let private requireOk (context: string) (result: Result<'a, string>) =
    match result with
    | Ok value -> value
    | Error error -> raise (InvalidOperationException($"{context}: {error}"))

let private jsonString (name: string) (element: JsonElement) =
    let value = element.GetProperty(name).GetString()

    if isNull value then
        raise (InvalidOperationException($"JSON property '{name}' was null"))
    else
        value

let private redactAttachmentTokens (raw: string) =
    Regex.Replace(
        raw,
        @"(/_treemon/[^/""\\]+/)[^/""\\]+(/)",
        "$1<redacted>$2",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1.0)
    )

let private parseRegistry (raw: string) : RegistryEvidence =
    use document = JsonDocument.Parse(raw)
    let root = document.RootElement

    let terminals =
        root.GetProperty("terminals").EnumerateArray()
        |> Seq.map (fun terminal ->
            { SessionId = jsonString "sessionId" terminal
              WorktreePath = jsonString "worktreePath" terminal
              AttachmentEndpoint = jsonString "attachmentEndpoint" terminal })
        |> List.ofSeq

    { Raw = redactAttachmentTokens raw
      Revision = root.GetProperty("revision").GetInt64()
      Terminals = terminals }

let private parseRecorderLine (raw: string) : RecorderEvidence =
    use document = JsonDocument.Parse(raw)
    let root = document.RootElement
    let arguments = root.GetProperty("args")

    let parsedArguments =
        match arguments.ValueKind with
        | JsonValueKind.Array ->
            arguments.EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.map (fun value -> if isNull value then "" else value)
            |> List.ofSeq
        | JsonValueKind.String -> [ arguments.GetString() |> Option.ofObj |> Option.defaultValue "" ]
        | JsonValueKind.Null -> []
        | kind ->
            raise (InvalidOperationException($"Recorder args had unexpected JSON kind {kind}"))

    { Raw = raw
      TerminalSessionId = jsonString "terminalSessionId" root
      WorktreePath = jsonString "worktreePath" root
      Arguments = parsedArguments }

let private readRecorder (path: string) =
    if File.Exists(path) then
        File.ReadAllLines(path, Encoding.UTF8)
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.map parseRecorderLine
        |> Array.toList
    else
        []

let private waitForValue
    (description: string)
    (timeout: TimeSpan)
    (poll: unit -> Async<'a option>)
    : Async<'a>
    =
    let deadline = DateTime.UtcNow.Add(timeout)

    let rec loop lastError =
        async {
            if DateTime.UtcNow >= deadline then
                let detail =
                    lastError
                    |> Option.map (fun error -> $" Last error: {error}")
                    |> Option.defaultValue ""

                return
                    raise (
                        TimeoutException(
                            $"Timed out waiting for {description}.{detail}"
                        )
                    )
            else
                try
                    match! poll () with
                    | Some value -> return value
                    | None ->
                        do! Async.Sleep 100
                        return! loop lastError
                with
                | :? InvalidOperationException as error
                    when error.Message.StartsWith(
                        "Isolated server exited",
                        StringComparison.Ordinal
                    ) ->
                    return raise error
                | error ->
                    do! Async.Sleep 100
                    return! loop (Some error.Message)
        }

    loop None

let private processStartInfo
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    (redirectOutput: bool)
    =
    let info =
        ProcessStartInfo(
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        )

    arguments |> List.iter info.ArgumentList.Add
    environment |> List.iter (fun (name, value) -> info.Environment[name] <- value)

    if redirectOutput then
        info.StandardOutputEncoding <- Encoding.UTF8
        info.StandardErrorEncoding <- Encoding.UTF8

    info

let private readIdentity (proc: Process) : ProcessIdentity =
    proc.Refresh()

    { Pid = proc.Id
      StartTimeUtcTicks = proc.StartTime.ToUniversalTime().Ticks
      Name = proc.ProcessName }

let private identityIsAlive (identity: ProcessIdentity) =
    try
        use proc = Process.GetProcessById(identity.Pid)
        proc.Refresh()

        not proc.HasExited
        && proc.StartTime.ToUniversalTime().Ticks = identity.StartTimeUtcTicks
    with
    | :? ArgumentException
    | :? InvalidOperationException
    | :? Win32Exception ->
        false

let private runProcess
    (timeout: TimeSpan)
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    : Async<ProcessResult>
    =
    async {
        let info =
            processStartInfo
                fileName
                arguments
                workingDirectory
                environment
                true

        use proc = new Process(StartInfo = info)
        ensure (proc.Start()) $"Could not start {fileName}"

        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        use timeoutSource = new CancellationTokenSource(timeout)

        try
            do!
                proc.WaitForExitAsync(timeoutSource.Token)
                |> Async.AwaitTask
        with :? OperationCanceledException ->
            if not proc.HasExited then
                proc.Kill(entireProcessTree = true)
                proc.WaitForExit()

            let! timedOutStdout = stdout |> Async.AwaitTask
            let! timedOutStderr = stderr |> Async.AwaitTask

            return
                raise (
                    TimeoutException(
                        $"{fileName} timed out after {timeout}.{Environment.NewLine}"
                        + $"stdout:{Environment.NewLine}{timedOutStdout}{Environment.NewLine}"
                        + $"stderr:{Environment.NewLine}{timedOutStderr}"
                    )
                )

        let! output = stdout |> Async.AwaitTask
        let! error = stderr |> Async.AwaitTask

        return
            { ExitCode = proc.ExitCode
              Stdout = output
              Stderr = error }
    }

let private startRunningProcess
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    : RunningProcess
    =
    let info =
        processStartInfo
            fileName
            arguments
            workingDirectory
            environment
            true

    let output = ConcurrentQueue<string>()
    let proc = new Process(StartInfo = info, EnableRaisingEvents = true)

    proc.OutputDataReceived.Add(fun args ->
        if not (isNull args.Data) then
            output.Enqueue($"stdout: {args.Data}"))

    proc.ErrorDataReceived.Add(fun args ->
        if not (isNull args.Data) then
            output.Enqueue($"stderr: {args.Data}"))

    ensure (proc.Start()) $"Could not start {fileName}"
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    { Process = proc
      Identity = readIdentity proc
      Output = output }

let private outputTail (running: RunningProcess) =
    running.Output.ToArray()
    |> Array.rev
    |> Array.truncate 80
    |> Array.rev
    |> String.concat Environment.NewLine

let private ensureServerRunning (server: RunningProcess) =
    if server.Process.HasExited then
        raise (
            InvalidOperationException(
                $"Isolated server exited with code {server.Process.ExitCode}.{Environment.NewLine}"
                + outputTail server
            )
        )

let private stopRunningProcess (running: RunningProcess) : Result<unit, string> =
    try
        if identityIsAlive running.Identity then
            running.Process.Kill(entireProcessTree = true)

            if not (running.Process.WaitForExit(15_000)) then
                Error(
                    $"Exact process {running.Identity.Name} "
                    + $"{running.Identity.Pid}@{running.Identity.StartTimeUtcTicks} did not exit"
                )
            elif identityIsAlive running.Identity then
                Error(
                    $"Exact process identity remained live after kill: "
                    + $"{running.Identity.Pid}@{running.Identity.StartTimeUtcTicks}"
                )
            else
                Ok()
        else
            Ok()
    with error ->
        Error(
            $"Could not stop exact process {running.Identity.Pid}@"
            + $"{running.Identity.StartTimeUtcTicks}: {error.Message}"
        )

let private killExactIdentity (identity: ProcessIdentity) : Result<unit, string> =
    try
        if not (identityIsAlive identity) then
            Ok()
        else
            use proc = Process.GetProcessById(identity.Pid)

            if proc.StartTime.ToUniversalTime().Ticks <> identity.StartTimeUtcTicks then
                Error $"PID {identity.Pid} was reused before exact cleanup"
            else
                proc.Kill(entireProcessTree = true)

                if proc.WaitForExit(15_000) && not (identityIsAlive identity) then
                    Ok()
                else
                    Error
                        $"Exact process {identity.Pid}@{identity.StartTimeUtcTicks} survived cleanup"
    with error ->
        Error(
            $"Could not kill exact process {identity.Pid}@"
            + $"{identity.StartTimeUtcTicks}: {error.Message}"
        )

let private parseManifest (path: string) : HostManifest =
    use document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8))
    let root = document.RootElement

    { Pid = root.GetProperty("pid").GetInt32()
      ProcessStartTimeUtcTicks =
        root.GetProperty("processStartTimeUtcTicks").GetInt64()
      Endpoint = jsonString "endpoint" root
      BearerToken = jsonString "bearerToken" root }

let private tryReadManifest (path: string) =
    if File.Exists(path) then
        Some(parseManifest path)
    else
        None

let private waitForManifest (path: string) =
    waitForValue
        "the isolated TerminalHost manifest"
        (TimeSpan.FromSeconds 45.0)
        (fun () ->
            async {
                return
                    try
                        tryReadManifest path
                    with :? JsonException ->
                        None
            })

let private sendControl
    (client: HttpClient)
    (manifest: HostManifest)
    (method: HttpMethod)
    (relativePath: string)
    : Async<string>
    =
    async {
        use request =
            new HttpRequestMessage(
                method,
                Uri(Uri(manifest.Endpoint), relativePath)
            )

        request.Headers.Authorization <-
            AuthenticationHeaderValue("Bearer", manifest.BearerToken)

        use! response =
            client.SendAsync(request)
            |> Async.AwaitTask

        let! raw = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        ensure
            response.IsSuccessStatusCode
            ($"TerminalHost {method} {relativePath} returned "
             + $"{int response.StatusCode}: {raw}")

        return raw
    }

let private readRegistry
    (client: HttpClient)
    (manifest: HostManifest)
    : Async<RegistryEvidence>
    =
    async {
        let! raw =
            sendControl
                client
                manifest
                HttpMethod.Get
                "/api/v2/terminals"

        return parseRegistry raw
    }

let private closeTerminal
    (client: HttpClient)
    (manifest: HostManifest)
    (terminalId: string)
    =
    sendControl
        client
        manifest
        HttpMethod.Delete
        $"/api/v2/terminals/{Uri.EscapeDataString terminalId}"

let private registryIds (registry: RegistryEvidence) =
    registry.Terminals |> List.map _.SessionId |> Set.ofList

let private pathEquals (left: string) (right: string) =
    PathUtils.pathEquals
        (Path.GetFullPath left)
        (Path.GetFullPath right)

let private windowsTerminalHandles () =
    Win32.listTopLevelWindows ()
    |> List.filter (fun hwnd ->
        Win32.getWindowClassName hwnd = "CASCADIA_HOSTING_WINDOW_CLASS")
    |> List.map int64
    |> List.sort

let private startResultId
    (context: string)
    (result: Result<EmbeddedTerminalStartResult, string>)
    =
    result
    |> requireOk context
    |> _.TerminalId
    |> EmbeddedTerminalId.value

let private runRoute
    (client: HttpClient)
    (manifest: HostManifest)
    (recorderPath: string)
    (server: RunningProcess)
    (name: string)
    (expectedPath: string)
    (expectedArguments: string list)
    (invoke: unit -> Async<string option>)
    =
    async {
        ensureServerRunning server
        let! before = readRegistry client manifest
        let beforeRecorder = readRecorder recorderPath
        let beforeHandles = windowsTerminalHandles ()
        let! reportedId = invoke ()

        let! after, addedRecorder =
            waitForValue
                $"route '{name}' registry and recorder evidence"
                (TimeSpan.FromSeconds 90.0)
                (fun () ->
                    async {
                        ensureServerRunning server
                        let! current = readRegistry client manifest
                        let currentRecorder = readRecorder recorderPath

                        if
                            current.Terminals.Length >= before.Terminals.Length + 1
                            && currentRecorder.Length >= beforeRecorder.Length + 1
                        then
                            return
                                Some(
                                    current,
                                    currentRecorder[beforeRecorder.Length]
                                )
                        else
                            return None
                    })

        let beforeIds = registryIds before
        let afterIds = registryIds after
        let addedIds = Set.difference afterIds beforeIds |> Set.toList

        ensure
            (after.Terminals.Length = before.Terminals.Length + 1)
            $"Route '{name}' did not add exactly one terminal"

        ensure
            (addedIds.Length = 1)
            $"Route '{name}' did not produce one exact new terminal ID"

        let recorderCountAfter = (readRecorder recorderPath).Length

        ensure
            (recorderCountAfter = beforeRecorder.Length + 1)
            $"Route '{name}' did not produce exactly one recorder payload"

        let addedId = addedIds.Head

        reportedId
        |> Option.iter (fun id ->
            ensure
                (id = addedId)
                $"Route '{name}' returned terminal {id}, but registry added {addedId}")

        let addedTerminal =
            after.Terminals
            |> List.find (fun terminal -> terminal.SessionId = addedId)

        ensure
            (pathEquals addedTerminal.WorktreePath expectedPath)
            ($"Route '{name}' registry worktree mismatch: "
             + $"{addedTerminal.WorktreePath}")

        ensure
            (addedRecorder.TerminalSessionId = addedId)
            $"Route '{name}' recorder terminal ID mismatch"

        ensure
            (pathEquals addedRecorder.WorktreePath expectedPath)
            ($"Route '{name}' recorder worktree mismatch: "
             + $"{addedRecorder.WorktreePath}")

        ensure
            (addedRecorder.Arguments = expectedArguments)
            ($"Route '{name}' recorder args mismatch.{Environment.NewLine}"
             + $"Expected: {JsonSerializer.Serialize(expectedArguments)}{Environment.NewLine}"
             + $"Actual: {addedRecorder.Raw}")

        do! Async.Sleep 500
        let! stable = readRegistry client manifest
        let stableRecorder = readRecorder recorderPath
        let afterHandles = windowsTerminalHandles ()

        ensure
            (registryIds stable = afterIds)
            $"Route '{name}' terminal did not remain in the authoritative registry"

        ensure
            (stableRecorder.Length = beforeRecorder.Length + 1)
            $"Route '{name}' produced a delayed duplicate recorder payload"

        ensure
            (afterHandles = beforeHandles)
            $"Route '{name}' changed the native Windows Terminal HWND set"

        emit $"ROUTE={name}"
        emit $"REGISTRY_BEFORE_RAW={before.Raw}"
        emit $"REGISTRY_AFTER_RAW={after.Raw}"
        emit $"RECORDER_RAW={addedRecorder.Raw}"
        emit $"HWNDS_BEFORE_RAW={JsonSerializer.Serialize(beforeHandles)}"
        emit $"HWNDS_AFTER_RAW={JsonSerializer.Serialize(afterHandles)}"

        return addedId
    }

let private createFixturePaths () =
    let suffix = Guid.NewGuid().ToString("N")[..9]
    let root =
        Path.Combine(
            repoRoot,
            ".agents",
            "verify-runtime",
            $"embedded-launch-{suffix}"
        )
    let repository = Path.Combine(root, "repo")
    let createdBranch = $"e2e-created-{suffix}"

    { Root = root
      Repository = repository
      Origin = Path.Combine(root, "origin.git")
      RoutesWorktree = Path.Combine(root, "wt-routes")
      CanvasWorktree = Path.Combine(root, "wt-canvas")
      CliWorktree = Path.Combine(root, "wt-cli")
      AutoSyncWorktree = Path.Combine(root, "wt-autosync")
      RuntimeDirectory = Path.Combine(root, "runtime")
      ConfigDirectory = Path.Combine(root, "config")
      HostStateDirectory = Path.Combine(root, "terminal-host-state")
      RecorderPath = Path.Combine(root, "copilot-recorder.jsonl")
      PromptFilePath = Path.Combine(root, "tm-launch-prompt.md")
      CreatedBranch = createdBranch
      CreatedWorktree = Path.Combine(root, $"tm-{createdBranch}") }

let private initializeFixture fixture =
    Directory.CreateDirectory(fixture.Root) |> ignore
    Directory.CreateDirectory(fixture.RuntimeDirectory) |> ignore
    Directory.CreateDirectory(fixture.ConfigDirectory) |> ignore
    Directory.CreateDirectory(fixture.HostStateDirectory) |> ignore

    initRepoOnMain fixture.Repository
    writeText fixture.Repository "baseline.txt" "base-1"
    gitOk fixture.Repository [ "add"; "baseline.txt" ]
    gitOk fixture.Repository [ "commit"; "-m"; "fixture baseline" ]

    Directory.CreateDirectory(fixture.Origin) |> ignore
    gitOk fixture.Origin [ "init"; "--bare" ]
    gitOk fixture.Repository [ "remote"; "add"; "origin"; fixture.Origin ]
    gitOk fixture.Repository [ "push"; "-u"; "origin"; "main" ]

    [ "routes", fixture.RoutesWorktree
      "canvas", fixture.CanvasWorktree
      "cli", fixture.CliWorktree
      "autosync", fixture.AutoSyncWorktree ]
    |> List.iter (fun (branch, path) ->
        gitOk
            fixture.Repository
            [ "worktree"; "add"; "-b"; branch; path; "main" ])

    File.AppendAllText(
        Path.Combine(fixture.Repository, "baseline.txt"),
        $"{Environment.NewLine}base-2"
    )
    gitOk fixture.Repository [ "add"; "baseline.txt" ]
    gitOk fixture.Repository [ "commit"; "-m"; "advance main" ]
    gitOk fixture.Repository [ "push"; "origin"; "main" ]

    File.AppendAllText(
        Path.Combine(fixture.AutoSyncWorktree, "baseline.txt"),
        $"{Environment.NewLine}local dirty change"
    )

    let canvasDirectory =
        Path.Combine(fixture.CanvasWorktree, ".agents", "canvas")

    Directory.CreateDirectory(canvasDirectory) |> ignore

    File.WriteAllText(
        Path.Combine(canvasDirectory, "review.html"),
        "<!doctype html><html><body>Embedded launch verification</body></html>",
        UTF8Encoding(false)
    )

    File.WriteAllText(
        fixture.PromptFilePath,
        "Verify the external tm launch route.\nPreserve this second line.",
        UTF8Encoding(false)
    )

let private startServer fixture port =
    let originalPath =
        Environment.GetEnvironmentVariable("PATH")
        |> Option.ofObj
        |> Option.defaultValue ""

    let server =
        startRunningProcess
            serverExecutable
            [ fixture.Repository
              "--port"
              string port
              "--no-canvas" ]
            fixture.RuntimeDirectory
            [ TerminalHostLayout.StateDirectoryEnvironmentVariable,
              fixture.HostStateDirectory
              "TREEMON_TERMINAL_HOST_EXECUTABLE", hostExecutable
              "TREEMON_CONFIG_DIR", fixture.ConfigDirectory
              "TM_COPILOT_RECORDER", fixture.RecorderPath
              "PATH",
              $"{Path.GetDirectoryName(recorderExecutable)}{Path.PathSeparator}{originalPath}" ]

    async {
        try
            let api = Cli.Program.createApi port
            let expectedPaths =
                [ fixture.Repository
                  fixture.RoutesWorktree
                  fixture.CanvasWorktree
                  fixture.CliWorktree
                  fixture.AutoSyncWorktree ]

            let! _ =
                waitForValue
                    "the isolated server to enumerate fixture worktrees"
                    (TimeSpan.FromSeconds 60.0)
                    (fun () ->
                        async {
                            ensureServerRunning server

                            try
                                let! dashboard = api.getWorktrees ()

                                let actualPaths =
                                    dashboard.Repos
                                    |> List.collect _.Worktrees
                                    |> List.map (fun worktree ->
                                        WorktreePath.value worktree.Path)

                                let allPresent =
                                    expectedPaths
                                    |> List.forall (fun expected ->
                                        actualPaths |> List.exists (pathEquals expected))

                                return
                                    if allPresent then
                                        Some dashboard
                                    else
                                        None
                            with _ when not server.Process.HasExited ->
                                return None
                        })

            return server, api
        with error ->
            let cleanup = stopRunningProcess server
            server.Process.Dispose()

            return
                raise (
                    InvalidOperationException(
                        $"Could not start isolated Treemon: {error.Message}. "
                        + $"Cleanup: {cleanup}.{Environment.NewLine}{outputTail server}",
                        error
                    )
                )
    }

let private queryProcessRows () =
    async {
        let script =
            "$items = @(Get-CimInstance Win32_Process | ForEach-Object { "
            + "[pscustomobject]@{ pid = [int]$_.ProcessId; "
            + "parentPid = [int]$_.ParentProcessId; "
            + "startTicks = if ($_.CreationDate) { "
            + "[int64]$_.CreationDate.ToUniversalTime().Ticks } else { 0 }; "
            + "name = [string]$_.Name } }); "
            + "ConvertTo-Json -InputObject $items -Compress"

        let! result =
            runProcess
                (TimeSpan.FromSeconds 20.0)
                "pwsh.exe"
                [ "-NoLogo"
                  "-NoProfile"
                  "-NonInteractive"
                  "-Command"
                  script ]
                repoRoot
                []

        ensure
            (result.ExitCode = 0)
            $"Could not enumerate process identities: {result.Stderr}"

        use document = JsonDocument.Parse(result.Stdout)

        return
            document.RootElement.EnumerateArray()
            |> Seq.map (fun item ->
                { Pid = item.GetProperty("pid").GetInt32()
                  ParentPid = item.GetProperty("parentPid").GetInt32()
                  StartTimeUtcTicks = item.GetProperty("startTicks").GetInt64()
                  Name = jsonString "name" item })
            |> List.ofSeq
    }

let private descendantIdentities hostPid rows =
    let rec collect known remaining collected =
        let children =
            remaining
            |> List.filter (fun row -> Set.contains row.ParentPid known)

        if children.IsEmpty then
            collected
        else
            let childIds = children |> List.map _.Pid |> Set.ofList
            let rest =
                remaining
                |> List.filter (fun row -> not (Set.contains row.Pid childIds))

            collect
                (Set.union known childIds)
                rest
                (List.append collected children)

    collect (Set.singleton hostPid) rows []
    |> List.filter (fun row -> row.StartTimeUtcTicks > 0L)
    |> List.map (fun row ->
        { Pid = row.Pid
          StartTimeUtcTicks = row.StartTimeUtcTicks
          Name = row.Name })

let private portOwners port =
    async {
        let! result =
            runProcess
                (TimeSpan.FromSeconds 10.0)
                "netstat.exe"
                [ "-ano"; "-p"; "tcp" ]
                repoRoot
                []

        ensure (result.ExitCode = 0) $"netstat failed: {result.Stderr}"

        let pattern =
            Regex(
                $@"^\s*TCP\s+\S+:{port}\s+\S+\s+LISTENING\s+(\d+)\s*$",
                RegexOptions.Multiline ||| RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1.0)
            )

        return
            pattern.Matches(result.Stdout)
            |> Seq.cast<Match>
            |> Seq.map (fun matched -> Int32.Parse(matched.Groups[1].Value))
            |> Seq.distinct
            |> Seq.sort
            |> List.ofSeq
    }

let private sourceStatus () =
    async {
        let! result =
            runProcess
                (TimeSpan.FromSeconds 20.0)
                "git"
                [ "--no-pager"; "status"; "--short" ]
                repoRoot
                []

        ensure (result.ExitCode = 0) $"git status failed: {result.Stderr}"
        return result.Stdout
    }

let private fileFingerprint path =
    if File.Exists(path) then
        let info = FileInfo(path)
        let hash =
            File.ReadAllBytes(path)
            |> SHA256.HashData
            |> Convert.ToHexString

        $"{hash}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
    else
        "<absent>"

let private waitForPortClosed port =
    waitForValue
        $"isolated server port {port} to close"
        (TimeSpan.FromSeconds 20.0)
        (fun () ->
            async {
                let! owners = portOwners port
                return if owners.IsEmpty then Some() else None
            })

let private deleteFixtureRoot root =
    try
        if Directory.Exists(root) then
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.iter (fun file ->
                let attributes = File.GetAttributes(file)

                if attributes.HasFlag(FileAttributes.ReadOnly) then
                    File.SetAttributes(
                        file,
                        attributes &&& ~~~FileAttributes.ReadOnly
                    ))

            Directory.Delete(root, recursive = true)

        if Directory.Exists(root) then
            Error $"Fixture root still exists: {root}"
        else
            Ok()
    with error ->
        Error $"Could not delete fixture root '{root}': {error.Message}"

let private cleanupRuntime
    (client: HttpClient)
    fixture
    server
    serverPort
    =
    async {
        let errors = ConcurrentQueue<string>()
        let manifestPath =
            Path.Combine(
                fixture.HostStateDirectory,
                TerminalHostLayout.ManifestFileName
            )

        let manifest =
            try
                tryReadManifest manifestPath
            with error ->
                errors.Enqueue(
                    $"Could not read isolated host manifest during cleanup: {error.Message}"
                )
                None

        let! descendants =
            match manifest with
            | None -> async { return [] }
            | Some host ->
                async {
                    try
                        let! rows = queryProcessRows ()
                        return descendantIdentities host.Pid rows
                    with error ->
                        errors.Enqueue(
                            $"Could not capture host descendants: {error.Message}"
                        )
                        return []
                }

        match manifest with
        | None -> ()
        | Some host ->
            try
                let! registry = readRegistry client host

                for terminal in registry.Terminals do
                    let! _ = closeTerminal client host terminal.SessionId
                    emit $"CLEANUP_TERMINAL_ID={terminal.SessionId}"

                let! empty =
                    waitForValue
                        "the isolated host registry to become empty"
                        (TimeSpan.FromSeconds 30.0)
                        (fun () ->
                            async {
                                let! current = readRegistry client host

                                return
                                    if current.Terminals.IsEmpty then
                                        Some current
                                    else
                                        None
                            })

                emit $"CLEANUP_REGISTRY_RAW={empty.Raw}"
            with error ->
                errors.Enqueue($"Terminal cleanup failed: {error.Message}")

            let hostIdentity =
                { Pid = host.Pid
                  StartTimeUtcTicks = host.ProcessStartTimeUtcTicks
                  Name = "TerminalHost" }

            try
                let! _ =
                    sendControl
                        client
                        host
                        HttpMethod.Post
                        "/api/v2/shutdown"

                let! _ =
                    waitForValue
                        "the exact isolated TerminalHost identity to exit"
                        (TimeSpan.FromSeconds 20.0)
                        (fun () ->
                            async {
                                return
                                    if identityIsAlive hostIdentity then
                                        None
                                    else
                                        Some()
                            })

                let! _ =
                    waitForValue
                        "the isolated TerminalHost manifest to be removed"
                        (TimeSpan.FromSeconds 20.0)
                        (fun () ->
                            async {
                                return
                                    if File.Exists(manifestPath) then
                                        None
                                    else
                                        Some()
                            })

                ()
            with error ->
                errors.Enqueue($"TerminalHost shutdown failed: {error.Message}")

                match killExactIdentity hostIdentity with
                | Ok() -> ()
                | Error cleanupError -> errors.Enqueue(cleanupError)

        let survivingDescendants =
            descendants |> List.filter identityIsAlive

        if not survivingDescendants.IsEmpty then
            errors.Enqueue(
                "Fixture-owned host descendants survived cleanup: "
                + JsonSerializer.Serialize(survivingDescendants)
            )

        match stopRunningProcess server with
        | Ok() -> ()
        | Error error -> errors.Enqueue(error)

        server.Process.Dispose()

        try
            do! waitForPortClosed serverPort |> Async.Ignore
        with error ->
            errors.Enqueue(error.Message)

        emit
            $"CLEANUP_HOST_DESCENDANTS_RAW={JsonSerializer.Serialize(descendants |> List.map (fun identity -> {| pid = identity.Pid; startTimeUtcTicks = identity.StartTimeUtcTicks; name = identity.Name |}))}"
        emit
            $"CLEANUP_SERVER_IDENTITY_RAW={JsonSerializer.Serialize({| pid = server.Identity.Pid; startTimeUtcTicks = server.Identity.StartTimeUtcTicks; name = server.Identity.Name |})}"

        return errors |> Seq.toList
    }

let private waitForAcceptedAutoSyncRecord
    storePath
    worktreePath
    baseRevision
    =
    waitForValue
        "the isolated AutoSync acceptance record"
        (TimeSpan.FromSeconds 20.0)
        (fun () ->
            async {
                if not (File.Exists(storePath)) then
                    return None
                else
                    let raw = File.ReadAllText(storePath, Encoding.UTF8)
                    use document = JsonDocument.Parse(raw)

                    let record =
                        document.RootElement.EnumerateObject()
                        |> Seq.tryFind (fun property ->
                            pathEquals property.Name worktreePath)

                    return
                        record
                        |> Option.bind (fun property ->
                            let revision =
                                jsonString "base_revision" property.Value

                            if revision = baseRevision then
                                Some raw
                            else
                                None)
            })

let private verifyForcedDeliveryFailure
    client
    manifest
    fixture
    recorderPath
    =
    async {
        let! before = readRegistry client manifest
        let beforeRecorder = readRecorder recorderPath
        let beforeHandles = windowsTerminalHandles ()
        let sendObservations =
            ConcurrentQueue<string * RegistryEvidence>()
        let launchResults = ConcurrentQueue<Result<unit, string>>()
        let deliveryResults = ConcurrentQueue<bool>()

        let failingConfig =
            { TerminalHostClient.defaultConfig [] with
                HostExecutablePath = hostExecutable
                HostStateDirectory = fixture.HostStateDirectory
                TtydExecutablePath = Some ttydExecutable
                SendTerminalCommand =
                    fun endpoint _ ->
                        async {
                            let! atSend = readRegistry client manifest
                            sendObservations.Enqueue((endpoint, atSend))

                            return
                                Error
                                    "forced embedded launch delivery failure"
                        } }

        let manager = EmbeddedTerminal.createWithConfig failingConfig

        let launch worktreePath prompt =
            async {
                let command =
                    CodingToolCli.build
                        None
                        (CodingToolCli.Interactive prompt)

                let! result =
                    EmbeddedTerminal.startWithCommand
                        manager
                        worktreePath
                        command.AsShellString

                let reduced = result |> Result.map ignore
                launchResults.Enqueue(reduced)
                return reduced
            }

        let deliver request =
            async {
                let! accepted =
                    AutoSync.deliver
                        (fun _ ->
                            async {
                                return
                                    SessionBridge.DeliveryResult.NoLiveSession
                            })
                        (fun () -> async { return () })
                        launch
                        request

                deliveryResults.Enqueue(accepted)
                return accepted
            }

        let failureStorePath =
            Path.Combine(fixture.Root, "forced-failure-auto-sync.json")

        let failureStore = AutoSyncStore.create failureStorePath
        failureStore.Load()

        let dependencies: AutoSync.TriggerDependencies =
            { ReadAcceptedRevision = failureStore.Get
              RecordAcceptedRevision =
                fun path revision ->
                    AutoSyncStore.publishAccepted
                        failureStore
                        path
                        { BaseRevision = revision
                          AcceptedAt = DateTimeOffset.UtcNow }
              ClearAcceptedRevision = AutoSyncStore.clear failureStore
              ReadPrStatus = fun _ -> async { return Some NoPr }
              ReadOwnership =
                fun _ ->
                    async {
                        return
                            AutoSync.WorktreeOwnership.Free(
                                AutoSync.SyncTarget.NoOpenSession None
                            )
                    }
              TryBeginOperation = fun _ -> async { return true }
              CompleteOperation = ignore
              MechanicalSync =
                fun _ ->
                    async {
                        return Error AutoSync.SyncFailure.DirtyWorktree
                    }
              ReloadGitData = fun _ -> async { return () }
              Deliver = deliver }

        let! gitData =
            GitWorktree.collectWorktreeGitData
                fixture.AutoSyncWorktree
                (Some "autosync")
                "origin"
                "main"

        ensure
            (gitData.MainBehindCount > 0 && gitData.BaseRevision.IsSome)
            "Forced-failure AutoSync fixture was not behind origin/main"

        do!
            AutoSync.trigger
                dependencies
                fixture.Repository
                "origin"
                "main"
                NoPr
                gitData

        let! after =
            waitForValue
                "forced-failure rollback to restore the exact registry"
                (TimeSpan.FromSeconds 30.0)
                (fun () ->
                    async {
                        let! current = readRegistry client manifest

                        return
                            if registryIds current = registryIds before then
                                Some current
                            else
                                None
                    })

        let observations = sendObservations.ToArray() |> Array.toList
        let launches = launchResults.ToArray() |> Array.toList
        let deliveries = deliveryResults.ToArray() |> Array.toList

        ensure
            (observations.Length = 1)
            "Forced failure did not reach exactly one command-send boundary"

        ensure
            (launches =
                [ Error "forced embedded launch delivery failure" ])
            "Forced failure did not surface the command delivery error"

        ensure
            (deliveries = [ false ])
            "AutoSync accepted a fallback whose command delivery failed"

        let endpoint, atSend = observations.Head
        let temporaryIds =
            Set.difference (registryIds atSend) (registryIds before)
            |> Set.toList

        ensure
            (atSend.Terminals.Length = before.Terminals.Length + 1)
            "Forced failure did not observe the shell after exact terminal creation"

        ensure
            (temporaryIds.Length = 1)
            "Forced failure did not create exactly one temporary terminal"

        let temporaryId = temporaryIds.Head

        ensure
            (endpoint.Contains(
                temporaryId,
                StringComparison.OrdinalIgnoreCase
            ))
            "Forced failure send endpoint did not belong to the temporary terminal"

        ensure
            (not (Set.contains temporaryId (registryIds after)))
            "Forced failure left the temporary terminal registered"

        ensure
            ((readRecorder recorderPath).Length = beforeRecorder.Length)
            "Forced failure reached the copilot recorder despite rejected delivery"

        let! acceptedRecord =
            failureStore.Get fixture.AutoSyncWorktree

        ensure
            acceptedRecord.IsNone
            "Forced failure persisted an AutoSync acceptance record"

        let! flushResult = failureStore.Flush()
        flushResult
        |> requireOk "Flush forced-failure AutoSync store"
        |> ignore

        let afterHandles = windowsTerminalHandles ()

        ensure
            (afterHandles = beforeHandles)
            "Forced delivery failure changed native Windows Terminal HWNDs"

        let failureStoreRaw =
            if File.Exists(failureStorePath) then
                File.ReadAllText(failureStorePath, Encoding.UTF8)
            else
                "<absent>"

        emit "FORCED_FAILURE=AutoSync"
        emit $"FORCED_FAILURE_REGISTRY_BEFORE_RAW={before.Raw}"
        emit $"FORCED_FAILURE_REGISTRY_AT_SEND_RAW={atSend.Raw}"
        emit $"FORCED_FAILURE_REGISTRY_AFTER_RAW={after.Raw}"
        emit
            $"FORCED_FAILURE_ENDPOINT_REDACTED={redactAttachmentTokens endpoint}"
        emit $"FORCED_FAILURE_AUTOSYNC_STORE_RAW={failureStoreRaw}"
        emit
            $"FORCED_FAILURE_HWND_BEFORE_RAW={JsonSerializer.Serialize(beforeHandles)}"
        emit
            $"FORCED_FAILURE_HWND_AFTER_RAW={JsonSerializer.Serialize(afterHandles)}"
    }

let private runScenario client fixture server api port =
    async {
        let manifestPath =
            Path.Combine(
                fixture.HostStateDirectory,
                TerminalHostLayout.ManifestFileName
            )

        let! warmupResult =
            api.startEmbeddedTerminal (WorktreePath fixture.RoutesWorktree)

        let warmup =
            startResultId "Warm up isolated TerminalHost" warmupResult

        let! closeWarmup =
            api.closeEmbeddedTerminal (EmbeddedTerminalId warmup)

        closeWarmup
        |> requireOk "Close warm-up terminal"
        |> ignore

        let! manifest = waitForManifest manifestPath

        ensure
            (Uri(manifest.Endpoint).Port <> 5000)
            "Isolated TerminalHost selected production port 5000"

        let! emptyRegistry =
            waitForValue
                "the warm-up terminal to close"
                (TimeSpan.FromSeconds 20.0)
                (fun () ->
                    async {
                        let! registry = readRegistry client manifest

                        return
                            if registry.Terminals.IsEmpty then
                                Some registry
                            else
                                None
                    })

        emit $"INITIAL_REGISTRY_RAW={emptyRegistry.Raw}"

        let directPrompt =
            "DIRECT_LAUNCH_PROMPT\nPreserve the second line exactly."

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "launchSession"
                fixture.RoutesWorktree
                [ "--yolo"; "-i"; directPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.launchSession
                                { Path = WorktreePath fixture.RoutesWorktree
                                  Prompt = directPrompt }

                        return
                            Some(
                                startResultId
                                    "launchSession"
                                    result
                            )
                    })
            |> Async.Ignore

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "tm-launch"
                fixture.CliWorktree
                [ "--yolo"; "-i"; Cli.Program.metaPrompt ]
                (fun () ->
                    async {
                        let! result =
                            runProcess
                                (TimeSpan.FromMinutes 2.0)
                                "pwsh.exe"
                                [ "-NoLogo"
                                  "-NoProfile"
                                  "-NonInteractive"
                                  "-File"
                                  Path.Combine(repoRoot, "tm.ps1")
                                  "launch"
                                  "--path"
                                  fixture.CliWorktree
                                  "--prompt-file"
                                  fixture.PromptFilePath
                                  "--port"
                                  string port ]
                                repoRoot
                                []

                        ensure
                            (result.ExitCode = 0)
                            $"tm launch failed: {result.Stderr}"

                        ensure
                            (result.Stdout.Contains(
                                "Agent launched in embedded terminal",
                                StringComparison.Ordinal
                            ))
                            $"tm launch did not report success: {result.Stdout}"

                        let copiedPrompt =
                            Path.Combine(
                                fixture.CliWorktree,
                                ".agents",
                                "prompt.md"
                            )
                        let copiedPromptText =
                            File.ReadAllText(copiedPrompt, Encoding.UTF8)
                        let sourcePromptText =
                            File.ReadAllText(
                                fixture.PromptFilePath,
                                Encoding.UTF8
                            )

                        ensure
                            (copiedPromptText = sourcePromptText)
                            "tm launch did not preserve the prompt file contents"

                        emit
                            $"TM_LAUNCH_STDOUT_RAW={JsonSerializer.Serialize(result.Stdout)}"
                        emit
                            $"TM_PROMPT_COPY_RAW={JsonSerializer.Serialize(copiedPromptText)}"

                        return None
                    })
            |> Async.Ignore

        let action =
            FixBuild "https://example.test/build/embedded-launch-e2e"
        let actionPrompt = CodingToolStatus.actionPrompt None action

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "launchAction"
                fixture.RoutesWorktree
                [ "--yolo"; "-i"; actionPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.launchAction
                                { Path = WorktreePath fixture.RoutesWorktree
                                  Action = action }

                        return
                            Some(
                                startResultId
                                    "launchAction"
                                    result
                            )
                    })
            |> Async.Ignore

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "resumeSession"
                fixture.RoutesWorktree
                [ "--yolo"; "--continue" ]
                (fun () ->
                    async {
                        let! result =
                            api.resumeSession(
                                WorktreePath fixture.RoutesWorktree
                            )

                        return
                            Some(
                                startResultId
                                    "resumeSession"
                                    result
                            )
                    })
            |> Async.Ignore

        let canvasFilename = "review.html"
        let canvasPrompt =
            CanvasSessionPrompt.forAgentDoc
                fixture.CanvasWorktree
                canvasFilename

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "explicit-canvas-session"
                fixture.CanvasWorktree
                [ "--yolo"; "-i"; canvasPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.launchAction
                                { Path = WorktreePath fixture.CanvasWorktree
                                  Action = CanvasSession canvasPrompt }

                        return
                            Some(
                                startResultId
                                    "explicit Canvas session launch"
                                    result
                            )
                    })
            |> Async.Ignore

        let createPrompt =
            "CREATE_WORKTREE_PROMPT\nPreserve the original second line."

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "create-worktree-with-prompt"
                fixture.CreatedWorktree
                [ "--yolo"; "-i"; createPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.createWorktree
                                { RepoId =
                                    fixture.Repository
                                    |> PathUtils.toRepoId
                                    |> RepoId.value
                                  BranchName =
                                    BranchName.create fixture.CreatedBranch
                                  BaseBranch = BranchName.create "main"
                                  Prompt = Some createPrompt
                                  Skill = None }

                        result
                        |> requireOk "createWorktree"
                        |> ignore

                        return None
                    })
            |> Async.Ignore

        let queuedFilename = "diff.html"
        let queuedPrompt =
            CanvasPrompt.continueWorking
                fixture.CanvasWorktree
                queuedFilename

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "queued-canvas-fallback"
                fixture.CanvasWorktree
                [ "--yolo"; "-i"; queuedPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.sendCanvasMessage
                                { WorktreePath =
                                    WorktreePath fixture.CanvasWorktree
                                  Filename = queuedFilename
                                  Payload =
                                    """{"action":"embedded-launch-e2e"}""" }

                        ensure
                            (result = CanvasMessageResult.Queued)
                            $"Queued Canvas fallback returned {result}"

                        return None
                    })
            |> Async.Ignore

        let! autoSyncReady =
            waitForValue
                "the AutoSync worktree to be dirty and behind"
                (TimeSpan.FromSeconds 60.0)
                (fun () ->
                    async {
                        ensureServerRunning server
                        let! dashboard = api.getWorktrees ()

                        return
                            dashboard.Repos
                            |> List.collect _.Worktrees
                            |> List.tryFind (fun worktree ->
                                pathEquals
                                    (WorktreePath.value worktree.Path)
                                    fixture.AutoSyncWorktree
                                && worktree.IsDirty
                                && worktree.MainBehindCount > 0)
                    })

        ensure
            (autoSyncReady.Branch = "autosync")
            "AutoSync fixture resolved the wrong branch"

        let autoSyncPrompt =
            AutoSync.fallbackPrompt
                "origin"
                "main"
                NoPr
                AutoSync.SyncFailure.DirtyWorktree

        do!
            runRoute
                client
                manifest
                fixture.RecorderPath
                server
                "autosync-fallback"
                fixture.AutoSyncWorktree
                [ "--yolo"; "-i"; autoSyncPrompt ]
                (fun () ->
                    async {
                        let! result =
                            api.toggleAutoSync
                                (WorktreePath fixture.AutoSyncWorktree)
                                true

                        result
                        |> requireOk "toggleAutoSync"
                        |> ignore

                        return None
                    })
            |> Async.Ignore

        let baseRevision =
            gitText
                fixture.Repository
                [ "rev-parse"; "refs/remotes/origin/main" ]

        let autoSyncStorePath =
            Path.Combine(
                fixture.RuntimeDirectory,
                "data",
                $"auto-sync-{port}.json"
            )

        let! autoSyncStoreRaw =
            waitForAcceptedAutoSyncRecord
                autoSyncStorePath
                fixture.AutoSyncWorktree
                baseRevision

        emit $"AUTOSYNC_ACCEPTED_STORE_RAW={autoSyncStoreRaw}"

        do!
            verifyForcedDeliveryFailure
                client
                manifest
                fixture
                fixture.RecorderPath

        let nativeSessionPath =
            Path.Combine(fixture.RuntimeDirectory, "data", "sessions.json")

        ensure
            (not (File.Exists nativeSessionPath))
            "Agent-bearing routes unexpectedly persisted native HWND session state"
    }

[<TestFixture>]
[<Category("EmbeddedLaunchE2E")>]
[<NonParallelizable>]
type EmbeddedLaunchRoutingEndToEndTests() =

    [<Test>]
    member _.``Every agent-bearing route reaches one isolated hosted shell and rolls back truthfully``() =
        async {
            if
                Environment.GetEnvironmentVariable(
                    "TREEMON_RUN_EMBEDDED_LAUNCH_E2E"
                )
                <> "1"
            then
                Assert.Ignore(
                    "Run scripts/verify-embedded-launch-routing.ps1 to execute this isolated verification."
                )

            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore(
                    "The embedded terminal launch verifier requires Windows."
                )

            ensure
                (File.Exists serverExecutable)
                $"Missing {serverExecutable}. Build the solution first."
            ensure
                (File.Exists hostExecutable)
                $"Missing {hostExecutable}. Build the solution first."
            ensure
                (File.Exists ttydExecutable)
                $"Missing {ttydExecutable}. Run scripts/setup-ttyd.ps1 first."
            ensure
                (File.Exists recorderExecutable)
                $"Missing {recorderExecutable}. Build the solution first."

            let fixture = createFixturePaths ()
            let productionManifest =
                Path.Combine(
                    TerminalHostLayout.defaultStateDirectory (),
                    TerminalHostLayout.ManifestFileName
                )

            let productionFingerprintBefore =
                fileFingerprint productionManifest
            let! productionPortOwnersBefore = portOwners 5000
            let! sourceStatusBefore = sourceStatus ()

            try
                initializeFixture fixture
                let serverPort =
                    Tests.TestUtils.getFreeTcpPorts 1 |> List.head

                ensure
                    (serverPort <> 5000)
                    "Isolated verifier selected production port 5000"

                let! server, api =
                    startServer fixture serverPort

                use client = new HttpClient()

                let! scenarioOutcome =
                    runScenario
                        client
                        fixture
                        server
                        api
                        serverPort
                    |> Async.Catch

                let! cleanupErrors =
                    cleanupRuntime
                        client
                        fixture
                        server
                        serverPort

                let productionFingerprintAfter =
                    fileFingerprint productionManifest
                let! productionPortOwnersAfter = portOwners 5000
                let! sourceStatusAfter = sourceStatus ()
                let fixtureDelete = deleteFixtureRoot fixture.Root

                let isolationErrors =
                    [ if
                          productionFingerprintAfter
                          <> productionFingerprintBefore
                      then
                          $"Production TerminalHost manifest changed: "
                          + $"{productionFingerprintBefore} -> "
                          + $"{productionFingerprintAfter}"

                      if
                          productionPortOwnersAfter
                          <> productionPortOwnersBefore
                      then
                          $"Production port 5000 owners changed: "
                          + $"{JsonSerializer.Serialize(productionPortOwnersBefore)} -> "
                          + $"{JsonSerializer.Serialize(productionPortOwnersAfter)}"

                      if sourceStatusAfter <> sourceStatusBefore then
                          $"Source status changed.{Environment.NewLine}"
                          + $"Before:{Environment.NewLine}{sourceStatusBefore}"
                          + $"After:{Environment.NewLine}{sourceStatusAfter}"

                      match fixtureDelete with
                      | Error error -> error
                      | Ok() -> () ]

                let allCleanupErrors =
                    List.append cleanupErrors isolationErrors

                emit
                    $"PRODUCTION_MANIFEST_FINGERPRINT={productionFingerprintAfter}"
                emit
                    $"PRODUCTION_PORT_5000_OWNERS_RAW={JsonSerializer.Serialize(productionPortOwnersAfter)}"
                emit $"SOURCE_STATUS_RAW={JsonSerializer.Serialize(sourceStatusAfter)}"
                emit $"FIXTURE_REMOVED={not (Directory.Exists fixture.Root)}"

                match scenarioOutcome, allCleanupErrors with
                | Choice1Of2 (), [] ->
                    emit "EMBEDDED_LAUNCH_E2E=PASS"
                | Choice2Of2 error, [] ->
                    return raise error
                | Choice1Of2 (), errors ->
                    return
                        raise (
                            InvalidOperationException(
                                "Cleanup or isolation checks failed:"
                                + Environment.NewLine
                                + String.concat Environment.NewLine errors
                            )
                        )
                | Choice2Of2 error, errors ->
                    return
                        raise (
                            InvalidOperationException(
                                $"Scenario failed: {error.Message}"
                                + Environment.NewLine
                                + "Cleanup or isolation checks also failed:"
                                + Environment.NewLine
                                + String.concat Environment.NewLine errors,
                                error
                            )
                        )
            with error ->
                match deleteFixtureRoot fixture.Root with
                | Ok() -> return raise error
                | Error cleanupError ->
                    return
                        raise (
                            InvalidOperationException(
                                $"{error.Message}{Environment.NewLine}{cleanupError}",
                                error
                            )
                        )
        }
        |> Async.StartAsTask
