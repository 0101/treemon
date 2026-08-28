module Server.TerminalHostProcess

open System
open System.Diagnostics
open System.IO
open Treemon.TerminalHosting

type internal Config =
    { HostExecutablePath: string
      HostStateDirectory: string
      TtydExecutablePath: string option
      ShellCommand: string
      AllowedOrigins: string list
      StartupTimeout: TimeSpan
      ControlRequestTimeout: TimeSpan
      ProbeInterval: TimeSpan
      LaunchHost: ProcessStartInfo -> Result<unit, string>
      ProcessIdentityMatches: int -> int64 -> Result<bool, string>
      ResolveProcessExecutable: int -> int64 -> Result<string, string>
      SendTerminalCommand: string -> string -> Async<Result<unit, string>> }

let internal processIdentityMatchesDefault pid processStartTimeUtcTicks =
    try
        use child = Process.GetProcessById pid

        if child.HasExited then
            Ok false
        else
            let startTicks =
                child.StartTime.ToUniversalTime().Ticks

            Ok(startTicks = processStartTimeUtcTicks)
    with
    | :? ArgumentException
    | :? InvalidOperationException -> Ok false
    | error ->
        Error $"Could not verify TerminalHost process identity: {error.Message}"

let internal resolveProcessExecutableDefault pid processStartTimeUtcTicks =
    try
        use child = Process.GetProcessById pid

        if child.HasExited then
            Error "The recorded TerminalHost process has exited"
        elif child.StartTime.ToUniversalTime().Ticks <> processStartTimeUtcTicks then
            Error "The recorded TerminalHost process identity no longer matches"
        else
            match child.MainModule |> Option.ofObj with
            | Some mainModule when not (String.IsNullOrWhiteSpace mainModule.FileName) ->
                Ok(Path.GetFullPath mainModule.FileName)
            | _ ->
                Error "Could not resolve the exact TerminalHost executable path"
    with error ->
        Error $"Could not resolve the exact TerminalHost executable path: {error.Message}"

let internal launchDetached (startInfo: ProcessStartInfo) =
    try
        if not (File.Exists startInfo.FileName) then
            Error $"TerminalHost executable was not found at '{startInfo.FileName}'"
        else
            match Process.Start startInfo |> Option.ofObj with
            | None -> Error "Windows did not start TerminalHost"
            | Some child ->
                child.Dispose()
                Ok()
    with error ->
        Error $"Could not start TerminalHost: {error.Message}"

let internal hostExecutableName = TerminalHostLayout.HostExecutableName

let internal resolveHostExecutable baseDirectory configuredPath =
    match configuredPath |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | Some configured -> Path.GetFullPath configured
    | None ->
        Path.Combine(baseDirectory, "terminal-host", hostExecutableName)
        |> Path.GetFullPath

let internal originsFor (serverOrigin: string) configuredOrigins =
    let origins =
        try
            let origin = Uri(serverOrigin, UriKind.Absolute)
            [ origin.GetLeftPart(UriPartial.Authority)
              $"{origin.Scheme}://localhost:{origin.Port}"
              $"{origin.Scheme}://127.0.0.1:{origin.Port}" ]
            @ configuredOrigins
        with _ ->
            serverOrigin :: configuredOrigins

    origins |> List.distinctBy _.ToUpperInvariant()

let internal hostStartInfo config =
    let workingDirectory =
        config.HostExecutablePath
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue AppContext.BaseDirectory

    let startInfo =
        ProcessStartInfo(
            FileName = config.HostExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        )

    [ "--state-dir"; config.HostStateDirectory; "--shell"; config.ShellCommand
      match config.TtydExecutablePath with
      | Some path ->
          "--ttyd"
          path
      | None -> ()
      for origin in config.AllowedOrigins do
          "--allowed-origin"
          origin ]
    |> List.iter startInfo.ArgumentList.Add

    startInfo

let internal startHostProcess config =
    try
        match config.TtydExecutablePath with
        | Some path when not (File.Exists path) ->
            Error $"ttyd was not found beside TerminalHost at '{path}'"
        | _ ->
            Directory.CreateDirectory config.HostStateDirectory
            |> ignore

            config.LaunchHost(hostStartInfo config)
    with error ->
        Error $"Could not prepare TerminalHost startup: {error.Message}"

let internal probeDelayMilliseconds config =
    config.ProbeInterval.TotalMilliseconds
    |> max 1.0
    |> min (float Int32.MaxValue)
    |> int

let internal defaultConfig allowedOrigins sendTerminalCommand =
    let layout = TerminalHostLayout.current ()
    let hostExecutable =
        Environment.GetEnvironmentVariable("TREEMON_TERMINAL_HOST_EXECUTABLE")
        |> Option.ofObj
        |> resolveHostExecutable AppContext.BaseDirectory

    { HostExecutablePath = hostExecutable
      HostStateDirectory = layout.StateDirectory
      TtydExecutablePath =
        TerminalHostLayout.adjacentTtydExecutablePath hostExecutable
      ShellCommand = "pwsh"
      AllowedOrigins = allowedOrigins
      StartupTimeout = TimeSpan.FromSeconds 30.0
      ControlRequestTimeout = TimeSpan.FromSeconds 10.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0
      LaunchHost = launchDetached
      ProcessIdentityMatches = processIdentityMatchesDefault
      ResolveProcessExecutable = resolveProcessExecutableDefault
      SendTerminalCommand = sendTerminalCommand }
