module Tests.TerminalSessionFixture

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open FsToolkit.ErrorHandling
open NUnit.Framework
open Shared
open Server.SessionManager

type private PowerShellProcessSnapshot =
    { ProcessId: int
      CreationTimeUtcTicks: int64
      CommandLine: string }

type TerminalTestEnvironment =
    { Agent: SessionAgent
      OriginalCwd: string
      FixtureRoot: string
      WorktreePath: WorktreePath }

let private processCreationTickTolerance = 10L

let private processQueryScript =
    [ "$processes = @(Get-CimInstance Win32_Process -Filter \"Name = 'pwsh.exe'\" | ForEach-Object {"
      "  [pscustomobject]@{"
      "    ProcessId = [int]$_.ProcessId"
      "    CreationTimeUtcTicks = [int64]$_.CreationDate.ToUniversalTime().Ticks"
      "    CommandLine = [string]$_.CommandLine"
      "  }"
      "})"
      "ConvertTo-Json -InputObject $processes -Compress" ]
    |> String.concat Environment.NewLine

let private parseProcessSnapshots (json: string) =
    try
        use document = JsonDocument.Parse(json)

        document.RootElement.EnumerateArray()
        |> Seq.map (fun (element: JsonElement) ->
            { ProcessId = element.GetProperty("ProcessId").GetInt32()
              CreationTimeUtcTicks = element.GetProperty("CreationTimeUtcTicks").GetInt64()
              CommandLine = element.GetProperty("CommandLine").GetString() })
        |> List.ofSeq
        |> Ok
    with
    | :? JsonException as ex -> Error $"Failed to parse PowerShell process list: {ex.Message}"
    | :? InvalidOperationException as ex -> Error $"Invalid PowerShell process list: {ex.Message}"

let private queryPowerShellProcesses () =
    async {
        let psi =
            ProcessStartInfo(
                "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            )

        [ "-NoProfile"; "-NonInteractive"; "-Command"; processQueryScript ]
        |> List.iter psi.ArgumentList.Add

        use proc = Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)

        try
            do! proc.WaitForExitAsync(timeout.Token) |> Async.AwaitTask
            let! output = stdout |> Async.AwaitTask
            let! error = stderr |> Async.AwaitTask

            return
                if proc.ExitCode = 0 then
                    parseProcessSnapshots output
                else
                    Error $"Failed to enumerate PowerShell processes: {error.Trim()}"
        with :? OperationCanceledException ->
            if not proc.HasExited then
                proc.Kill(entireProcessTree = true)

            do! proc.WaitForExitAsync() |> Async.AwaitTask
            let! _ = stdout |> Async.AwaitTask
            let! _ = stderr |> Async.AwaitTask
            return Error "Timed out while enumerating PowerShell processes"
    }

let private encodedCommandLine =
    Regex(
        @"^\s*(?:""[^""]*pwsh(?:\.exe)?""|[^\s""]*pwsh(?:\.exe)?)\s+(?:(?:-NoProfile|-NoLogo|-NonInteractive|-NoExit)\s+)*-EncodedCommand\s+([A-Za-z0-9+/]+={0,2})\s*$",
        RegexOptions.IgnoreCase
    )

let internal isOwnedPowerShellCommand (worktreePath: string) (commandLine: string) =
    let nativePath = worktreePath.Replace('/', Path.DirectorySeparatorChar)
    let expectedScriptPrefix = buildScript nativePath
    let matched = encodedCommandLine.Match(commandLine)

    if not matched.Success then
        false
    else
        try
            matched.Groups[1].Value
            |> Convert.FromBase64String
            |> Text.Encoding.Unicode.GetString
            |> _.StartsWith(expectedScriptPrefix, StringComparison.OrdinalIgnoreCase)
        with :? FormatException ->
            false

let private tryOpenOwnedProcess worktreePath snapshot =
    if not (isOwnedPowerShellCommand worktreePath snapshot.CommandLine) then
        Ok None
    else
        try
            let proc = Process.GetProcessById(snapshot.ProcessId)
            try
                proc.SafeHandle |> ignore
                proc.Refresh()

                let matchesSnapshot =
                    proc.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(proc.StartTime.ToUniversalTime().Ticks - snapshot.CreationTimeUtcTicks)
                       <= processCreationTickTolerance

                if matchesSnapshot then
                    Ok(Some proc)
                else
                    proc.Dispose()
                    Ok None
            with
            | :? InvalidOperationException ->
                proc.Dispose()
                Ok None
            | :? Win32Exception as ex ->
                proc.Dispose()
                Error $"Failed to verify fixture PowerShell PID {snapshot.ProcessId}: {ex.Message}"
        with
        | :? ArgumentException -> Ok None

let private openOwnedProcesses worktreePath snapshots =
    let rec openAll opened remaining =
        match remaining with
        | [] -> Ok(List.rev opened)
        | snapshot :: rest ->
            match tryOpenOwnedProcess worktreePath snapshot with
            | Ok None -> openAll opened rest
            | Ok(Some proc) -> openAll (proc :: opened) rest
            | Error message ->
                opened |> List.iter _.Dispose()
                Error message

    openAll [] snapshots

let private ownedPowerShellProcesses worktreePath =
    async {
        let! snapshots = queryPowerShellProcesses ()
        return snapshots |> Result.bind (openOwnedProcesses worktreePath)
    }

let private stopOwnedProcess (proc: Process) =
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)

        if proc.WaitForExit(5_000) then
            Ok()
        else
            Error $"Fixture PowerShell PID {proc.Id} did not exit"
    with
    | :? InvalidOperationException -> Ok()
    | :? Win32Exception as ex -> Error $"Failed to stop fixture PowerShell PID {proc.Id}: {ex.Message}"

let internal stopOwnedPowerShellProcesses worktreePath =
    async {
        let! processes = ownedPowerShellProcesses worktreePath

        return
            match processes with
            | Error message -> Error message
            | Ok owned ->
                try
                    owned
                    |> List.traverseResultA stopOwnedProcess
                    |> Result.map ignore
                    |> Result.mapError (String.concat Environment.NewLine)
                finally
                    owned |> List.iter _.Dispose()
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

let internal ownedPowerShellProcessIds worktreePath =
    async {
        let! processes = ownedPowerShellProcesses worktreePath

        return
            processes
            |> Result.map (fun owned ->
                try
                    owned |> List.map _.Id
                finally
                    owned |> List.iter _.Dispose())
    }

let private closeTrackedSessions environment =
    async {
        do! requestSessionClosure environment.Agent
        let worktreePath = WorktreePath.value environment.WorktreePath
        let! fallback = stopOwnedPowerShellProcesses worktreePath
        do! requestSessionClosure environment.Agent
        let! remainingSessions = getActiveSessions environment.Agent
        let! remainingProcesses = ownedPowerShellProcessIds worktreePath

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

let private deleteFixtureRoot fixtureRoot =
    if Directory.Exists(fixtureRoot) then
        Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
        |> Seq.iter (fun file ->
            let attributes = File.GetAttributes(file)

            if attributes.HasFlag(FileAttributes.ReadOnly) then
                File.SetAttributes(file, attributes &&& ~~~FileAttributes.ReadOnly))

        Directory.Delete(fixtureRoot, recursive = true)

let create prefix prepareWorktree =
    let originalCwd = Environment.CurrentDirectory
    let fixtureRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}")
    let worktreePath = Path.Combine(fixtureRoot, "repo")

    try
        prepareWorktree worktreePath
        Environment.CurrentDirectory <- fixtureRoot

        { Agent = createAgent ()
          OriginalCwd = originalCwd
          FixtureRoot = fixtureRoot
          WorktreePath = WorktreePath worktreePath }
    with _ ->
        Environment.CurrentDirectory <- originalCwd

        try
            deleteFixtureRoot fixtureRoot
        with cleanupEx ->
            TestContext.Error.WriteLine($"SetUp cleanup failed: {cleanupEx.Message}")

        reraise ()

/// Flushes the agent mailbox while the fixture CWD is still in place. A request whose reply timed out
/// leaves its message queued, and `SessionManager` persists `data/sessions.json` relative to the
/// process CWD, so a late `Kill` would otherwise write into the restored directory. The mailbox is
/// FIFO: the first reply proves every earlier message and its persist finished, and the second sees
/// an already-validated map, so it cannot trigger a further write.
let private drainAgent environment =
    async {
        do! getActiveSessions environment.Agent |> Async.Ignore
        do! getActiveSessions environment.Agent |> Async.Ignore
    }

let cleanup environment =
    let sessionCleanup =
        try
            Async.RunSynchronously(closeTrackedSessions environment, timeout = 60_000)
        with ex ->
            Error ex.Message

    let agentDrain =
        try
            Async.RunSynchronously(drainAgent environment, timeout = 30_000)
            Ok()
        with ex ->
            Error ex.Message

    Environment.CurrentDirectory <- environment.OriginalCwd

    let directoryCleanup =
        try
            deleteFixtureRoot environment.FixtureRoot
            Ok()
        with ex ->
            Error ex.Message

    [ sessionCleanup |> Result.mapError (fun error -> $"Session cleanup failed: {error}")
      agentDrain |> Result.mapError (fun error -> $"Agent drain failed: {error}")
      directoryCleanup |> Result.mapError (fun error -> $"Fixture cleanup failed: {error}") ]
    |> List.sequenceResultA
    |> Result.map ignore
    |> Result.mapError (String.concat Environment.NewLine)
