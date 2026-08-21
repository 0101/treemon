namespace TerminalHost

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Treemon.TerminalHosting

[<RequireQualifiedAccess>]
module Manifest =
    [<Literal>]
    let FileName = TerminalHostLayout.ManifestFileName

    let path stateDirectory =
        TerminalHostLayout.forStateDirectory stateDirectory
        |> _.ManifestPath

    let generateBearerToken () =
        RandomNumberGenerator.GetBytes 32
        |> Convert.ToBase64String
        |> _.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let readStagedExecutableVersion layout =
        try
            if not (Directory.Exists layout.StagingDirectory) then
                None
            else
                Directory.EnumerateDirectories layout.StagingDirectory
                |> Seq.choose (fun directory ->
                    let info = DirectoryInfo directory

                    let hasCompleteBundle =
                        layout.RequiredBundleFileNames
                        |> List.forall (fun name ->
                            let memberInfo =
                                FileInfo(Path.Combine(directory, name))

                            memberInfo.Exists
                            && (memberInfo.Attributes
                                &&& FileAttributes.ReparsePoint) = enum 0)

                    if
                        TerminalHostLayout.isValidVersionDirectoryName info.Name
                        && (info.Attributes &&& FileAttributes.ReparsePoint) = enum 0
                        && hasCompleteBundle
                    then
                        Some(info.LastWriteTimeUtc, info.Name)
                    else
                        None)
                |> Seq.sortByDescending id
                |> Seq.tryHead
                |> Option.map snd
        with _ ->
            None

    let private toJson manifest =
        let document = JsonObject()
        document["pid"] <- JsonValue.Create manifest.Identity.Pid

        document["processStartTimeUtcTicks"] <-
            JsonValue.Create manifest.Identity.ProcessStartTimeUtcTicks

        document["endpoint"] <- JsonValue.Create manifest.Identity.Endpoint
        document["bearerToken"] <- JsonValue.Create manifest.BearerToken
        document["hostVersion"] <- JsonValue.Create manifest.Identity.HostVersion

        document["controlApiVersion"] <-
            JsonValue.Create manifest.Identity.ControlApiVersion

        manifest.StagedExecutableVersion
        |> Option.iter (fun version ->
            document["stagedExecutableVersion"] <- JsonValue.Create version)

        document

    let write stateDirectory manifest =
        try
            Directory.CreateDirectory stateDirectory |> ignore
            let destination = path stateDirectory
            let temporary = Path.Combine(stateDirectory, $".host-{Guid.NewGuid():N}.tmp")
            let bytes = toJson manifest |> _.ToJsonString() |> System.Text.Encoding.UTF8.GetBytes

            try
                do
                    use stream =
                        new FileStream(
                            temporary,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            4_096,
                            FileOptions.WriteThrough
                        )

                    stream.Write(bytes, 0, bytes.Length)
                    stream.Flush(flushToDisk = true)

                File.Move(temporary, destination, overwrite = true)
                Ok()
            finally
                if File.Exists temporary then
                    File.Delete temporary
        with ex ->
            Error $"Could not publish the TerminalHost discovery manifest: {ex.Message}"

    let private isOwnedBy (identity: HostIdentity) manifestPath =
        try
            let info = FileInfo manifestPath

            if not info.Exists || info.Length <= 0L || info.Length > 65_536L then
                false
            else
                use document = JsonDocument.Parse(File.ReadAllBytes manifestPath)
                let root = document.RootElement
                // JsonElement.TryGetProperty requires byrefs; mutation is confined to this parser.
                let mutable pid = Unchecked.defaultof<JsonElement>
                let mutable startTime = Unchecked.defaultof<JsonElement>

                root.ValueKind = JsonValueKind.Object
                && root.TryGetProperty("pid", &pid)
                && pid.ValueKind = JsonValueKind.Number
                && pid.GetInt32() = identity.Pid
                && root.TryGetProperty("processStartTimeUtcTicks", &startTime)
                && startTime.ValueKind = JsonValueKind.Number
                && startTime.GetInt64() = identity.ProcessStartTimeUtcTicks
        with _ ->
            false

    let removeIfOwned stateDirectory identity =
        let manifestPath = path stateDirectory

        try
            if isOwnedBy identity manifestPath then
                File.Delete manifestPath
        with _ ->
            ()

    let internal monitorWithDelay
        waitForNextPoll
        stateDirectory
        layout
        identity
        bearerToken
        initialVersion
        (cancellationToken: CancellationToken)
        =
        let rec loop currentVersion =
            async {
                let! keepGoing = waitForNextPoll cancellationToken

                if keepGoing then
                    let discovered = readStagedExecutableVersion layout

                    let nextVersion =
                        if discovered = currentVersion then
                            currentVersion
                        else
                            match
                                write
                                    stateDirectory
                                    { Identity = identity
                                      BearerToken = bearerToken
                                      StagedExecutableVersion = discovered }
                            with
                            | Ok() -> discovered
                            | Error _ -> currentVersion

                    return! loop nextVersion
            }

        loop initialVersion

    let private waitForNextPoll (cancellationToken: CancellationToken) =
        async {
            try
                do!
                    Task.Delay(TimeSpan.FromSeconds 1.0, cancellationToken)
                    |> Async.AwaitTask

                return true
            with :? OperationCanceledException ->
                return false
        }

    let monitor
        stateDirectory
        layout
        identity
        bearerToken
        initialVersion
        cancellationToken
        =
        monitorWithDelay
            waitForNextPoll
            stateDirectory
            layout
            identity
            bearerToken
            initialVersion
            cancellationToken
        |> Async.StartAsTask
