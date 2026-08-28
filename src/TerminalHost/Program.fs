namespace TerminalHost

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Treemon.TerminalHosting

type private HostConfig =
    { Control: ControlApiConfig
      Layout: TerminalHostLayout
      TerminalLaunch: TerminalLaunchConfig }

[<RequireQualifiedAccess>]
module private HostConfig =
    let private parseOrigin value =
        // Uri.TryCreate is a byref-only framework parser; mutation stays at this boundary.
        let mutable uri = Unchecked.defaultof<Uri>
        let parsed = Uri.TryCreate(value, UriKind.Absolute, &uri)

        let loopbackHost =
            if not parsed then
                false
            else
                match IPAddress.TryParse uri.Host with
                | true, address -> IPAddress.IsLoopback address
                | false, _ ->
                    String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)

        if
            parsed
            && (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps)
            && loopbackHost
            && uri.AbsolutePath = "/"
            && String.IsNullOrEmpty uri.Query
            && String.IsNullOrEmpty uri.Fragment
        then
            Ok(uri.GetLeftPart(UriPartial.Authority))
        else
            Error $"Invalid allowed origin '{value}'"

    let parse arguments =
        let initial =
            { Control =
                { Port = 0
                  AllowedOrigins = [] }
              Layout = TerminalHostLayout.current ()
              TerminalLaunch =
                { TtydExecutable =
                    Path.Combine(
                        AppContext.BaseDirectory,
                        TerminalHostLayout.TtydExecutableName
                    )
                  ShellCommand = "pwsh"
                  StartupTimeout = TimeSpan.FromSeconds 10.0 } }

        let rec collect config remaining =
            match remaining with
            | [] -> Ok config
            | "--port" :: value :: tail ->
                match Int32.TryParse value with
                | true, port when port >= 0 && port <= 65_535 ->
                    collect { config with Control.Port = port } tail
                | _ -> Error $"Invalid control port '{value}'"
            | "--state-dir" :: value :: tail when not (String.IsNullOrWhiteSpace value) ->
                collect
                    { config with
                        Layout =
                            TerminalHostLayout.forStateDirectory value }
                    tail
            | "--ttyd" :: value :: tail when not (String.IsNullOrWhiteSpace value) ->
                collect
                    { config with
                        TerminalLaunch.TtydExecutable = Path.GetFullPath value }
                    tail
            | "--shell" :: value :: tail when not (String.IsNullOrWhiteSpace value) ->
                collect { config with TerminalLaunch.ShellCommand = value } tail
            | "--allowed-origin" :: value :: tail ->
                match parseOrigin value with
                | Error error -> Error error
                | Ok origin ->
                    collect
                        { config with
                            Control.AllowedOrigins =
                                origin :: config.Control.AllowedOrigins }
                        tail
            | option :: _ -> Error $"Unknown or incomplete TerminalHost option '{option}'"

        try
            match collect initial (arguments |> Array.toList) with
            | Error error -> Error error
            | Ok config when not (OperatingSystem.IsWindows()) ->
                Error "TerminalHost requires Windows"
            | Ok config when not (File.Exists config.TerminalLaunch.TtydExecutable) ->
                Error
                    $"ttyd is not installed at '{config.TerminalLaunch.TtydExecutable}'. Run '.\\treemon.ps1 setup-ttyd'."
            | Ok config -> Ok config
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException as error ->
            Error $"Invalid TerminalHost path: {error.Message}"

[<RequireQualifiedAccess>]
module private HostRuntime =
    let run config =
        task {
            use currentProcess = Process.GetCurrentProcess()
            let hostPid = currentProcess.Id
            let processStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks
            let version =
                Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                |> Option.ofObj
                |> Option.map _.InformationalVersion
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultValue "1.0.0"
            let token = Manifest.generateBearerToken ()

            let registry =
                TerminalRegistry.create
                    (TerminalLauncher.start config.TerminalLaunch)
                    (TerminalProxy.start config.TerminalLaunch.StartupTimeout config.Control.AllowedOrigins token)

            let! control =
                ControlApi.start
                    config.Control
                    token
                    hostPid
                    processStartTimeUtcTicks
                    version
                    registry

            let identity =
                { Pid = hostPid
                  ProcessStartTimeUtcTicks = processStartTimeUtcTicks
                  Endpoint = control.Endpoint
                  HostVersion = version
                  ControlApiVersion = Protocol.ControlApiVersion }

            let manifest =
                { Identity = identity
                  BearerToken = token
                  StagedExecutableVersion =
                        Manifest.readStagedExecutableVersion config.Layout }

            match Manifest.write config.Layout.StateDirectory manifest with
            | Error error ->
                do! TerminalRegistry.shutdown registry |> Async.StartAsTask
                do! ControlApi.stop control
                return Error error
            | Ok() ->
                use monitorCancellation = new CancellationTokenSource()

                let monitor =
                    Manifest.monitor
                        config.Layout.StateDirectory
                        config.Layout
                        identity
                        token
                        manifest.StagedExecutableVersion
                        monitorCancellation.Token

                let! outcome =
                    task {
                        try
                            do! ControlApi.waitForShutdown control
                            return Ok()
                        with error ->
                            return Error error.Message
                    }

                monitorCancellation.Cancel()
                do! monitor
                do! TerminalRegistry.shutdown registry |> Async.StartAsTask
                do! ControlApi.stop control
                Manifest.removeIfOwned config.Layout.StateDirectory identity
                return outcome
        }

module Program =
    [<EntryPoint>]
    let main arguments =
        try
            match HostConfig.parse arguments with
            | Error error ->
                Console.Error.WriteLine(error)
                1
            | Ok config ->
                match HostRuntime.run config |> _.GetAwaiter().GetResult() with
                | Ok() -> 0
                | Error error ->
                    Console.Error.WriteLine(error)
                    1
        with error ->
            Console.Error.WriteLine($"TerminalHost failed: {error.Message}")
            1
