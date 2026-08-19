module Server.EmbeddedTerminal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Shared

type internal Config =
    { NodeExecutable: string
      HostScriptPath: string
      SupervisorScriptPath: string
      ProcessIdentityHelperPath: string
      WebSocketPackagePath: string
      HostStateDirectory: string
      TtydExecutablePath: string
      TtydExpectedHash: string option
      ShellCommand: string
      StartupTimeout: TimeSpan
      ControlRequestTimeout: TimeSpan
      ProbeInterval: TimeSpan
      ReservationRenewalInterval: TimeSpan }

type internal RuntimeBundleIdentity =
    { Version: int
      BundleHash: string
      HostScriptHash: string
      SupervisorScriptHash: string
      ProcessIdentityHelperHash: string
      TtydExecutableHash: string option
      WebSocketPackageHash: string option }

type internal RuntimeBundle =
    { Identity: RuntimeBundleIdentity
      Directory: string }

type private RuntimeAsset =
    { Role: string
      Name: string
      Content: byte array
      Hash: string }

[<RequireQualifiedAccess>]
type internal GenerationCompactionStage =
    | BeforeRename
    | AfterRename
    | BeforeClaimDeletion
    | AfterClaimDeletion
    | DuringWitnessCleanup

type private HostIdentity =
    { Generation: string
      Pid: int
      ProcessStartTicks: int64
      ProcessStartExact: bool
      KernelOwnership: bool }

type private HostConnection =
    { Version: int
      Generation: string
      Pid: int
      ProcessStartTicks: int64
      ProcessStartExact: bool
      KernelOwnership: bool
      ControlPort: int
      ControlToken: string
      StartedAt: string
      RuntimeBundle: RuntimeBundleIdentity option
      SupervisorProtocolGeneration: int option
      Capabilities: Set<string> }

type private SupervisorIdentity =
    { Pid: int
      ProcessStartTicks: int64 }

type private SupervisorTrustState =
    | LegacyUntrusted
    | InProgress
    | Quarantined
    | TrustedEmpty

type private GenerationSessionEvidence =
    { SessionId: string
      WorktreePath: WorktreePath
      WitnessTokenHash: string
      SupervisorPid: int option
      SupervisorStartTicks: int64 option
      TrustState: SupervisorTrustState
      SupervisorExited: bool
      SupervisorExitCode: int option
      SupervisorExitSignal: string option
      SupervisorOutputClosed: bool }

type private GenerationEvidence =
    { Path: string
      Serialized: string
      RecordVersion: int
      HostProtocolVersion: int
      RuntimeBundle: RuntimeBundleIdentity option
      SupervisorProtocolGeneration: int option
      Capabilities: Set<string>
      Identity: HostIdentity
      SessionsUnknown: bool
      Sessions: GenerationSessionEvidence list }

type private EmptyWitness =
    { Generation: string
      WorktreePath: WorktreePath
      SessionId: string
      Supervisor: SupervisorIdentity
      Nonce: string }

type private HostSession =
    { Id: string
      Supervisor: SupervisorIdentity option
      Tab: EmbeddedTerminalTab }

type private PriorGenerationBoundary =
    | KnownSupervisor of SupervisorIdentity
    | MissingSupervisor

type private Reservation =
    { Id: string
      Sessions: HostSession list }

type private CleanupLease =
    { Renew:
        CancellationToken ->
            Async<Result<unit, string>>
      Release: unit -> Async<Result<unit, string>> }

type private CleanupReservation =
    { Lease: CleanupLease
      WorktreeLock: IDisposable }

type private LockAcquisitionToken =
    | LockAcquisitionToken of Guid

type private PendingLockRequest =
    | PendingStart of
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | PendingCleanup of
        AsyncReplyChannel<Result<CleanupReservation, string>>

type private PendingLockAcquisition =
    { WorktreePath: WorktreePath
      Cancellation: CancellationToken
      Registration: CancellationTokenRegistration
      Request: PendingLockRequest }

type private ReservedOperationOutcome =
    | ReservedResult of Result<unit, string>
    | ReservedCancelled of OperationCanceledException

type private ManagerState =
    { LastSnapshot: EmbeddedTerminalSnapshot
      AnnouncedHost: HostIdentity option
      KnownHost: HostIdentity option
      PriorGenerationOwners: Map<string, HostIdentity list>
      KnownSessionSupervisors: Map<string, SupervisorIdentity>
      PriorGenerationBoundaries:
        Map<string, PriorGenerationBoundary list>
      PendingLocks:
        Map<LockAcquisitionToken, PendingLockAcquisition> }

type private Message =
    | Start of
        LockAcquisitionToken *
        CancellationToken *
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | Get of AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | Close of WorktreePath * AsyncReplyChannel<EmbeddedTerminalSnapshot>
    | CloseStrict of
        WorktreePath *
        AsyncReplyChannel<Result<EmbeddedTerminalSnapshot, string>>
    | ReserveCleanup of
        LockAcquisitionToken *
        CancellationToken *
        WorktreePath *
        AsyncReplyChannel<Result<CleanupReservation, string>>
    | LockAcquired of
        LockAcquisitionToken *
        Result<IDisposable, string>
    | CancelLockAcquisition of LockAcquisitionToken
    | ShutdownHost of AsyncReplyChannel<Result<unit, string>>

type Manager = private Manager of MailboxProcessor<Message>

let private hostProtocolVersion = 3
let private generationRecordVersion = 2
let private previousGenerationRecordVersion = 1
let private supervisorProtocolGeneration = 2
let private maximumGenerationRecords = 64
let private maximumUnreferencedRuntimeBundles = 8
let private legacyRuntimeBundleVersion = 1
let private runtimeBundleVersion = 2
let private webSocketPackageVersion = "8.21.3"
let private pinnedTtydSha256 =
    "e33a27501b10b96981335bcba938b1145c7f52551a343e72160f00ab71832b37"

let private legacyRuntimeCapabilities =
    set
        [ "immutable-runtime-bundle-v1"
          "strict-evidence-paths-v1"
          "trusted-empty-supervisor-v1" ]

let private runtimeCapabilities =
    legacyRuntimeCapabilities
    |> Set.add "immutable-executable-dependencies-v1"

let private runtimeBundleCapabilitiesMatch bundleVersion capabilities =
    match bundleVersion with
    | version when version = legacyRuntimeBundleVersion ->
        capabilities = legacyRuntimeCapabilities
    | version when version = runtimeBundleVersion ->
        capabilities = runtimeCapabilities
    | _ -> false

let private webSocketRuntimeFiles =
    [ "LICENSE"
      "package.json"
      "wrapper.mjs"
      "lib/buffer-util.js"
      "lib/constants.js"
      "lib/event-target.js"
      "lib/extension.js"
      "lib/limiter.js"
      "lib/permessage-deflate.js"
      "lib/receiver.js"
      "lib/sender.js"
      "lib/stream.js"
      "lib/subprotocol.js"
      "lib/validation.js"
      "lib/websocket-server.js"
      "lib/websocket.js" ]

let private optionalDependencyGuardContent =
    Encoding.UTF8.GetBytes(
        "'use strict';\nthrow new Error('Optional native WebSocket dependency is disabled in the immutable terminal runtime');\n"
    )

let private optionalDependencyGuardPackage =
    Encoding.UTF8.GetBytes(
        "{\"private\":true,\"main\":\"index.js\"}\n"
    )

let private validGeneration (value: string) =
    not (String.IsNullOrEmpty value)
    && value.Length >= 1
    && value.Length <= 128
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '_'
           || character = '-')

let private kernelOwnershipError =
    "The durable terminal host predates kernel-enforced Job Object ownership; Treemon cannot start a terminal or authorize cleanup for that generation"

let private httpClient =
    new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

let private defaultConfig () =
    let root = Directory.GetCurrentDirectory()
    let deployedScripts =
        Path.Combine(AppContext.BaseDirectory, "scripts")

    let runtimeScript name =
        let deployed =
            Path.Combine(deployedScripts, name)

        if File.Exists deployed then
            deployed
        else
            Path.Combine(root, "scripts", name)

    let deployedWebSocket =
        Path.Combine(
            deployedScripts,
            "node_modules",
            "ws"
        )

    let deployedTtyd =
        Path.Combine(deployedScripts, "ttyd.exe")

    let stateDirectory =
        Environment.GetEnvironmentVariable("TREEMON_TERMINAL_STATE_DIR")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue (Path.Combine(root, ".agents", "durable-terminal"))

    { NodeExecutable = "node"
      HostScriptPath =
        runtimeScript "durable-terminal-host.mjs"
      SupervisorScriptPath =
        runtimeScript "terminal-job-supervisor.ps1"
      ProcessIdentityHelperPath =
        runtimeScript "terminate-owned-process.ps1"
      WebSocketPackagePath =
        if Directory.Exists deployedWebSocket then
            deployedWebSocket
        else
            Path.Combine(root, "node_modules", "ws")
      HostStateDirectory = stateDirectory
      TtydExecutablePath =
        if File.Exists deployedTtyd then
            deployedTtyd
        else
            Path.Combine(root, ".tools", "ttyd", "1.7.7", "ttyd.exe")
      TtydExpectedHash = Some pinnedTtydSha256
      ShellCommand = "pwsh"
      StartupTimeout = TimeSpan.FromSeconds 30.0
      ControlRequestTimeout = TimeSpan.FromSeconds 30.0
      ProbeInterval = TimeSpan.FromMilliseconds 100.0
      ReservationRenewalInterval = TimeSpan.FromSeconds 30.0 }

let private pathComparison =
    if OperatingSystem.IsWindows() then
        StringComparison.OrdinalIgnoreCase
    else
        StringComparison.Ordinal

let private samePathText left right =
    String.Equals(
        Path.GetFullPath left
        |> Path.TrimEndingDirectorySeparator,
        Path.GetFullPath right
        |> Path.TrimEndingDirectorySeparator,
        pathComparison
    )

let private ensureNoReparsePoint root candidate =
    try
        let rootPath =
            Path.GetFullPath root
            |> Path.TrimEndingDirectorySeparator

        let candidatePath = Path.GetFullPath candidate
        let relative = Path.GetRelativePath(rootPath, candidatePath)

        let segments =
            relative.Split(
                [| Path.DirectorySeparatorChar
                   Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries
            )

        let paths =
            segments
            |> Array.scan (fun parent child ->
                Path.Combine(parent, child)) rootPath
            |> Array.toList
            |> fun descendants -> rootPath :: descendants

        match
            paths
            |> List.tryFind (fun path ->
                (File.Exists path || Directory.Exists path)
                && (File.GetAttributes path
                    &&& FileAttributes.ReparsePoint)
                   <> enum 0)
        with
        | Some _ ->
            Error
                "Durable terminal evidence path crosses a reparse point"
        | None -> Ok ()
    with ex ->
        Error
            $"Could not validate durable terminal evidence path containment: {ex.Message}"

let private containedPath root candidate =
    result {
        let rootPath =
            Path.GetFullPath root
            |> Path.TrimEndingDirectorySeparator

        let candidatePath = Path.GetFullPath candidate
        let relative = Path.GetRelativePath(rootPath, candidatePath)

        let segments =
            relative.Split(
                [| Path.DirectorySeparatorChar
                   Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries
            )

        if
            String.IsNullOrWhiteSpace relative
            || Path.IsPathRooted relative
            || relative = ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal
            )
            || segments
               |> Array.exists (fun segment ->
                   segment = "."
                   || segment = ".."
                   || segment.Contains(':'))
        then
            return!
                Error
                    "Durable terminal evidence path escaped its state directory"

        do! ensureNoReparsePoint rootPath candidatePath
        return candidatePath
    }

let private containedDirectChild root name =
    result {
        if
            String.IsNullOrWhiteSpace name
            || Path.IsPathRooted name
            || name = "."
            || name = ".."
            || name.Contains(':')
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || Path.GetFileName name <> name
        then
            return!
                Error
                    "Durable terminal evidence path contains an invalid child name"

        let! path =
            Path.Combine(root, name)
            |> containedPath root

        if
            not (
                samePathText
                    (Path.GetDirectoryName path)
                    root
            )
        then
            return!
                Error
                    "Durable terminal evidence path is not a direct child"

        return path
    }

let private canonicalWorktreePath =
    WorktreePath.value >> PathUtils.toWorktreePath

let private isPath path (tab: EmbeddedTerminalTab) =
    Shared.PathUtils.pathEquals
        (WorktreePath.value path)
        (WorktreePath.value tab.Worktree)

let private isInterrupted tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Interrupted _ -> true
    | _ -> false

let private isHostOwned tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Starting
    | EmbeddedTerminalLifecycle.Running _ -> true
    | _ -> false

let private isFailed tab =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Failed _ -> true
    | _ -> false

let private worktreeKey =
    WorktreePath.value >> PathUtils.normalizePath

let private withoutPath path snapshot =
    { Tabs = snapshot.Tabs |> List.filter (isPath path >> not) }

let private mergeSnapshotWith
    replacements
    previous
    current
    =
    let replacementFor tab =
        current.Tabs |> List.tryFind (isPath tab.Worktree)

    let retained =
        previous.Tabs
        |> List.choose (fun tab ->
            match replacementFor tab, tab.Lifecycle with
            | Some replacement, EmbeddedTerminalLifecycle.Interrupted _
                when replacements
                     |> Set.contains (worktreeKey tab.Worktree) ->
                Some replacement
            | _, EmbeddedTerminalLifecycle.Interrupted _ -> Some tab
            | Some replacement, _ -> Some replacement
            | None, _ -> None)

    let appended =
        current.Tabs
        |> List.filter (fun tab ->
            retained
            |> List.exists (isPath tab.Worktree)
            |> not)

    { Tabs =
        retained @ appended
        |> List.fold (fun tabs tab ->
            if tabs |> List.exists (isPath tab.Worktree) then
                tabs
            else
                tabs @ [ tab ]) [] }

let private mergeSnapshot =
    mergeSnapshotWith Set.empty

let private mergeSnapshotReplacing path =
    mergeSnapshotWith (Set.singleton (worktreeKey path))

let private withFailure path error snapshot =
    match snapshot.Tabs |> List.tryFind (isPath path) with
    | Some _ ->
        { Tabs =
            snapshot.Tabs
            |> List.map (fun tab ->
                if isPath path tab then
                    { tab with
                        Lifecycle = EmbeddedTerminalLifecycle.Failed error }
                else
                    tab) }
    | None ->
        { Tabs =
            snapshot.Tabs
            @ [ { Worktree = path
                  Lifecycle = EmbeddedTerminalLifecycle.Failed error } ] }

let private withHostFailure error snapshot =
    { Tabs =
        snapshot.Tabs
        |> List.map (fun tab ->
            { tab with
                Lifecycle =
                    EmbeddedTerminalLifecycle.Interrupted(
                        $"Durable terminal host unavailable: {error}"
                    ) }) }

let private interruptForHostReplacement snapshot =
    { Tabs =
        snapshot.Tabs
        |> List.map (fun tab ->
            if isHostOwned tab then
                    { tab with
                        Lifecycle =
                            EmbeddedTerminalLifecycle.Interrupted(
                                "Durable terminal host generation changed; the prior Job Object boundary must be confirmed stopped before cleanup can be inferred"
                            ) }
            else
                    tab) }

let private tryProperty (name: string) (element: JsonElement) =
    element.EnumerateObject()
    |> Seq.tryFind _.NameEquals(name)
    |> Option.map _.Value

let private requiredString name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String ->
        value.GetString()
        |> Option.ofObj
        |> Result.requireSome $"Missing '{name}'"
    | _ -> Error $"Missing '{name}'"

let private optionalString name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.String ->
        value.GetString() |> Option.ofObj
    | _ -> None

let private requiredInt name element =
    match tryProperty name element with
    | Some value ->
        match value.TryGetInt32() with
        | true, result -> Ok result
        | false, _ -> Error $"Invalid '{name}'"
    | None -> Error $"Missing '{name}'"

let private requiredBool name element =
    match tryProperty name element with
    | Some value when value.ValueKind = JsonValueKind.True -> Ok true
    | Some value when value.ValueKind = JsonValueKind.False -> Ok false
    | _ -> Error $"Missing or invalid '{name}'"

let private optionalBool name element =
    match tryProperty name element with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
    | Some value when value.ValueKind = JsonValueKind.True -> Ok(Some true)
    | Some value when value.ValueKind = JsonValueKind.False -> Ok(Some false)
    | _ -> Error $"Invalid '{name}'"

let private optionalInt name element =
    match tryProperty name element with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
    | Some value ->
        match value.TryGetInt32() with
        | true, result -> Ok(Some result)
        | false, _ -> Error $"Invalid '{name}'"

let private requiredInt64String name element =
    result {
        let! text = requiredString name element

        match Int64.TryParse text with
        | true, value when value > 0L -> return value
        | _ -> return! Error $"Invalid '{name}'"
    }

let private optionalInt64String name element =
    match tryProperty name element with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
    | Some _ -> requiredInt64String name element |> Result.map Some

let private validBoundedToken minimum maximum (value: string) =
    value.Length >= minimum
    && value.Length <= maximum
    && value
       |> Seq.forall (fun character ->
           Char.IsAsciiLetterOrDigit character
           || character = '_'
           || character = '-')

let private validSha256Hex (value: string) =
    not (String.IsNullOrEmpty value)
    && value.Length = 64
    && value
       |> Seq.forall Uri.IsHexDigit

let private requiredStringSet name element =
    match tryProperty name element with
    | Some values when values.ValueKind = JsonValueKind.Array ->
        values.EnumerateArray()
        |> Seq.map (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString()
                |> Option.ofObj
                |> Result.requireSome $"Invalid '{name}'"
            else
                Error $"Invalid '{name}'")
        |> Seq.toList
        |> List.sequenceResultM
        |> Result.bind (fun entries ->
            if
                entries
                |> List.exists String.IsNullOrWhiteSpace
                || (entries |> Set.ofList |> Set.count)
                   <> List.length entries
            then
                Error $"Invalid '{name}'"
            else
                Ok(Set.ofList entries))
    | _ -> Error $"Missing or invalid '{name}'"

let private parseRuntimeBundleIdentity root : Result<RuntimeBundleIdentity, string> =
    result {
        let! version =
            optionalInt "runtimeBundleVersion" root
            |> Result.map (
                Option.defaultValue
                    legacyRuntimeBundleVersion
            )

        let! bundleHash = requiredString "bundleHash" root
        let! hostScriptHash = requiredString "hostScriptHash" root
        let! supervisorScriptHash =
            requiredString "supervisorScriptHash" root
        let! processIdentityHelperHash =
            requiredString "processIdentityHelperHash" root
        let! ttydExecutableHash,
             webSocketPackageHash =
            if version = legacyRuntimeBundleVersion then
                Ok(None, None)
            elif version = runtimeBundleVersion then
                result {
                    let! ttyd =
                        requiredString
                            "ttydExecutableHash"
                            root

                    let! webSocket =
                        requiredString
                            "webSocketPackageHash"
                            root

                    return Some ttyd, Some webSocket
                }
            else
                Error
                    "Durable terminal runtime bundle version is incompatible"

        if
            [ bundleHash
              hostScriptHash
              supervisorScriptHash
              processIdentityHelperHash
              yield!
                  ttydExecutableHash
                  |> Option.toList
              yield!
                  webSocketPackageHash
                  |> Option.toList ]
            |> List.exists (validSha256Hex >> not)
        then
            return!
                Error
                    "Durable terminal runtime bundle contains an invalid content hash"

        return
            { Version = version
              BundleHash = bundleHash.ToLowerInvariant()
              HostScriptHash =
                hostScriptHash.ToLowerInvariant()
              SupervisorScriptHash =
                supervisorScriptHash.ToLowerInvariant()
              ProcessIdentityHelperHash =
                processIdentityHelperHash.ToLowerInvariant()
              TtydExecutableHash =
                ttydExecutableHash
                |> Option.map _.ToLowerInvariant()
              WebSocketPackageHash =
                webSocketPackageHash
                |> Option.map _.ToLowerInvariant() }
    }

let private parseHostConnection (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if
                version <> 1
                && version <> 2
                && version <> hostProtocolVersion
            then
                return! Error $"Unsupported durable terminal host protocol version {version}"

            let! pid = requiredInt "pid" root
            let! controlPort = requiredInt "controlPort" root
            let! controlToken = requiredString "controlToken" root
            let! startedAt = requiredString "startedAt" root
            let! generation, processStartTicks, processStartExact =
                if version >= 2 then
                    result {
                        let! generation = requiredString "generation" root
                        let! processStartTicks =
                            requiredInt64String "processStartTicks" root
                        let! processStartExact =
                            requiredBool "processStartExact" root

                        return
                            generation,
                            processStartTicks,
                            processStartExact
                    }
                else
                    match DateTimeOffset.TryParse startedAt with
                    | true, started when started.UtcTicks > 0L ->
                        Ok(
                            $"legacy-v1-{pid}-{started.UtcTicks}",
                            started.UtcTicks,
                            false
                        )
                    | _ ->
                        Error
                            "Invalid protocol-1 durable terminal host start identity"

            let kernelOwnership =
                optionalString "ownershipBoundary" root
                |> Option.contains "windows-job-v1"

            if pid <= 0 then
                return! Error "Invalid durable terminal host PID"

            if not (validGeneration generation) then
                return! Error "Invalid durable terminal host generation"

            if controlPort <= 0 || controlPort > 65535 || controlPort = 5000 then
                return! Error "Invalid durable terminal host control port"

            if String.IsNullOrWhiteSpace controlToken then
                return! Error "Invalid durable terminal host control token"

            let! runtimeBundle, supervisorGeneration, capabilities =
                if version = hostProtocolVersion then
                    result {
                        let! bundle =
                            parseRuntimeBundleIdentity root
                        let! generation =
                            requiredInt
                                "supervisorProtocolGeneration"
                                root
                        let! capabilities =
                            requiredStringSet "capabilities" root

                        if
                            generation
                            <> supervisorProtocolGeneration
                            || not (
                                runtimeBundleCapabilitiesMatch
                                    bundle.Version
                                    capabilities
                            )
                        then
                            return!
                                Error
                                    "Durable terminal host runtime capabilities are incompatible"

                        return
                            Some bundle,
                            Some generation,
                            capabilities
                    }
                else
                    Ok(None, None, Set.empty)

            return
                { Version = version
                  Generation = generation
                  Pid = pid
                  ProcessStartTicks = processStartTicks
                  ProcessStartExact = processStartExact
                  KernelOwnership = kernelOwnership
                  ControlPort = controlPort
                  ControlToken = controlToken
                  StartedAt = startedAt
                  RuntimeBundle = runtimeBundle
                  SupervisorProtocolGeneration =
                    supervisorGeneration
                  Capabilities = capabilities }
        }
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host state: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host state: {ex.Message}"

let private parseGenerationSession recordVersion element =
    result {
        let! sessionId = requiredString "sessionId" element
        let! worktreePath = requiredString "worktreePath" element
        let! witnessTokenHash =
            requiredString "witnessTokenHash" element
        let! supervisorPid = optionalInt "supervisorPid" element
        let! supervisorStartTicks =
            optionalInt64String
                "supervisorStartTimeUtcTicks"
                element
        let! trustState,
             supervisorExited,
             supervisorExitCode,
             supervisorExitSignal,
             supervisorOutputClosed =
            if recordVersion = previousGenerationRecordVersion then
                requiredBool "protocolFailure" element
                |> Result.map (fun protocolFailure ->
                    (if protocolFailure then
                         Quarantined
                     else
                         LegacyUntrusted),
                    false,
                    None,
                    None,
                    false)
            else
                result {
                    let! state =
                        requiredString "supervisorState" element

                    let! exited =
                        requiredBool "supervisorExited" element

                    let! exitCode =
                        optionalInt "supervisorExitCode" element

                    let exitSignal =
                        optionalString
                            "supervisorExitSignal"
                            element

                    let! outputClosed =
                        requiredBool
                            "supervisorOutputClosed"
                            element

                    let! parsedState =
                        match state with
                        | "in-progress" -> Ok InProgress
                        | "quarantined" -> Ok Quarantined
                        | "trusted-empty" -> Ok TrustedEmpty
                        | _ ->
                            Error
                                "Durable terminal generation record has an invalid supervisor trust state"

                    return
                        parsedState,
                        exited,
                        exitCode,
                        exitSignal,
                        outputClosed
                }

        if not (validBoundedToken 16 128 sessionId) then
            return!
                Error
                    "Durable terminal generation record has an invalid session identity"

        if not (validSha256Hex witnessTokenHash) then
            return!
                Error
                    "Durable terminal generation record has an invalid witness-token commitment"

        if not (Path.IsPathFullyQualified worktreePath) then
            return!
                Error
                    "Durable terminal generation record has a non-canonical worktree path"

        match supervisorPid, supervisorStartTicks with
        | Some pid, _ when pid <= 0 ->
            return!
                Error
                    "Durable terminal generation record has an invalid supervisor PID"
        | None, Some _ ->
            return!
                Error
                    "Durable terminal generation record has a supervisor start identity without a PID"
        | _ -> ()

        match trustState with
        | TrustedEmpty
            when supervisorPid.IsNone
                 || supervisorStartTicks.IsNone
                 || not supervisorExited
                 || supervisorExitCode <> Some 0
                 || supervisorExitSignal.IsSome
                 || not supervisorOutputClosed ->
            return!
                Error
                    "Durable terminal generation record has an incomplete trusted-empty supervisor state"
        | InProgress
        | Quarantined
        | LegacyUntrusted
            when supervisorExited
                 || supervisorExitCode.IsSome
                 || supervisorExitSignal.IsSome
                 || supervisorOutputClosed ->
            return!
                Error
                    "Durable terminal generation record has contradictory supervisor state"
        | _ -> ()

        return
            { SessionId = sessionId
              WorktreePath =
                PathUtils.toWorktreePath worktreePath
              WitnessTokenHash =
                witnessTokenHash.ToLowerInvariant()
              SupervisorPid = supervisorPid
              SupervisorStartTicks = supervisorStartTicks
              TrustState = trustState
              SupervisorExited = supervisorExited
              SupervisorExitCode = supervisorExitCode
              SupervisorExitSignal = supervisorExitSignal
              SupervisorOutputClosed = supervisorOutputClosed }
    }

let private parseGenerationEvidence
    (path: string)
    (text: string)
    =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if
                version <> previousGenerationRecordVersion
                && version <> generationRecordVersion
            then
                return!
                    Error
                        $"Unsupported terminal generation record version {version}"

            let! protocolVersion =
                requiredInt "hostProtocolVersion" root

            let! generation = requiredString "generation" root
            let! hostPid = requiredInt "hostPid" root
            let! hostStartTicks =
                requiredInt64String
                    "hostProcessStartTicks"
                    root
            let! hostStartExact =
                requiredBool "hostProcessStartExact" root
            let! sessionsUnknown =
                optionalBool "sessionsUnknown" root
                |> Result.map (Option.defaultValue false)

            if
                version = previousGenerationRecordVersion
                && protocolVersion >= hostProtocolVersion
                && not sessionsUnknown
            then
                return!
                    Error
                        "Legacy terminal generation record cannot claim the current host protocol"

            let! runtimeBundle,
                 supervisorGeneration,
                 capabilities =
                if version = generationRecordVersion then
                    result {
                        if protocolVersion <> hostProtocolVersion then
                            return!
                                Error
                                    "Current terminal generation record has an incompatible host protocol"

                        let! bundle =
                            parseRuntimeBundleIdentity root

                        let! generation =
                            requiredInt
                                "supervisorProtocolGeneration"
                                root

                        let! capabilities =
                            requiredStringSet "capabilities" root

                        if
                            generation
                            <> supervisorProtocolGeneration
                            || not (
                                runtimeBundleCapabilitiesMatch
                                    bundle.Version
                                    capabilities
                            )
                        then
                            return!
                                Error
                                    "Terminal generation runtime capabilities are incompatible"

                        return
                            Some bundle,
                            Some generation,
                            capabilities
                    }
                else
                    Ok(None, None, Set.empty)

            let filename =
                Path.GetFileName path

            let filenameGeneration =
                let marker = filename.IndexOf(".json", StringComparison.Ordinal)

                if marker > 0 then
                    filename.Substring(0, marker)
                else
                    ""

            if
                not (validGeneration generation)
                || hostPid <= 0
                || not (
                    String.Equals(
                        filenameGeneration,
                        generation,
                        StringComparison.Ordinal
                    )
                )
            then
                return!
                    Error
                        "Durable terminal generation record has an invalid host identity"

            let kernelOwnership =
                optionalString "ownershipBoundary" root
                |> Option.contains "windows-job-v1"

            let! sessions =
                match tryProperty "sessions" root with
                | Some values
                    when values.ValueKind
                         = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.map (parseGenerationSession version)
                    |> Seq.toList
                    |> List.sequenceResultM
                | None when sessionsUnknown -> Ok []
                | _ ->
                    Error
                        "Durable terminal generation record omitted its session evidence"

            if
                sessions
                |> List.distinctBy _.SessionId
                |> List.length
                <> List.length sessions
            then
                return!
                    Error
                        "Durable terminal generation record contains duplicate session identities"

            return
                { Path = path
                  Serialized = text
                  RecordVersion = version
                  HostProtocolVersion = protocolVersion
                  RuntimeBundle = runtimeBundle
                  SupervisorProtocolGeneration =
                    supervisorGeneration
                  Capabilities = capabilities
                  Identity =
                    { Generation = generation
                      Pid = hostPid
                      ProcessStartTicks = hostStartTicks
                      ProcessStartExact = hostStartExact
                      KernelOwnership = kernelOwnership }
                  SessionsUnknown = sessionsUnknown
                  Sessions = sessions }
        }
    with
    | :? JsonException as ex ->
        Error
            $"Invalid durable terminal generation record: {ex.Message}"
    | ex ->
        Error
            $"Could not read durable terminal generation record: {ex.Message}"

let private parseEmptyWitness (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if version <> 1 then
                return!
                    Error
                        $"Unsupported terminal empty-witness version {version}"

            let! generation = requiredString "generation" root
            let! worktreePath = requiredString "worktreePath" root
            let! sessionId = requiredString "sessionId" root
            let! supervisorPid = requiredInt "supervisorPid" root
            let! supervisorStartTicks =
                requiredInt64String
                    "supervisorStartTimeUtcTicks"
                    root
            let! nonce = requiredString "nonce" root

            if
                not (validGeneration generation)
                || not (validBoundedToken 16 128 sessionId)
                || not (validBoundedToken 24 128 nonce)
                || supervisorPid <= 0
                || not (Path.IsPathFullyQualified worktreePath)
            then
                return!
                    Error
                        "Terminal empty witness has invalid ownership metadata"

            return
                { Generation = generation
                  WorktreePath =
                    PathUtils.toWorktreePath worktreePath
                  SessionId = sessionId
                  Supervisor =
                    { Pid = supervisorPid
                      ProcessStartTicks =
                        supervisorStartTicks }
                  Nonce = nonce }
        }
    with
    | :? JsonException as ex ->
        Error $"Invalid terminal empty witness: {ex.Message}"
    | ex ->
        Error $"Could not read terminal empty witness: {ex.Message}"

let private lifecycleFor element =
    result {
        let! lifecycle = requiredString "lifecycle" element

        match lifecycle with
        | "starting" -> return EmbeddedTerminalLifecycle.Starting
        | "running" ->
            let! endpoint = requiredString "endpoint" element
            return EmbeddedTerminalLifecycle.Running endpoint
        | "failed" ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    optionalString "error" element
                    |> Option.defaultValue "Durable terminal session failed"
                )
        | "closing" ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    "Durable terminal session is closing"
                )
        | unsupported ->
            return
                EmbeddedTerminalLifecycle.Failed(
                    $"Durable terminal host returned unsupported lifecycle '{unsupported}'"
                )
    }

let private supervisorIdentityFor element =
    let nonNullProperty name =
        tryProperty name element
        |> Option.filter (fun value ->
            value.ValueKind <> JsonValueKind.Null)

    match
        nonNullProperty "supervisorPid",
        nonNullProperty "supervisorStartTimeUtcTicks"
    with
    | None, None -> Ok None
    | Some _, Some _ ->
        result {
            let! pid = requiredInt "supervisorPid" element
            let! processStartTicks =
                requiredInt64String
                    "supervisorStartTimeUtcTicks"
                    element

            if pid <= 0 then
                return! Error "Invalid terminal supervisor PID"

            return
                Some
                    { Pid = pid
                      ProcessStartTicks = processStartTicks }
        }
    | _ ->
        Error
            "Durable terminal host returned incomplete supervisor identity"

let private parseHostSessions (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        match tryProperty "sessions" root with
        | Some sessions when sessions.ValueKind = JsonValueKind.Array ->
            sessions.EnumerateArray()
            |> Seq.map (fun session ->
                result {
                    let! id = requiredString "id" session
                    let! path = requiredString "worktreePath" session
                    let! lifecycle = lifecycleFor session
                    let! supervisor =
                        supervisorIdentityFor session

                    return
                        { Id = id
                          Supervisor = supervisor
                          Tab =
                            { Worktree = PathUtils.toWorktreePath path
                              Lifecycle = lifecycle } }
                })
            |> Seq.toList
            |> List.sequenceResultM
        | _ -> Error "Durable terminal host response omitted 'sessions'"
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host response: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host response: {ex.Message}"

let private parseReservation (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! reservation =
                match tryProperty "reservation" root with
                | Some value
                    when value.ValueKind
                         = JsonValueKind.Object ->
                    Ok value
                | _ ->
                    Error
                        "Durable terminal host response omitted 'reservation'"

            let! id = requiredString "id" reservation
            let! sessions = parseHostSessions text
            return { Id = id; Sessions = sessions }
        }
    with
    | :? JsonException as ex ->
        Error
            $"Invalid durable terminal reservation response: {ex.Message}"
    | ex ->
        Error
            $"Could not read durable terminal reservation response: {ex.Message}"

let private snapshot sessions =
    { Tabs = sessions |> List.map _.Tab }

let private withKnownSessionSupervisors state sessions =
    let supervisors =
        sessions
        |> List.choose (fun session ->
            session.Supervisor
            |> Option.map (fun supervisor ->
                worktreeKey session.Tab.Worktree,
                supervisor))
        |> Map.ofList

    { state with
        KnownSessionSupervisors = supervisors }

let private statePath config =
    Path.Combine(config.HostStateDirectory, "host.json")

let private readHostConnection config =
    let path = statePath config

    if not (File.Exists path) then
        Ok None
    else
        try
            File.ReadAllText path
            |> parseHostConnection
            |> Result.map Some
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException ->
            Ok None
        | ex ->
            Error $"Could not read durable terminal host state: {ex.Message}"

let private hostIdentity (connection: HostConnection) =
    { Generation = connection.Generation
      Pid = connection.Pid
      ProcessStartTicks = connection.ProcessStartTicks
      ProcessStartExact = connection.ProcessStartExact
      KernelOwnership = connection.KernelOwnership }

let private sameHostIdentity (left: HostIdentity) (right: HostIdentity) =
    left.Generation = right.Generation
    && left.Pid = right.Pid
    && left.ProcessStartTicks = right.ProcessStartTicks
    && left.KernelOwnership = right.KernelOwnership

let private pidIsAlive pid =
    try
        use proc = Process.GetProcessById pid
        not proc.HasExited
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

let private currentProcessStartTicks () =
    use proc = Process.GetCurrentProcess()
    proc.StartTime.ToUniversalTime().Ticks

let private processIdentityMatchesValues
    pid
    processStartTicks
    processStartExact
    =
    try
        use proc = Process.GetProcessById pid

        if proc.HasExited then
            Ok false
        else
            let actual = proc.StartTime.ToUniversalTime().Ticks
            let difference =
                abs (actual - processStartTicks)

            if actual = processStartTicks then
                Ok true
            elif processStartExact then
                Ok false
            elif difference <= TimeSpan.FromSeconds(2.0).Ticks then
                Ok true
            else
                Ok false
    with
    | :? ArgumentException -> Ok false
    | :? InvalidOperationException -> Ok false
    | ex ->
        Error
            $"Could not verify durable terminal host PID {pid} start identity: {ex.Message}"

let private processIdentityMatches (connection: HostConnection) =
    processIdentityMatchesValues
        connection.Pid
        connection.ProcessStartTicks
        connection.ProcessStartExact

let private hostIdentityMatches (identity: HostIdentity) =
    processIdentityMatchesValues
        identity.Pid
        identity.ProcessStartTicks
        identity.ProcessStartExact

let private generationDirectory config =
    Path.Combine(
        config.HostStateDirectory,
        "terminal-generations"
    )

let private generationRecordPath config generation =
    if validGeneration generation then
        containedDirectChild
            (generationDirectory config)
            $"{generation}.json"
    else
        Error "Invalid durable terminal host generation"

let private emptyWitnessDirectory config generation =
    result {
        if not (validGeneration generation) then
            return!
                Error
                    "Invalid durable terminal empty-witness generation"

        let root =
            Path.Combine(
                config.HostStateDirectory,
                "terminal-empty-witnesses"
            )

        return!
            containedDirectChild root generation
    }

let private emptyWitnessPath config generation sessionId =
    result {
        if not (validBoundedToken 16 128 sessionId) then
            return!
                Error
                    "Invalid durable terminal empty-witness session identity"

        let! directory =
            emptyWitnessDirectory config generation

        return!
            containedDirectChild
                directory
                $"{sessionId}.json"
    }

let private atomicWriteBytes (path: string) (content: byte array) =
    try
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore
        let temporaryPath =
            $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp"

        try
            do
                use stream =
                    new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough
                    )

                stream.Write(content, 0, content.Length)
                stream.Flush true

            File.Move(temporaryPath, path, true)
            Ok ()
        finally
            File.Delete temporaryPath
    with ex ->
        Error
            $"Could not persist durable terminal generation evidence: {ex.Message}"

let private sha256Hex (content: byte array) =
    content
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

let private webSocketPackageHash (assets: RuntimeAsset list) =
    assets
    |> List.filter _.Name.StartsWith(
        "node_modules/ws/",
        StringComparison.Ordinal
    )
    |> List.sortBy _.Name
    |> List.map (fun asset ->
        $"file:{asset.Name}:{asset.Hash}")
    |> String.concat "\n"
    |> fun text -> $"{text}\n"
    |> Encoding.UTF8.GetBytes
    |> sha256Hex

let private runtimeBundleHash (identity: RuntimeBundleIdentity) =
    let lines =
        [ $"bundle-version:{identity.Version}"
          $"host-protocol:{hostProtocolVersion}"
          $"supervisor-protocol:{supervisorProtocolGeneration}"
          yield!
              (if
                   identity.Version
                   = legacyRuntimeBundleVersion
               then
                   legacyRuntimeCapabilities
               else
                   runtimeCapabilities)
              |> Set.toList
              |> List.map (fun capability ->
                  $"capability:{capability}")
          $"file:host:durable-terminal-host.mjs:{identity.HostScriptHash}"
          $"file:supervisor:terminal-job-supervisor.ps1:{identity.SupervisorScriptHash}"
          $"file:processIdentityHelper:terminate-owned-process.ps1:{identity.ProcessIdentityHelperHash}"
          if identity.Version = runtimeBundleVersion then
              $"file:ttyd:ttyd.exe:{identity.TtydExecutableHash.Value}"
              $"package:ws:{webSocketPackageVersion}:{identity.WebSocketPackageHash.Value}" ]

    (String.concat "\n" lines) + "\n"
    |> Encoding.UTF8.GetBytes
    |> sha256Hex

let private runtimeBundleRoot config =
    Path.Combine(
        config.HostStateDirectory,
        "terminal-runtime-bundles"
    )

let private runtimeBundleDirectory config (bundleHash: string) =
    if validSha256Hex bundleHash then
        containedDirectChild
            (runtimeBundleRoot config)
            (bundleHash.ToLowerInvariant())
    else
        Error "Durable terminal runtime bundle hash is invalid"

let private readRuntimeAsset role name (path: string) =
    try
        if not (File.Exists path) then
            Error
                $"Durable terminal runtime file is missing at '{path}'"
        else
            let info = FileInfo path

            if
                info.Length > 16L * 1024L * 1024L
                || (info.Attributes
                    &&& FileAttributes.ReparsePoint)
                   <> enum 0
            then
                Error
                    $"Durable terminal runtime file is not a bounded regular file at '{path}'"
            else
                let content = File.ReadAllBytes path

                Ok
                    { Role = role
                      Name = name
                      Content = content
                      Hash = sha256Hex content }
    with ex ->
        Error
            $"Could not read durable terminal runtime file '{path}': {ex.Message}"

let private bundleRelativePath (root: string) (relativePath: string) =
    relativePath.Split('/')
    |> Array.fold (fun parent child ->
        Path.Combine(parent, child)) root

let private runtimeAssets (config: Config) =
    result {
        do!
            ensureNoReparsePoint
                config.WebSocketPackagePath
                config.WebSocketPackagePath

        let sources =
            [ ("host",
               "durable-terminal-host.mjs",
               config.HostScriptPath)
              ("supervisor",
               "terminal-job-supervisor.ps1",
               config.SupervisorScriptPath)
              ("processIdentityHelper",
               "terminate-owned-process.ps1",
               config.ProcessIdentityHelperPath)
              ("ttyd",
               "ttyd.exe",
               config.TtydExecutablePath)
              yield!
                  webSocketRuntimeFiles
                  |> List.map (fun relativePath ->
                      let bundleName =
                          $"node_modules/ws/{relativePath}"

                      $"ws:{relativePath}",
                      bundleName,
                      bundleRelativePath
                          config.WebSocketPackagePath
                          relativePath) ]

        let! sourceAssets =
            sources
            |> List.map (fun (role, name, path) ->
                readRuntimeAsset role name path)
            |> List.sequenceResultM

        match config.TtydExpectedHash with
        | Some expected
            when sourceAssets
                 |> List.find (fun asset ->
                     asset.Role = "ttyd")
                 |> _.Hash
                 <> expected ->
            return!
                Error
                    $"ttyd checksum mismatch. Expected {expected}; rerun '.\\treemon.ps1 setup-ttyd'."
        | _ -> ()

        let package =
            sourceAssets
            |> List.find (fun asset ->
                asset.Name = "node_modules/ws/package.json")

        let! packageVersion =
            try
                use document =
                    JsonDocument.Parse package.Content

                requiredString
                    "version"
                    document.RootElement
            with ex ->
                Error
                    $"Could not validate the durable terminal WebSocket package: {ex.Message}"

        if packageVersion <> webSocketPackageVersion then
            return!
                Error
                    $"Durable terminal WebSocket package must be exactly {webSocketPackageVersion}"

        let guardAssets =
            [ "bufferutil"; "utf-8-validate" ]
            |> List.collect (fun packageName ->
                [ { Role =
                        $"ws-optional-guard:{packageName}:package"
                    Name =
                        $"node_modules/ws/node_modules/{packageName}/package.json"
                    Content = optionalDependencyGuardPackage
                    Hash =
                        sha256Hex
                            optionalDependencyGuardPackage }
                  { Role =
                        $"ws-optional-guard:{packageName}:entry"
                    Name =
                        $"node_modules/ws/node_modules/{packageName}/index.js"
                    Content = optionalDependencyGuardContent
                    Hash =
                        sha256Hex
                            optionalDependencyGuardContent } ])

        return sourceAssets @ guardAssets
    }

let private identityForAssets (assets: RuntimeAsset list) =
    let hashFor role =
        assets
        |> List.find (fun asset -> asset.Role = role)
        |> _.Hash

    let provisional =
        { Version = runtimeBundleVersion
          BundleHash = ""
          HostScriptHash = hashFor "host"
          SupervisorScriptHash = hashFor "supervisor"
          ProcessIdentityHelperHash =
            hashFor "processIdentityHelper"
          TtydExecutableHash =
            Some(hashFor "ttyd")
          WebSocketPackageHash =
            Some(webSocketPackageHash assets) }

    { provisional with
        BundleHash = runtimeBundleHash provisional }

let private bundleManifestBytes
    (identity: RuntimeBundleIdentity)
    (assets: RuntimeAsset list)
    =
    JsonSerializer.SerializeToUtf8Bytes(
        {| version = runtimeBundleVersion
           runtimeBundleVersion = runtimeBundleVersion
           bundleHash = identity.BundleHash
           hostProtocolVersion = hostProtocolVersion
           hostScriptHash = identity.HostScriptHash
           supervisorScriptHash =
            identity.SupervisorScriptHash
           processIdentityHelperHash =
            identity.ProcessIdentityHelperHash
           ttydExecutableHash =
            identity.TtydExecutableHash.Value
           webSocketPackageHash =
            identity.WebSocketPackageHash.Value
           webSocketPackageVersion =
            webSocketPackageVersion
           supervisorProtocolGeneration =
            supervisorProtocolGeneration
           capabilities =
            runtimeCapabilities |> Set.toArray
           files =
            assets
            |> List.map (fun asset ->
                {| role = asset.Role
                   name = asset.Name
                   sha256 = asset.Hash |})
            |> List.toArray |}
    )

let private runtimeBundleFileLayout (version: int) =
    [ ("host", "durable-terminal-host.mjs")
      ("supervisor", "terminal-job-supervisor.ps1")
      ("processIdentityHelper",
       "terminate-owned-process.ps1")
      if version = runtimeBundleVersion then
          ("ttyd", "ttyd.exe")
          yield!
              webSocketRuntimeFiles
              |> List.map (fun relativePath ->
                  $"ws:{relativePath}",
                  $"node_modules/ws/{relativePath}")
          yield!
              [ "bufferutil"; "utf-8-validate" ]
              |> List.collect (fun packageName ->
                  [ ($"ws-optional-guard:{packageName}:package",
                     $"node_modules/ws/node_modules/{packageName}/package.json")
                    ($"ws-optional-guard:{packageName}:entry",
                     $"node_modules/ws/node_modules/{packageName}/index.js") ]) ]

let private verifyBundleManifest
    (identity: RuntimeBundleIdentity)
    (text: string)
    =
    try
        use document = JsonDocument.Parse text
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root
            let! bundleHash = requiredString "bundleHash" root
            let! protocol = requiredInt "hostProtocolVersion" root
            let! supervisorGeneration =
                requiredInt
                    "supervisorProtocolGeneration"
                    root
            let! capabilities =
                requiredStringSet "capabilities" root

            let! manifestIdentity =
                parseRuntimeBundleIdentity root

            if
                version <> identity.Version
                || bundleHash <> identity.BundleHash
                || manifestIdentity <> identity
                || protocol <> hostProtocolVersion
                || supervisorGeneration
                   <> supervisorProtocolGeneration
                || not (
                    runtimeBundleCapabilitiesMatch
                        identity.Version
                        capabilities
                )
            then
                return!
                    Error
                        "Durable terminal runtime bundle manifest is incompatible"

            let! actual =
                match tryProperty "files" root with
                | Some files
                    when files.ValueKind
                         = JsonValueKind.Array ->
                    files.EnumerateArray()
                    |> Seq.map (fun file ->
                        result {
                            let! role =
                                requiredString "role" file
                            let! name =
                                requiredString "name" file
                            let! hash =
                                requiredString "sha256" file

                            return role, name, hash
                        })
                    |> Seq.toList
                    |> List.sequenceResultM
                | _ ->
                    Error
                        "Durable terminal runtime bundle manifest omitted its files"

            let expectedLayout =
                runtimeBundleFileLayout identity.Version

            if
                (actual
                 |> List.map (fun (role, name, _) ->
                     role, name))
                <> expectedLayout
                || (actual
                    |> List.exists (fun (_, _, hash) ->
                        not (validSha256Hex hash)))
            then
                return!
                    Error
                        "Durable terminal runtime bundle manifest file identity changed"

            let hashFor role =
                actual
                |> List.find (fun (candidateRole, _, _) ->
                    candidateRole = role)
                |> fun (_, _, hash) -> hash

            if
                hashFor "host" <> identity.HostScriptHash
                || hashFor "supervisor"
                   <> identity.SupervisorScriptHash
                || hashFor "processIdentityHelper"
                   <> identity.ProcessIdentityHelperHash
            then
                return!
                    Error
                        "Durable terminal runtime bundle manifest file identity changed"

            if identity.Version = runtimeBundleVersion then
                let! expectedTtydHash =
                    identity.TtydExecutableHash
                    |> Result.requireSome
                        "Durable terminal runtime bundle omitted its ttyd identity"

                let! expectedWebSocketHash =
                    identity.WebSocketPackageHash
                    |> Result.requireSome
                        "Durable terminal runtime bundle omitted its WebSocket identity"

                let! packageVersion =
                    requiredString
                        "webSocketPackageVersion"
                        root

                let webSocketAssets =
                    actual
                    |> List.choose (fun (role, name, hash) ->
                        if
                            name.StartsWith(
                                "node_modules/ws/",
                                StringComparison.Ordinal
                            )
                        then
                            Some
                                { Role = role
                                  Name = name
                                  Content = Array.empty
                                  Hash = hash }
                        else
                            None)

                if
                    hashFor "ttyd" <> expectedTtydHash
                    || webSocketPackageHash
                           webSocketAssets
                       <> expectedWebSocketHash
                    || packageVersion
                       <> webSocketPackageVersion
                then
                    return!
                        Error
                            "Durable terminal runtime dependency identity changed"

            return actual
        }
    with
    | :? JsonException as ex ->
        Error
            $"Invalid durable terminal runtime bundle manifest: {ex.Message}"
    | ex ->
        Error
            $"Could not read durable terminal runtime bundle manifest: {ex.Message}"

let private readRuntimeBundleIdentity
    (directory: string)
    : Result<RuntimeBundleIdentity, string> =
    try
        result {
            let! manifestPath =
                containedDirectChild directory "bundle.json"

            let info = FileInfo manifestPath

            if
                not info.Exists
                || info.Length > 1024L * 1024L
                || (info.Attributes
                    &&& FileAttributes.ReparsePoint)
                   <> enum 0
            then
                return!
                    Error
                        "Durable terminal runtime bundle manifest is not a bounded regular file"

            use document =
                File.ReadAllText manifestPath
                |> JsonDocument.Parse

            return!
                parseRuntimeBundleIdentity
                    document.RootElement
        }
    with
    | :? JsonException as ex ->
        Error
            $"Invalid durable terminal runtime bundle manifest: {ex.Message}"
    | ex ->
        Error
            $"Could not read durable terminal runtime bundle manifest: {ex.Message}"

let private verifyRuntimeBundleUnsafe
    config
    (identity: RuntimeBundleIdentity)
    =
    result {
        if
            runtimeBundleHash identity
            <> identity.BundleHash
        then
            return!
                Error
                    "Durable terminal runtime bundle hash does not match its files"

        let root = runtimeBundleRoot config
        let! directory =
            runtimeBundleDirectory
                config
                identity.BundleHash

        if not (Directory.Exists directory) then
            return!
                Error
                    "Durable terminal runtime bundle directory is missing"

        do! ensureNoReparsePoint root directory

        let normalizedRelativePath path =
            Path.GetRelativePath(directory, path)
                .Replace(
                    Path.DirectorySeparatorChar,
                    '/'
                )
                .Replace(
                    Path.AltDirectorySeparatorChar,
                    '/'
                )

        let expectedFiles =
            "bundle.json"
            :: (runtimeBundleFileLayout identity.Version
                |> List.map snd)
            |> Set.ofList

        let expectedDirectories =
            expectedFiles
            |> Set.toList
            |> List.collect (fun name ->
                let segments = name.Split('/')

                [ 1 .. max 0 (segments.Length - 1) ]
                |> List.map (fun count ->
                    segments
                    |> Array.take count
                    |> String.concat "/"))
            |> Set.ofList

        let actualFiles =
            Directory.GetFiles(
                directory,
                "*",
                SearchOption.AllDirectories
            )
            |> Array.map normalizedRelativePath
            |> Set.ofArray

        let actualDirectories =
            Directory.GetDirectories(
                directory,
                "*",
                SearchOption.AllDirectories
            )
            |> Array.map (fun path ->
                path,
                normalizedRelativePath path)

        if
            actualFiles <> expectedFiles
            || (actualDirectories
                |> Array.map snd
                |> Set.ofArray)
               <> expectedDirectories
            || (actualDirectories
                |> Array.exists (fun (path, _) ->
                    (File.GetAttributes path
                     &&& FileAttributes.ReparsePoint)
                    <> enum 0))
        then
            return!
                Error
                    "Durable terminal runtime bundle contains unexpected files"

        let! manifestPath =
            containedDirectChild directory "bundle.json"

        let manifestInfo = FileInfo manifestPath

        if
            not manifestInfo.Exists
            || manifestInfo.Length > 1024L * 1024L
            || (manifestInfo.Attributes
                &&& FileAttributes.ReparsePoint)
               <> enum 0
        then
            return!
                Error
                    "Durable terminal runtime bundle manifest is not a bounded regular file"

        let! manifestFiles =
            File.ReadAllText manifestPath
            |> verifyBundleManifest identity

        let! _ =
            manifestFiles
            |> List.map (fun (_, name, expectedHash) ->
                result {
                    let! path =
                        bundleRelativePath directory name
                        |> containedPath directory

                    let info = FileInfo path

                    if
                        not info.Exists
                        || info.Length
                           > 16L * 1024L * 1024L
                        || (info.Attributes
                            &&& FileAttributes.ReparsePoint)
                           <> enum 0
                    then
                        return!
                            Error
                                "Durable terminal runtime bundle file is not a bounded regular file"

                    let actualHash =
                        File.ReadAllBytes path
                        |> sha256Hex

                    if actualHash <> expectedHash then
                        return!
                            Error
                                $"Durable terminal runtime bundle hash mismatch for {name}"

                    return ()
                })
            |> List.sequenceResultM

        return
            { Identity = identity
              Directory = directory }
    }

let private verifyRuntimeBundle
    config
    (identity: RuntimeBundleIdentity)
    =
    try
        verifyRuntimeBundleUnsafe config identity
    with ex ->
        Error
            $"Could not verify durable terminal runtime bundle: {ex.Message}"

let private writeBundleFile
    (path: string)
    (content: byte array)
    =
    Directory.CreateDirectory(Path.GetDirectoryName path)
    |> ignore

    use stream =
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough
        )

    stream.Write(content, 0, content.Length)
    stream.Flush true

let internal materializeRuntimeBundle config =
    result {
        let! assets = runtimeAssets config
        let identity = identityForAssets assets
        let root = runtimeBundleRoot config

        let rootReady =
            try
                Directory.CreateDirectory
                    config.HostStateDirectory
                |> ignore

                Directory.CreateDirectory root |> ignore

                ensureNoReparsePoint
                    config.HostStateDirectory
                    root
            with ex ->
                Error
                    $"Could not create durable terminal runtime bundle store: {ex.Message}"

        do! rootReady

        let! directory =
            runtimeBundleDirectory
                config
                identity.BundleHash

        let claimRecovery =
            try
                let claims =
                    Directory.GetDirectories(
                        root,
                        $"{identity.BundleHash}.*.reclaim",
                        SearchOption.TopDirectoryOnly
                    )

                match
                    Directory.Exists directory,
                    claims
                with
                | false, [| claim |] ->
                    result {
                        let! containedClaim =
                            containedDirectChild
                                root
                                (Path.GetFileName claim)

                        do!
                            ensureNoReparsePoint
                                root
                                containedClaim

                        Directory.Move(
                            containedClaim,
                            directory
                        )

                        return ()
                    }
                | _, [||] -> Ok ()
                | _ ->
                    Error
                        "Durable terminal runtime bundle has conflicting compaction claims"
            with ex ->
                Error
                    $"Could not recover durable terminal runtime bundle: {ex.Message}"

        do! claimRecovery

        if not (Directory.Exists directory) then
            let stagingName =
                $"{identity.BundleHash}.{Environment.ProcessId}.{Guid.NewGuid():N}.pending"

            let! staging =
                containedDirectChild root stagingName

            let installation =
                try
                    try
                        Directory.CreateDirectory staging
                        |> ignore

                        match
                            ensureNoReparsePoint
                                root
                                staging
                        with
                        | Ok () -> ()
                        | Error error ->
                            raise (
                                InvalidDataException
                                    error
                            )

                        let files =
                            assets
                            |> List.map (fun asset ->
                                bundleRelativePath
                                    staging
                                    asset.Name
                                |> containedPath staging
                                |> Result.map (fun path ->
                                    path,
                                    asset.Content))
                            |> List.sequenceResultM
                            |> function
                                | Ok paths -> paths
                                | Error error ->
                                    raise (
                                        InvalidDataException
                                            error
                                    )

                        files
                        |> List.iter (fun (path, content) ->
                            writeBundleFile
                                path
                                content)

                        let manifestPath =
                            match
                                containedDirectChild
                                    staging
                                    "bundle.json"
                            with
                            | Ok path -> path
                            | Error error ->
                                raise (
                                    InvalidDataException
                                        error
                                )

                        writeBundleFile
                            manifestPath
                            (bundleManifestBytes
                                identity
                                assets)

                        try
                            Directory.Move(
                                staging,
                                directory
                            )
                        with :? IOException
                            when Directory.Exists directory ->
                            ()
                    finally
                        if Directory.Exists staging then
                            Directory.Delete(staging, true)

                    Ok ()
                with ex ->
                    Error
                        $"Could not materialize durable terminal runtime bundle: {ex.Message}"

            do! installation

        return! verifyRuntimeBundle config identity
    }

let private readEvidenceText path =
    use stream =
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite ||| FileShare.Delete
        )

    use reader = new StreamReader(stream, Encoding.UTF8, true)
    reader.ReadToEnd()

type private GenerationCompactionOwner =
    { Generation: string
      Pid: int
      ProcessStartTicks: int64
      Nonce: string }

type private GenerationCompactionClaim =
    | CurrentClaim of
        RecordGeneration: string *
        Owner: GenerationCompactionOwner
    | LegacyClaim of RecordGeneration: string

let private validClaimNonce value =
    validBoundedToken 8 128 value

let private generationCompactionClaimName
    recordGeneration
    owner
    =
    if
        not (validGeneration recordGeneration)
        || not (validGeneration owner.Generation)
        || owner.Pid <= 0
        || owner.ProcessStartTicks <= 0L
        || not (validClaimNonce owner.Nonce)
    then
        Error
            "Durable terminal generation compaction owner is invalid"
    else
        Ok
            $"{recordGeneration}.json.{owner.Generation}.{owner.Pid}.{owner.ProcessStartTicks}.{owner.Nonce}.reclaim"

let private parseGenerationCompactionClaimFilename filename =
    let invalid () =
        Error
            "Durable terminal generation directory contains an invalid compaction claim"

    if
        String.IsNullOrEmpty filename
        || filename.Length > 512
    then
        invalid ()
    else
        let parts = filename.Split('.')

        match parts with
        | [| recordGeneration
             "json"
             ownerGeneration
             pidText
             startTicksText
             nonce
             "reclaim" |] ->
            match
                Int32.TryParse pidText,
                Int64.TryParse startTicksText
            with
            | (true, pid), (true, startTicks)
                when validGeneration recordGeneration
                     && validGeneration ownerGeneration
                     && (pidText
                         |> Seq.forall Char.IsAsciiDigit)
                     && (startTicksText
                         |> Seq.forall Char.IsAsciiDigit)
                     && pid > 0
                     && startTicks > 0L
                     && validClaimNonce nonce ->
                Ok(
                    CurrentClaim(
                        recordGeneration,
                        { Generation = ownerGeneration
                          Pid = pid
                          ProcessStartTicks = startTicks
                          Nonce = nonce }
                    )
                )
            | _ -> invalid ()
        | [| recordGeneration
             "json"
             pidText
             nonce
             "reclaim" |]
            when validGeneration recordGeneration
                  && (pidText
                      |> Seq.forall Char.IsAsciiDigit)
                  && (match Int32.TryParse pidText with
                      | true, pid -> pid > 0
                      | _ -> false)
                 && validClaimNonce nonce ->
            Ok(LegacyClaim recordGeneration)
        | [| recordGeneration
             "json"
             pidText
             nonce
             "reclaim"
             "json" |]
            when validGeneration recordGeneration
                 && (pidText
                     |> Seq.forall Char.IsAsciiDigit)
                 && (match Int32.TryParse pidText with
                     | true, pid -> pid > 0
                     | _ -> false)
                 && validClaimNonce nonce ->
            Ok(LegacyClaim recordGeneration)
        | _ -> invalid ()

let private claimRecordGeneration = function
    | CurrentClaim(recordGeneration, _)
    | LegacyClaim recordGeneration ->
        recordGeneration

let private generationClaimPaths directory =
    Directory.GetFiles(
        directory,
        "*",
        SearchOption.TopDirectoryOnly
    )
    |> Array.filter (fun path ->
        let filename = Path.GetFileName path

        filename.EndsWith(
            ".reclaim",
            StringComparison.Ordinal
        )
        || filename.EndsWith(
            ".reclaim.json",
            StringComparison.Ordinal
        ))
    |> Array.sort
    |> Array.toList
    |> List.map (fun path ->
        result {
            let filename = Path.GetFileName path
            let! claim =
                parseGenerationCompactionClaimFilename
                    filename

            let! contained =
                containedDirectChild directory filename

            let info = FileInfo contained

            if
                not info.Exists
                || info.Length > 1024L * 1024L
                || (info.Attributes
                    &&& FileAttributes.ReparsePoint)
                   <> enum 0
            then
                return!
                    Error
                        "Durable terminal generation compaction claim is not a bounded regular file"

            return contained, claim
        })
    |> List.sequenceResultM
    |> Result.bind (fun claims ->
        if
            claims
            |> List.countBy (snd >> claimRecordGeneration)
            |> List.exists (fun (_, count) -> count > 1)
        then
            Error
                "Durable terminal generation directory contains conflicting compaction claims"
        else
            Ok claims)

let private recoverGenerationCompactionClaims directory =
    let restoreClaim (path, claim) =
        result {
            let generation =
                claimRecordGeneration claim

            let! record =
                containedDirectChild
                    directory
                    $"{generation}.json"

            if File.Exists record then
                return!
                    Error
                        "Durable terminal generation compaction claim conflicts with live evidence"

            try
                File.Move(path, record)
            with
            | :? FileNotFoundException -> ()
            | :? IOException
                when File.Exists record
                     && not (File.Exists path) ->
                ()

            return ()
        }

    let rec recover attempts =
        result {
            let! claims = generationClaimPaths directory

            let! activeClaims =
                claims
                |> List.choose (fun ((_, claim) as entry) ->
                    match claim with
                    | LegacyClaim _ -> None
                    | CurrentClaim(_, owner) ->
                        Some(
                            processIdentityMatchesValues
                                owner.Pid
                                owner.ProcessStartTicks
                                true
                            |> Result.map (fun active ->
                                entry, active)
                        ))
                |> List.sequenceResultM
                |> Result.map (
                    List.choose (fun (claim, active) ->
                        if active then Some claim else None)
                )

            match activeClaims, attempts with
            | [], _ ->
                let! _ =
                    claims
                    |> List.map restoreClaim
                    |> List.sequenceResultM

                return ()
            | _, 0 ->
                return!
                    Error
                        "Durable terminal generation evidence is being compacted by another live owner"
            | _ ->
                Thread.Sleep 10
                return! recover (attempts - 1)
        }

    try
        recover 200
    with ex ->
        Error
            $"Could not recover durable terminal generation compaction: {ex.Message}"

let private generationRecordPaths config =
    try
        let directory = generationDirectory config

        if not (Directory.Exists directory) then
            Ok []
        else
            result {
                let! validatedDirectory =
                    containedDirectChild
                        config.HostStateDirectory
                        "terminal-generations"

                do!
                    ensureNoReparsePoint
                        config.HostStateDirectory
                        validatedDirectory

                do!
                    recoverGenerationCompactionClaims
                        validatedDirectory

                let files =
                    Directory.GetFiles(
                        validatedDirectory,
                        "*.json",
                        SearchOption.TopDirectoryOnly
                    )
                    |> Array.sort
                    |> Array.toList

                if
                    List.length files
                    > maximumGenerationRecords
                then
                    return!
                        Error
                            $"Durable terminal generation retention exceeds {maximumGenerationRecords} unresolved records; verify or manually drain retired generations before continuing"

                let! paths =
                    files
                    |> List.map (fun path ->
                        result {
                            let filename =
                                Path.GetFileName path

                            let generation =
                                if
                                    filename.EndsWith(
                                        ".json",
                                        StringComparison.Ordinal
                                    )
                                then
                                    filename.Substring(
                                        0,
                                        filename.Length - 5
                                    )
                                else
                                    ""

                            if not (validGeneration generation) then
                                return!
                                    Error
                                        "Durable terminal generation directory contains invalid evidence"

                            let! contained =
                                containedDirectChild
                                    validatedDirectory
                                    filename

                            let attributes =
                                File.GetAttributes contained

                            if
                                (attributes
                                 &&& FileAttributes.ReparsePoint)
                                <> enum 0
                            then
                                return!
                                    Error
                                        "Durable terminal generation evidence is a reparse point"

                            return contained
                        })
                    |> List.sequenceResultM

                return paths
            }
    with ex ->
        Error
            $"Could not enumerate durable terminal generation evidence: {ex.Message}"

let private readGenerationEvidence config =
    generationRecordPaths config
    |> Result.bind (
        List.map (fun path ->
            try
                let info = FileInfo path

                if info.Length > 1024L * 1024L then
                    Error
                        "Durable terminal generation record exceeded 1 MiB"
                else
                    let text = readEvidenceText path
                    parseGenerationEvidence path text
            with ex ->
                Error
                    $"Could not read durable terminal generation evidence: {ex.Message}")
        >> List.sequenceResultM
        >> Result.bind (fun records ->
            if
                records
                |> List.distinctBy _.Identity.Generation
                |> List.length
                <> List.length records
            then
                Error
                    "Durable terminal generation evidence contains duplicate generation identities"
            else
                Ok records)
    )

let private requireGenerationCapacity config =
    generationRecordPaths config
    |> Result.bind (fun paths ->
        if
            List.length paths
            >= maximumGenerationRecords
        then
            Error
                $"Durable terminal generation retention reached {maximumGenerationRecords} unresolved records; verify or manually drain retired generations before starting another host"
        else
            Ok ())

let private persistUnknownRetiredGeneration
    config
    (connection: HostConnection)
    =
    result {
        let! path =
            generationRecordPath
                config
                connection.Generation

        let! records = readGenerationEvidence config

        match
            records
            |> List.tryFind (fun evidence ->
                evidence.Identity.Generation = connection.Generation)
        with
        | Some existing ->
            if
                existing.HostProtocolVersion
                = connection.Version
                && sameHostIdentity
                    existing.Identity
                    (hostIdentity connection)
            then
                return ()
            else
                return!
                    Error
                    "Existing retired terminal generation evidence belongs to a different host identity"
        | None ->
            if
                List.length records
                >= maximumGenerationRecords
            then
                return!
                    Error
                        $"Durable terminal generation retention reached {maximumGenerationRecords} unresolved records; verify or manually drain retired generations before starting another host"

            let! content =
                if connection.Version = hostProtocolVersion then
                    result {
                        let! bundle =
                            connection.RuntimeBundle
                            |> Result.requireSome
                                "Current durable terminal host omitted its runtime bundle identity"

                        return
                            JsonSerializer.SerializeToUtf8Bytes(
                                {| version =
                                    generationRecordVersion
                                   hostProtocolVersion =
                                    connection.Version
                                   generation =
                                    connection.Generation
                                   hostPid = connection.Pid
                                   hostProcessStartTicks =
                                    string connection.ProcessStartTicks
                                   hostProcessStartExact =
                                    connection.ProcessStartExact
                                   ownershipBoundary =
                                    if connection.KernelOwnership then
                                        "windows-job-v1"
                                    else
                                        "unsupported"
                                   runtimeBundleVersion =
                                    bundle.Version
                                   bundleHash =
                                    bundle.BundleHash
                                   hostScriptHash =
                                    bundle.HostScriptHash
                                   supervisorScriptHash =
                                    bundle.SupervisorScriptHash
                                   processIdentityHelperHash =
                                    bundle.ProcessIdentityHelperHash
                                   ttydExecutableHash =
                                    bundle.TtydExecutableHash
                                    |> Option.toObj
                                   webSocketPackageHash =
                                    bundle.WebSocketPackageHash
                                    |> Option.toObj
                                   supervisorProtocolGeneration =
                                    supervisorProtocolGeneration
                                   capabilities =
                                    connection.Capabilities
                                    |> Set.toArray
                                   startedAt =
                                    connection.StartedAt
                                   sessionsUnknown = true
                                   sessions =
                                    Array.empty<obj> |}
                            )
                    }
                else
                    JsonSerializer.SerializeToUtf8Bytes(
                        {| version =
                            previousGenerationRecordVersion
                           hostProtocolVersion =
                            connection.Version
                           generation =
                            connection.Generation
                           hostPid = connection.Pid
                           hostProcessStartTicks =
                            string connection.ProcessStartTicks
                           hostProcessStartExact =
                            connection.ProcessStartExact
                           ownershipBoundary =
                            if connection.KernelOwnership then
                                "windows-job-v1"
                            else
                                "unsupported"
                           startedAt = connection.StartedAt
                           sessionsUnknown = true
                           sessions = Array.empty<obj> |}
                    )
                    |> Ok

            let directoryReady =
                try
                    Directory.CreateDirectory(
                        generationDirectory config
                    )
                    |> ignore

                    ensureNoReparsePoint
                        config.HostStateDirectory
                        (generationDirectory config)
                with ex ->
                    Error
                        $"Could not prepare retired terminal generation evidence: {ex.Message}"

            do! directoryReady

            return! atomicWriteBytes path content
    }

let private witnessFor config evidence session =
    result {
        match session.TrustState with
        | Quarantined ->
            return!
                Error
                    "Cannot authorize terminal cleanup because the retired supervisor was quarantined after a sticky protocol failure"
        | InProgress
        | LegacyUntrusted ->
            return!
                Error
                    "Cannot authorize terminal cleanup because the retired supervisor did not reach terminal trusted-empty state"
        | TrustedEmpty -> ()

        if
            not session.SupervisorExited
            || session.SupervisorExitCode <> Some 0
            || session.SupervisorExitSignal.IsSome
            || not session.SupervisorOutputClosed
        then
            return!
                Error
                    "Cannot authorize terminal cleanup because the retired supervisor exit transcript is incomplete"

        let! expectedPid =
            session.SupervisorPid
            |> Result.requireSome
                "Cannot confirm retired terminal cleanup because its supervisor PID was not durably published"

        let! expectedStartTicks =
            session.SupervisorStartTicks
            |> Result.requireSome
                "Cannot confirm retired terminal cleanup because its exact supervisor start identity was not durably published"

        let! path =
            emptyWitnessPath
                config
                evidence.Identity.Generation
                session.SessionId

        let! witness =
            try
                if not (File.Exists path) then
                    Error
                        "Cannot confirm retired terminal cleanup because its durable empty witness has not arrived"
                else
                    let info = FileInfo path

                    if
                        info.Length > 1024L * 1024L
                        || (info.Attributes
                            &&& FileAttributes.ReparsePoint)
                           <> enum 0
                    then
                        Error
                            "Retired terminal empty witness is not a bounded regular file"
                    else
                        readEvidenceText path
                        |> parseEmptyWitness
            with ex ->
                Error
                    $"Could not read retired terminal empty witness: {ex.Message}"

        let nonceHash =
            witness.Nonce
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        if
            witness.Generation
            <> evidence.Identity.Generation
            || witness.SessionId <> session.SessionId
            || not (
                Shared.PathUtils.pathEquals
                    (WorktreePath.value witness.WorktreePath)
                    (WorktreePath.value session.WorktreePath)
            )
            || nonceHash <> session.WitnessTokenHash
            || witness.Supervisor.Pid <> expectedPid
            || witness.Supervisor.ProcessStartTicks
               <> expectedStartTicks
        then
            return!
                Error
                    "Cannot confirm retired terminal cleanup because its empty witness does not match the generation, session, nonce, or exact supervisor identity"

        match
            processIdentityMatchesValues
                witness.Supervisor.Pid
                witness.Supervisor.ProcessStartTicks
                true
        with
        | Ok false -> return witness
        | Ok true ->
            return!
                Error
                    "Cannot confirm retired terminal cleanup because its exact Job Object supervisor is still running"
        | Error error ->
            return!
                Error
                    $"Cannot verify the retired Job Object supervisor before terminal cleanup: {error}"
    }

let private generationHostStopped evidence =
    match hostIdentityMatches evidence.Identity with
    | Ok false -> Ok ()
    | Ok true ->
        Error
            "Cannot authorize terminal cleanup while the retired durable host generation is still alive"
    | Error error ->
        Error
            $"Cannot verify the retired durable host generation before terminal cleanup: {error}"

let private removeGenerationWitnesses
    injectFault
    config
    (evidence: GenerationEvidence)
    =
    result {
        let! paths =
            evidence.Sessions
            |> List.map (fun session ->
                emptyWitnessPath
                    config
                    evidence.Identity.Generation
                    session.SessionId)
            |> List.sequenceResultM

        let rec remove = function
            | [] -> Ok ()
            | path :: remaining ->
                try
                    File.Delete path

                    if
                        injectFault
                            GenerationCompactionStage.DuringWitnessCleanup
                    then
                        Error
                            "Injected terminal generation compaction interruption during witness cleanup"
                    else
                        remove remaining
                with ex ->
                    Error
                        $"Could not compact terminal generation witnesses: {ex.Message}"

        do! remove paths

        let! directory =
            emptyWitnessDirectory
                config
                evidence.Identity.Generation

        if
            Directory.Exists directory
            && Directory.EnumerateFileSystemEntries directory
               |> Seq.isEmpty
        then
            try
                Directory.Delete directory
            with :? DirectoryNotFoundException ->
                ()

        return ()
    }

let private compactGenerationWith
    injectFault
    config
    (evidence: GenerationEvidence)
    =
    let directory = Path.GetDirectoryName evidence.Path
    let owner =
        { Generation = $"manager_{Guid.NewGuid():N}"
          Pid = Environment.ProcessId
          ProcessStartTicks = currentProcessStartTicks ()
          Nonce = Guid.NewGuid().ToString("N") }

    let restoreClaim claimedPath =
        try
            if
                File.Exists claimedPath
                && not (File.Exists evidence.Path)
            then
                File.Move(claimedPath, evidence.Path)

            Ok ()
        with ex ->
            Error
                $"Could not restore terminal generation evidence after deferred compaction: {ex.Message}"

    result {
        let! claimedName =
            generationCompactionClaimName
                evidence.Identity.Generation
                owner

        let! claimedPath =
            containedDirectChild directory claimedName

        if injectFault GenerationCompactionStage.BeforeRename then
            return!
                Error
                    "Injected terminal generation compaction interruption before rename"

        let! claimed =
            try
                File.Move(evidence.Path, claimedPath)
                Ok true
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException ->
                Ok false
            | :? IOException
                when not (File.Exists evidence.Path) ->
                Ok false
            | ex ->
                Error
                    $"Could not claim fully witnessed terminal generation evidence: {ex.Message}"

        if not claimed then
            return ()

        if injectFault GenerationCompactionStage.AfterRename then
            return!
                Error
                    "Injected terminal generation compaction interruption after rename"

        let claimedText =
            try
                Ok(readEvidenceText claimedPath)
            with ex ->
                Error ex.Message

        match claimedText with
        | Error _ when File.Exists evidence.Path ->
            return ()
        | Error _ when not (File.Exists claimedPath) ->
            removeGenerationWitnesses
                injectFault
                config
                evidence
            |> ignore

            return ()
        | Error error ->
            do! restoreClaim claimedPath

            return!
                Error
                    $"Could not read claimed terminal generation evidence: {error}"
        | Ok text when text <> evidence.Serialized ->
            do! restoreClaim claimedPath

            return!
                Error
                    "Durable terminal generation evidence changed during compare-before-delete compaction"
        | Ok _ -> ()

        if
            injectFault
                GenerationCompactionStage.BeforeClaimDeletion
        then
            return!
                Error
                    "Injected terminal generation compaction interruption before claim deletion"

        let! deleted =
            try
                File.Delete claimedPath
                Ok true
            with ex ->
                restoreClaim claimedPath
                |> Result.bind (fun () ->
                    Error
                        $"Could not commit terminal generation evidence removal: {ex.Message}")

        if
            not deleted
            || File.Exists evidence.Path
            || File.Exists claimedPath
        then
            if File.Exists claimedPath then
                do! restoreClaim claimedPath

            return!
                Error
                    "Could not confirm committed terminal generation evidence removal"

        if
            injectFault
                GenerationCompactionStage.AfterClaimDeletion
        then
            return!
                Error
                    "Injected terminal generation compaction interruption after claim deletion"

        removeGenerationWitnesses
            injectFault
            config
            evidence
        |> ignore

        return ()
    }

let private compactGeneration =
    compactGenerationWith (fun _ -> false)

let internal compactGenerationForTest
    config
    generation
    injectFault
    =
    result {
        let! evidence =
            readGenerationEvidence config
            |> Result.bind (
                List.tryFind (fun evidence ->
                    evidence.Identity.Generation = generation)
                >> Result.requireSome
                    "Terminal generation evidence was not found"
            )

        return!
            compactGenerationWith
                injectFault
                config
                evidence
    }

let private isCurrentGeneration
    (current: HostConnection option)
    evidence
    =
    current
    |> Option.exists (fun connection ->
        sameHostIdentity
            (hostIdentity connection)
            evidence.Identity)

let private generationIsFullyWitnessed
    config
    evidence
    =
    if
        evidence.SessionsUnknown
        || evidence.RecordVersion
           <> generationRecordVersion
        || evidence.HostProtocolVersion
           <> hostProtocolVersion
        || not evidence.Identity.KernelOwnership
    then
        false
    else
        match evidence.RuntimeBundle with
        | None -> false
        | Some identity ->
            match
                verifyRuntimeBundle config identity,
                generationHostStopped evidence
            with
            | Ok _, Ok () ->
                evidence.Sessions
                |> List.forall (fun session ->
                    witnessFor config evidence session
                    |> Result.isOk)
            | _ -> false

let private compactOrphanedGenerationWitnesses config =
    try
        result {
            let witnessRoot =
                Path.Combine(
                    config.HostStateDirectory,
                    "terminal-empty-witnesses"
                )

            if Directory.Exists witnessRoot then
                do!
                    ensureNoReparsePoint
                        config.HostStateDirectory
                        witnessRoot

                let! knownGenerations =
                    readGenerationEvidence config
                    |> Result.map (
                        List.map _.Identity.Generation
                        >> Set.ofList
                    )

                let! orphaned =
                    Directory.GetDirectories(
                        witnessRoot,
                        "*",
                        SearchOption.TopDirectoryOnly
                    )
                    |> Array.filter (fun directory ->
                        let generation =
                            Path.GetFileName directory

                        validGeneration generation
                        && not (
                            knownGenerations
                            |> Set.contains generation
                        ))
                    |> Array.map (fun directory ->
                        result {
                            let! contained =
                                containedDirectChild
                                    witnessRoot
                                    (Path.GetFileName directory)

                            do!
                                ensureNoReparsePoint
                                    witnessRoot
                                    contained

                            return contained
                        })
                    |> Array.toList
                    |> List.sequenceResultM

                orphaned
                |> List.iter (fun directory ->
                    Directory.Delete(directory, true))

            return ()
        }
    with ex ->
        Error
            $"Could not compact orphaned terminal empty witnesses: {ex.Message}"

let private compactFullyWitnessedGenerations
    config
    current
    =
    result {
        let! records = readGenerationEvidence config

        let! _ =
            records
            |> List.filter (
                isCurrentGeneration current
                >> not
            )
            |> List.filter (
                generationIsFullyWitnessed config
            )
            |> List.map (compactGeneration config)
            |> List.sequenceResultM

        do! compactOrphanedGenerationWitnesses config

        return ()
    }

let private runtimeBundleSnapshot root directory =
    result {
        do! ensureNoReparsePoint root directory

        let directories =
            Directory.GetDirectories(
                directory,
                "*",
                SearchOption.AllDirectories
            )

        if
            directories
            |> Array.exists (fun path ->
                (File.GetAttributes path
                 &&& FileAttributes.ReparsePoint)
                <> enum 0)
        then
            return!
                Error
                    "Durable terminal runtime bundle contains a reparse point"

        let relative path =
            Path.GetRelativePath(directory, path)
                .Replace(
                    Path.DirectorySeparatorChar,
                    '/'
                )
                .Replace(
                    Path.AltDirectorySeparatorChar,
                    '/'
                )

        let directorySnapshot =
            directories
            |> Array.map (fun path ->
                $"directory:{relative path}", "")
            |> Array.toList

        let! fileSnapshot =
            Directory.GetFiles(
                directory,
                "*",
                SearchOption.AllDirectories
            )
            |> Array.sort
            |> Array.toList
            |> List.map (fun path ->
                result {
                    let! contained =
                        containedPath directory path

                    let info = FileInfo contained

                    if
                        not info.Exists
                        || (info.Attributes
                            &&& FileAttributes.ReparsePoint)
                           <> enum 0
                    then
                        return!
                            Error
                                "Durable terminal runtime bundle contains a non-file entry"

                    return
                        $"file:{relative path}",
                        (File.ReadAllBytes contained
                         |> sha256Hex)
                })
            |> List.sequenceResultM

        return
            directorySnapshot @ fileSnapshot
            |> List.sortBy fst
    }

let private compactRuntimeBundle
    config
    (bundle: RuntimeBundle)
    =
    let root = runtimeBundleRoot config
    let claimName =
        $"{bundle.Identity.BundleHash}.{Environment.ProcessId}.{Guid.NewGuid():N}.reclaim"

    result {
        let! before =
            runtimeBundleSnapshot
                root
                bundle.Directory

        let! claim =
            containedDirectChild root claimName

        try
            Directory.Move(bundle.Directory, claim)

            let! after =
                runtimeBundleSnapshot root claim

            if after <> before then
                if not (Directory.Exists bundle.Directory) then
                    Directory.Move(claim, bundle.Directory)

                return!
                    Error
                        "Durable terminal runtime bundle changed during compare-before-delete compaction"

            Directory.Delete(claim, true)
            return ()
        with
        | :? DirectoryNotFoundException -> return ()
        | ex ->
            if
                Directory.Exists claim
                && not (Directory.Exists bundle.Directory)
            then
                try
                    Directory.Move(claim, bundle.Directory)
                with _ ->
                    ()

            return!
                Error
                    $"Could not compact unreferenced durable terminal runtime bundle: {ex.Message}"
    }

let private recoverRuntimeBundleCompactionClaims root =
    try
        Directory.GetDirectories(
            root,
            "*.reclaim",
            SearchOption.TopDirectoryOnly
        )
        |> Array.toList
        |> List.map (fun claim ->
            result {
                let name = Path.GetFileName claim
                let marker = name.IndexOf('.')

                let bundleHash =
                    if marker > 0 then
                        name.Substring(0, marker)
                    else
                        ""

                if
                    not (validSha256Hex bundleHash)
                    || not (
                        name.EndsWith(
                            ".reclaim",
                            StringComparison.Ordinal
                        )
                    )
                then
                    return!
                        Error
                            "Durable terminal runtime store contains an invalid compaction claim"

                let! containedClaim =
                    containedDirectChild root name

                do!
                    ensureNoReparsePoint
                        root
                        containedClaim

                let! bundleDirectory =
                    containedDirectChild
                        root
                        bundleHash

                if Directory.Exists bundleDirectory then
                    return!
                        Error
                            "Durable terminal runtime compaction claim conflicts with an immutable bundle"

                try
                    Directory.Move(
                        containedClaim,
                        bundleDirectory
                    )
                with
                | :? DirectoryNotFoundException ->
                    ()
                | :? IOException
                    when Directory.Exists bundleDirectory ->
                    ()

                return ()
            })
        |> List.sequenceResultM
        |> Result.map ignore
    with ex ->
        Error
            $"Could not recover durable terminal runtime compaction: {ex.Message}"

let private compactRuntimeBundlesUnsafe
    config
    (current: HostConnection option)
    protectedHashes
    =
    result {
        let root = runtimeBundleRoot config

        if not (Directory.Exists root) then
            return ()

        do!
            ensureNoReparsePoint
                config.HostStateDirectory
                root

        do! recoverRuntimeBundleCompactionClaims root

        let! records = readGenerationEvidence config

        let referenced =
            [ yield! protectedHashes |> Set.toList
              yield!
                  records
                  |> List.choose (fun evidence ->
                      evidence.RuntimeBundle
                      |> Option.map _.BundleHash)
              yield!
                  current
                  |> Option.bind _.RuntimeBundle
                  |> Option.map _.BundleHash
                  |> Option.toList ]
            |> Set.ofList

        let! bundles =
            Directory.GetDirectories(
                root,
                "*",
                SearchOption.TopDirectoryOnly
            )
            |> Array.toList
            |> List.choose (fun directory ->
                let name = Path.GetFileName directory

                if validSha256Hex name then
                    Some(
                        result {
                            let! knownIdentity =
                                readRuntimeBundleIdentity
                                    directory

                            if
                                knownIdentity.BundleHash
                                <> name
                            then
                                return!
                                    Error
                                        "Durable terminal runtime bundle directory does not match its manifest hash"

                            return!
                                verifyRuntimeBundle
                                    config
                                    knownIdentity
                        })
                else
                    None)
            |> List.sequenceResultM

        let unreferenced =
            bundles
            |> List.filter (fun bundle ->
                referenced
                |> Set.contains bundle.Identity.BundleHash
                |> not)

        let removable =
            unreferenced
            |> List.sortByDescending (fun bundle ->
                Directory.GetLastWriteTimeUtc bundle.Directory)
            |> List.skip (
                min
                    maximumUnreferencedRuntimeBundles
                    (List.length unreferenced)
            )

        let! _ =
            removable
            |> List.map (compactRuntimeBundle config)
            |> List.sequenceResultM

        return ()
    }

let private compactRuntimeBundles
    config
    (current: HostConnection option)
    protectedHashes
    =
    try
        compactRuntimeBundlesUnsafe
            config
            current
            protectedHashes
    with ex ->
        Error
            $"Could not compact durable terminal runtime bundles: {ex.Message}"

let private confirmPersistedGenerationCleanup
    config
    current
    worktreePath
    =
    result {
        let! records = readGenerationEvidence config

        let retired =
            records
            |> List.filter (
                isCurrentGeneration current
                >> not
            )

        let! _ =
            retired
            |> List.map (fun evidence ->
                let matchingSessions =
                    evidence.Sessions
                    |> List.filter (fun session ->
                        Shared.PathUtils.pathEquals
                            (WorktreePath.value session.WorktreePath)
                            (WorktreePath.value worktreePath))

                if evidence.SessionsUnknown then
                    Error
                        $"Cannot authorize strict terminal cleanup while retired protocol-{evidence.HostProtocolVersion} ownership lacks generation-scoped Job Object witnesses; manually drain its terminals—or restart the machine—then remove that retired record before retrying"
                elif List.isEmpty matchingSessions then
                    Ok ()
                elif
                    evidence.RecordVersion
                    <> generationRecordVersion
                    || evidence.HostProtocolVersion
                    <> hostProtocolVersion
                    || not evidence.Identity.KernelOwnership
                then
                    Error
                        "Cannot authorize strict terminal cleanup for a retired generation without the current Job Object witness protocol"
                else
                    result {
                        let! bundle =
                            evidence.RuntimeBundle
                            |> Result.requireSome
                                "Cannot authorize strict terminal cleanup without an immutable runtime bundle identity"

                        let! _ =
                            verifyRuntimeBundle config bundle

                        do! generationHostStopped evidence

                        let! _ =
                            matchingSessions
                            |> List.map (witnessFor config evidence)
                            |> List.sequenceResultM

                        return ()
                    })
            |> List.sequenceResultM

        let! _ =
            retired
            |> List.filter (
                generationIsFullyWitnessed config
            )
            |> List.map (compactGeneration config)
            |> List.sequenceResultM

        do! compactOrphanedGenerationWitnesses config

        return ()
    }

let private hostUri connection path =
    Uri($"http://127.0.0.1:{connection.ControlPort}{path}")

let private request
    config
    (connection: HostConnection)
    method
    path
    (body: string option)
    =
    async {
        try
            use timeout =
                new CancellationTokenSource(config.ControlRequestTimeout)
            use request = new HttpRequestMessage(method, hostUri connection path)
            request.Headers.Authorization <-
                AuthenticationHeaderValue("Bearer", connection.ControlToken)

            body
            |> Option.iter (fun json ->
                request.Content <-
                    new StringContent(json, Encoding.UTF8, "application/json"))

            use! response =
                httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token
                )
                |> Async.AwaitTask

            let contentLength =
                response.Content.Headers.ContentLength
                |> Option.ofNullable

            match contentLength with
            | Some length when length > 1024L * 1024L ->
                return Error "Durable terminal host response exceeded 1 MiB"
            | _ ->
                let! content =
                    response.Content.ReadAsStringAsync(timeout.Token)
                    |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok content
                else
                    return
                        Error
                            $"Durable terminal host returned HTTP {int response.StatusCode}: {content.Trim()}"
        with
        | :? OperationCanceledException ->
            return
                Error
                    $"Durable terminal host request timed out after {config.ControlRequestTimeout.TotalSeconds:g} seconds"
        | ex ->
            return Error $"Durable terminal host request failed: {ex.Message}"
    }

let private parseHealth connection (text: string) =
    try
        use document = JsonDocument.Parse(text)
        let root = document.RootElement

        result {
            let! version = requiredInt "version" root

            if version <> connection.Version then
                return!
                    Error
                        $"Durable terminal host manifest protocol {connection.Version} does not match health protocol {version}"

            let! pid = requiredInt "pid" root
            let! startedAt = requiredString "startedAt" root

            if pid <> connection.Pid || startedAt <> connection.StartedAt then
                return!
                    Error
                        "Durable terminal host state does not match the running control endpoint"

            let kernelOwnership =
                optionalString "ownershipBoundary" root
                |> Option.contains "windows-job-v1"

            if kernelOwnership <> connection.KernelOwnership then
                return!
                    Error
                        "Durable terminal host ownership capability does not match the running control endpoint"

            if version >= 2 then
                let! generation = requiredString "generation" root
                let! processStartTicks =
                    requiredInt64String "processStartTicks" root
                let! processStartExact =
                    requiredBool "processStartExact" root
                if
                    generation <> connection.Generation
                    || processStartTicks
                       <> connection.ProcessStartTicks
                    || processStartExact
                       <> connection.ProcessStartExact
                then
                    return!
                        Error
                            "Durable terminal host state does not match the running control endpoint"

            if version = hostProtocolVersion then
                let! runtimeBundle =
                    parseRuntimeBundleIdentity root

                let! supervisorGeneration =
                    requiredInt
                        "supervisorProtocolGeneration"
                        root

                let! capabilities =
                    requiredStringSet "capabilities" root

                if
                    connection.RuntimeBundle
                    <> Some runtimeBundle
                    || connection.SupervisorProtocolGeneration
                       <> Some supervisorGeneration
                    || connection.Capabilities
                       <> capabilities
                    || supervisorGeneration
                       <> supervisorProtocolGeneration
                    || not (
                        runtimeBundleCapabilitiesMatch
                            runtimeBundle.Version
                            capabilities
                    )
                then
                    return!
                        Error
                            "Durable terminal host runtime bundle does not match the running control endpoint"
        }
    with
    | :? JsonException as ex ->
        Error $"Invalid durable terminal host health response: {ex.Message}"
    | ex ->
        Error $"Could not read durable terminal host health response: {ex.Message}"

let private probe config connection =
    asyncResult {
        let! response =
            request config connection HttpMethod.Get "/health" None

        return! parseHealth connection response
    }

let private requireCurrentRuntimeBundle
    config
    (connection: HostConnection)
    =
    result {
        if connection.Version <> hostProtocolVersion then
            return!
                Error
                    $"The protocol-{connection.Version} durable terminal host is in drain-only compatibility mode"

        let! identity =
            connection.RuntimeBundle
            |> Result.requireSome
                "Durable terminal host omitted its immutable runtime bundle identity"

        return! verifyRuntimeBundle config identity
    }

type private HostDiscovery =
    | MissingHost
    | HealthyHost of HostConnection
    | DeadHost of HostConnection * reason: string

type private ManifestReclaim =
    | Reclaimed
    | ReclaimDeferred
    | OwnershipChanged

type private LegacyRetirement =
    | LegacyRetired
    | LegacyReplaced of HostConnection

let private discoverHost config =
    async {
        match readHostConnection config with
        | Error error -> return Error error
        | Ok None -> return Ok MissingHost
        | Ok (Some connection) ->
            match processIdentityMatches connection with
            | Error error -> return Error error
            | Ok false ->
                return
                    Ok(
                        DeadHost(
                            connection,
                            $"Durable terminal host PID {connection.Pid} is no longer the recorded process"
                        )
                    )
            | Ok true ->
                match! probe config connection with
                | Ok () -> return Ok(HealthyHost connection)
                | Error probeError ->
                    match processIdentityMatches connection with
                    | Ok false ->
                        return
                            Ok(
                                DeadHost(
                                    connection,
                                    $"Durable terminal host PID {connection.Pid} exited: {probeError}"
                                )
                            )
                    | Ok true ->
                        return
                            Error
                                $"Durable terminal host PID {connection.Pid} is alive but unhealthy: {probeError}"
                    | Error identityError -> return Error identityError
    }

let private sameConnectionOwner
    (left: HostConnection)
    (right: HostConnection)
    =
    if left.Version = 1 || right.Version = 1 then
        left.Version = right.Version
        && left.Pid = right.Pid
        && left.ProcessStartTicks = right.ProcessStartTicks
        && left.ControlPort = right.ControlPort
        && left.ControlToken = right.ControlToken
        && left.StartedAt = right.StartedAt
    elif
        left.Version = hostProtocolVersion
        || right.Version = hostProtocolVersion
    then
        left.Version = right.Version
        && sameHostIdentity
            (hostIdentity left)
            (hostIdentity right)
        && left.RuntimeBundle = right.RuntimeBundle
        && left.SupervisorProtocolGeneration
           = right.SupervisorProtocolGeneration
        && left.Capabilities = right.Capabilities
    else
        sameHostIdentity (hostIdentity left) (hostIdentity right)

let private removeManifestIfConnectionOwned path expected =
    let claimedPath =
        $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.reclaim"

    let restoreClaim () =
        try
            if File.Exists claimedPath && not (File.Exists path) then
                File.Move(claimedPath, path)

            Ok false
        with ex ->
            Error
                $"Could not restore unclaimed durable terminal host state: {ex.Message}"

    let claim () =
        try
            if File.Exists path then
                File.Move(path, claimedPath)
                Ok true
            else
                Ok false
        with
        | :? FileNotFoundException -> Ok false
        | ex ->
            Error
                $"Could not claim durable terminal host state for removal: {ex.Message}"

    match claim () with
    | Error error -> Error error
    | Ok false -> Ok true
    | Ok true ->
        let current =
            try
                File.ReadAllText claimedPath
                |> parseHostConnection
            with ex ->
                Error
                    $"Could not read claimed durable terminal host state: {ex.Message}"

        match current with
        | Error error ->
            restoreClaim ()
            |> Result.bind (fun _ -> Error error)
        | Ok current
            when sameConnectionOwner current expected |> not ->
            restoreClaim ()
        | Ok _ ->
            try
                File.Delete claimedPath
                Ok true
            with ex ->
                restoreClaim ()
                |> Result.bind (fun _ ->
                    Error
                        $"Could not remove claimed durable terminal host state: {ex.Message}")

let internal removeManifestIfOwned path expectedText =
    parseHostConnection expectedText
    |> Result.bind (removeManifestIfConnectionOwned path)

let private removeStoppedState config expected =
    removeManifestIfConnectionOwned
        (statePath config)
        expected
    |> Result.map (function
        | true -> Reclaimed
        | false -> OwnershipChanged)

let private removeStaleState config expected =
    persistUnknownRetiredGeneration config expected
    |> Result.bind (fun () ->
        removeStoppedState config expected)

let private startupLockPath config =
    Path.Combine(config.HostStateDirectory, "host.lock")

let private worktreeLockPath config worktreePath =
    let key =
        worktreePath
        |> WorktreePath.value
        |> PathUtils.normalizePath
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    Path.Combine(
        config.HostStateDirectory,
        "worktree-locks",
        $"{key}.lock"
    )

let private tryAcquireWorktreeLock config worktreePath =
    try
        let path = worktreeLockPath config worktreePath
        Directory.CreateDirectory(Path.GetDirectoryName path)
        |> ignore

        new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        )
        |> Some
        |> Ok
    with
    | :? IOException -> Ok None
    | ex ->
        Error
            $"Could not acquire terminal worktree ownership: {ex.Message}"

let private tryAcquireStartupLock config =
    try
        Directory.CreateDirectory config.HostStateDirectory |> ignore

        new FileStream(
            startupLockPath config,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read
        )
        |> Some
        |> Ok
    with
    | :? IOException -> Ok None
    | ex ->
        Error
            $"Could not acquire durable terminal host startup ownership: {ex.Message}"

let private writeStartupClaim
    (stream: FileStream)
    generation
    (hostProcess: (int * int64) option)
    =
    try
        let claim =
            match hostProcess with
            | None ->
                JsonSerializer.SerializeToUtf8Bytes(
                    {| generation = generation
                       ownerPid = Environment.ProcessId
                       ownerProcessStartTicks =
                        currentProcessStartTicks () |}
                )
            | Some(hostPid, hostProcessStartTicks) ->
                JsonSerializer.SerializeToUtf8Bytes(
                    {| generation = generation
                       ownerPid = Environment.ProcessId
                       ownerProcessStartTicks =
                        currentProcessStartTicks ()
                       hostPid = hostPid
                       hostProcessStartTicks =
                        string hostProcessStartTicks |}
                )

        stream.Position <- 0L
        stream.SetLength 0L
        stream.Write(claim, 0, claim.Length)
        stream.Flush true
        Ok ()
    with ex ->
        Error $"Could not write durable terminal startup ownership: {ex.Message}"

let private startHostProcess
    config
    generation
    (bundle: RuntimeBundle)
    =
    result {
        if not (validGeneration generation) then
            return!
                Error
                    "Invalid durable terminal host generation"

        let! verified =
            verifyRuntimeBundle config bundle.Identity

        let! hostPath =
            containedDirectChild
                verified.Directory
                "durable-terminal-host.mjs"

        let! ttydPath =
            containedDirectChild
                verified.Directory
                "ttyd.exe"

        let! expectedTtydHash =
            verified.Identity.TtydExecutableHash
            |> Result.requireSome
                "Immutable durable terminal runtime omitted ttyd"

        if
            (File.ReadAllBytes hostPath |> sha256Hex)
            <> verified.Identity.HostScriptHash
            || (File.ReadAllBytes ttydPath |> sha256Hex)
               <> expectedTtydHash
        then
            return!
                Error
                    "Immutable durable terminal runtime changed immediately before host launch"

        return!
            try
                Directory.CreateDirectory config.HostStateDirectory
                |> ignore

                let psi =
                    ProcessStartInfo(
                        FileName = config.NodeExecutable,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = verified.Directory
                    )

                [ hostPath
                  "--state-dir"
                  config.HostStateDirectory
                  "--ttyd"
                  ttydPath
                  "--shell"
                  config.ShellCommand
                  "--generation"
                  generation
                  "--runtime-bundle-dir"
                  bundle.Directory
                  "--runtime-bundle-hash"
                  verified.Identity.BundleHash
                  "--runtime-bundle-version"
                  string verified.Identity.Version
                  "--host-script-hash"
                  verified.Identity.HostScriptHash
                  "--supervisor-script-hash"
                  verified.Identity.SupervisorScriptHash
                  "--process-helper-hash"
                  verified.Identity.ProcessIdentityHelperHash
                  "--ttyd-hash"
                  expectedTtydHash
                  "--ws-package-hash"
                  verified.Identity.WebSocketPackageHash.Value ]
                |> List.iter psi.ArgumentList.Add

                use proc = new Process(StartInfo = psi)

                if proc.Start() then
                    Ok(
                        proc.Id,
                        proc.StartTime.ToUniversalTime().Ticks
                    )
                else
                    Error
                        "Node did not start the durable terminal host"
            with ex ->
                Error
                    $"Failed to start the durable terminal host: {ex.Message}"
    }

let private waitForHost
    config
    deadline
    startedPid
    (expectedBundle: RuntimeBundle)
    =
    let rec wait () =
        async {
            match! discoverHost config with
            | Ok (HealthyHost connection)
                when not connection.KernelOwnership ->
                return Error kernelOwnershipError
            | Ok (HealthyHost connection)
                when connection.Version
                     <> hostProtocolVersion ->
                match!
                    request
                        config
                        connection
                        HttpMethod.Get
                        "/sessions"
                        None
                with
                | Ok content ->
                    match parseHostSessions content with
                    | Ok (_ :: _) -> return Ok connection
                    | Ok [] ->
                        let!
                            _ =
                            request
                                config
                                connection
                                HttpMethod.Post
                                "/shutdown"
                                None

                        return
                            Error
                                $"New protocol-{connection.Version} durable terminal host has no sessions and cannot accept current starts"
                    | Error error -> return Error error
                | Error error -> return Error error
            | Ok (HealthyHost connection) ->
                match
                    requireCurrentRuntimeBundle
                        config
                        connection
                with
                | Error error -> return Error error
                | Ok bundle
                    when bundle.Identity
                         = expectedBundle.Identity ->
                    return Ok connection
                | Ok _ ->
                    return
                        Error
                            "Durable terminal host started with an unexpected runtime bundle"
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}: {error}"
            | Ok (DeadHost(_, error)) when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}: {error}"
            | Ok MissingHost when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {startedPid}"
            | Error _
            | Ok (DeadHost _)
            | Ok MissingHost ->
                if not (pidIsAlive startedPid) then
                    return
                        Error
                            $"Durable terminal host PID {startedPid} exited during startup"
                else
                    do! Async.Sleep config.ProbeInterval
                    return! wait ()
        }

    wait ()

let private ensureHostWithTtydRequirement requireTtyd config =
    async {
        if
            not (
                RuntimeInformation.IsOSPlatform(
                    OSPlatform.Windows
                )
            )
        then
            return
                Error
                    $"Kernel-enforced durable terminal ownership is unsupported on {RuntimeInformation.OSDescription}"
        elif not (File.Exists config.HostScriptPath) then
            return
                Error
                    $"Durable terminal host script is missing at '{config.HostScriptPath}'"
        elif not (File.Exists config.SupervisorScriptPath) then
            return
                Error
                    $"Durable terminal supervisor script is missing at '{config.SupervisorScriptPath}'"
        elif not (File.Exists config.ProcessIdentityHelperPath) then
            return
                Error
                    $"Durable terminal process identity helper is missing at '{config.ProcessIdentityHelperPath}'"
        elif
            requireTtyd
            && not (Directory.Exists config.WebSocketPackagePath)
        then
            return
                Error
                    $"The locked ws {webSocketPackageVersion} runtime is missing at '{config.WebSocketPackagePath}'. Run 'npm install'."
        elif requireTtyd && not (File.Exists config.TtydExecutablePath) then
            return
                Error
                    $"ttyd is not installed at '{config.TtydExecutablePath}'. Run '.\\treemon.ps1 setup-ttyd'."
        else
            let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

            let rec acquireOrDiscover () =
                async {
                    match! discoverHost config with
                    | Ok (HealthyHost connection)
                        when requireTtyd
                             && not connection.KernelOwnership ->
                        return Error kernelOwnershipError
                    | Ok (HealthyHost connection)
                        when requireTtyd
                             && connection.Version
                                = hostProtocolVersion ->
                        return
                            requireCurrentRuntimeBundle
                                config
                                connection
                            |> Result.map (fun _ -> connection)
                    | Ok (HealthyHost connection) ->
                        return Ok connection
                    | Error error -> return Error error
                    | Ok (DeadHost _)
                    | Ok MissingHost ->
                        match tryAcquireStartupLock config with
                        | Error error -> return Error error
                        | Ok None when DateTimeOffset.UtcNow >= deadline ->
                            return
                                Error
                                    "Timed out waiting for durable terminal host startup ownership"
                        | Ok None ->
                            do! Async.Sleep config.ProbeInterval
                            return! acquireOrDiscover ()
                        | Ok (Some startupLock) ->
                            use startupLock = startupLock

                            match! discoverHost config with
                            | Ok (HealthyHost connection)
                                when requireTtyd
                                     && not connection.KernelOwnership ->
                                return Error kernelOwnershipError
                            | Ok (HealthyHost connection)
                                when requireTtyd
                                     && connection.Version
                                        = hostProtocolVersion ->
                                return
                                    requireCurrentRuntimeBundle
                                        config
                                        connection
                                    |> Result.map (fun _ ->
                                        connection)
                            | Ok (HealthyHost connection) ->
                                return Ok connection
                            | Error error -> return Error error
                            | Ok discovery ->
                                let startNewHost () =
                                    asyncResult {
                                        do!
                                            compactFullyWitnessedGenerations
                                                config
                                                None

                                        let! bundle =
                                            materializeRuntimeBundle config

                                        do!
                                            compactRuntimeBundles
                                                config
                                                None
                                                (Set.singleton
                                                    bundle.Identity.BundleHash)

                                        do! requireGenerationCapacity config

                                        let generation =
                                            Guid.NewGuid().ToString("N")

                                        do!
                                            writeStartupClaim
                                                startupLock
                                                generation
                                                None

                                        let! startedPid, startedAt =
                                            startHostProcess
                                                config
                                                generation
                                                bundle

                                        do!
                                            writeStartupClaim
                                                startupLock
                                                generation
                                                (Some(
                                                    startedPid,
                                                    startedAt
                                                ))

                                        return!
                                            waitForHost
                                                config
                                                deadline
                                                startedPid
                                                bundle
                                    }

                                match discovery with
                                | DeadHost(connection, _) ->
                                    match removeStaleState config connection with
                                    | Error error -> return Error error
                                    | Ok Reclaimed -> return! startNewHost ()
                                    | Ok OwnershipChanged ->
                                        match! discoverHost config with
                                        | Ok (HealthyHost replacement) ->
                                            if
                                                requireTtyd
                                                && replacement.Version
                                                   = hostProtocolVersion
                                            then
                                                return
                                                    requireCurrentRuntimeBundle
                                                        config
                                                        replacement
                                                    |> Result.map (fun _ ->
                                                        replacement)
                                            else
                                                return Ok replacement
                                        | Ok _ ->
                                            return
                                                Error
                                                    "Durable terminal host ownership changed to an unavailable replacement"
                                        | Error error -> return Error error
                                    | Ok ReclaimDeferred ->
                                        return
                                            Error
                                                "Durable terminal host reclamation was unexpectedly deferred"
                                | MissingHost ->
                                    return! startNewHost ()
                                | HealthyHost connection ->
                                    if
                                        requireTtyd
                                        && connection.Version
                                           = hostProtocolVersion
                                    then
                                        return
                                            requireCurrentRuntimeBundle
                                                config
                                                connection
                                            |> Result.map (fun _ ->
                                                connection)
                                    else
                                        return Ok connection
                }

            return! acquireOrDiscover ()
    }

let private ensureHost =
    ensureHostWithTtydRequirement true

let private ensureControlHost =
    ensureHostWithTtydRequirement false

let private getHostSessions config connection =
    asyncResult {
        let! content =
            request config connection HttpMethod.Get "/sessions" None

        return! parseHostSessions content
    }

let private announce config connection instanceId =
    let body =
        JsonSerializer.Serialize(
            {| kind = "treemon-connected"
               treemonPid = Environment.ProcessId
               instanceId = instanceId |}
        )

    request config connection HttpMethod.Post "/events" (Some body)
    |> AsyncResult.ignore

let private observeHostIdentity state connection =
    let identity = hostIdentity connection

    match state.KnownHost with
    | Some previous
        when sameHostIdentity previous identity
             |> not ->
        let transitionKeys =
            state.LastSnapshot.Tabs
            |> List.choose (fun tab ->
                let key = worktreeKey tab.Worktree
                let supervisor =
                    state.KnownSessionSupervisors
                    |> Map.tryFind key

                if
                    isHostOwned tab
                    || isFailed tab
                    || supervisor.IsSome
                then
                    Some(key, supervisor)
                else
                    None)

        let addDistinct key value values =
            let existing =
                values
                |> Map.tryFind key
                |> Option.defaultValue []

            let updated =
                if existing |> List.contains value then
                    existing
                else
                    value :: existing

            values |> Map.add key updated

        let priorOwners =
            transitionKeys
            |> List.fold (fun owners (key, _) ->
                addDistinct key previous owners)
                state.PriorGenerationOwners

        let priorBoundaries =
            transitionKeys
            |> List.fold (fun boundaries (key, supervisor) ->
                supervisor
                |> Option.map KnownSupervisor
                |> Option.defaultValue MissingSupervisor
                |> fun boundary ->
                    addDistinct key boundary boundaries)
                state.PriorGenerationBoundaries

        { state with
            LastSnapshot =
                interruptForHostReplacement state.LastSnapshot
            AnnouncedHost = None
            KnownHost = Some identity
            PriorGenerationOwners = priorOwners
            KnownSessionSupervisors = Map.empty
            PriorGenerationBoundaries = priorBoundaries }
    | _ ->
        { state with KnownHost = Some identity }

let private confirmPriorGenerationStopped
    state
    worktreePath
    =
    let key = worktreeKey worktreePath

    match state.PriorGenerationOwners |> Map.tryFind key with
    | None -> Ok state
    | Some identities ->
        let stopped stillRunningError inspectionError matches =
            match
                matches
                |> List.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None),
                matches
                |> List.exists (function
                    | Ok true -> true
                    | _ -> false)
            with
            | None, false -> Ok ()
            | None, true -> Error stillRunningError
            | Some error, _ ->
                Error $"{inspectionError}: {error}"

        let ownerResult =
            if
                identities
                |> List.exists (fun identity ->
                    not identity.KernelOwnership)
            then
                Error
                    "Cannot confirm cleanup for a prior durable host generation that lacked kernel-enforced Job Object ownership"
            else
                identities
                |> List.map hostIdentityMatches
                |> stopped
                    "Cannot infer terminal cleanup from the replacement host: the prior durable host generation is still alive"
                    "Cannot verify the prior durable host generation before terminal cleanup"

        let boundaryResult =
            match
                state.PriorGenerationBoundaries
                |> Map.tryFind key
            with
            | None ->
                Error
                    "Cannot confirm prior-generation cleanup because its Job Object supervisor identity was not published"
            | Some boundaries
                when boundaries
                     |> List.contains MissingSupervisor ->
                Error
                    "Cannot confirm prior-generation cleanup because its Job Object supervisor identity was not published"
            | Some boundaries ->
                boundaries
                |> List.choose (function
                    | KnownSupervisor supervisor ->
                        Some supervisor
                    | MissingSupervisor -> None)
                |> List.map (fun supervisor ->
                    processIdentityMatchesValues
                        supervisor.Pid
                        supervisor.ProcessStartTicks
                        true)
                |> stopped
                    "Cannot confirm prior-generation cleanup because its Job Object supervisor is still running"
                    "Cannot verify the prior Job Object supervisor before terminal cleanup"

        match ownerResult, boundaryResult with
        | Ok (), Ok () ->
            Ok
                { state with
                    PriorGenerationOwners =
                        state.PriorGenerationOwners
                        |> Map.remove key
                    PriorGenerationBoundaries =
                        state.PriorGenerationBoundaries
                        |> Map.remove key }
        | Error error, _
        | _, Error error -> Error error

let private confirmAllPriorGenerationCleanup
    config
    current
    state
    worktreePath
    =
    result {
        let! confirmed =
            confirmPriorGenerationStopped
                state
                worktreePath

        do!
            confirmPersistedGenerationCleanup
                config
                current
                worktreePath

        return confirmed
    }

let private announceIfNeeded config state connection instanceId =
    async {
        let identity = hostIdentity connection
        let known = observeHostIdentity state connection

        if
            known.AnnouncedHost
            |> Option.exists (sameHostIdentity identity)
        then
            return known
        else
            match! announce config connection instanceId with
            | Ok () ->
                return
                    { known with
                        AnnouncedHost = Some identity }
            | Error error ->
                Log.log
                    "EmbeddedTerminal"
                    $"Failed to record Treemon reconnect with durable host PID {connection.Pid}: {error}"

                return known
    }

let rec private startTerminal config instanceId state worktreePath =
    async {
        match! ensureHost config with
        | Error error ->
            let current = withFailure worktreePath error state.LastSnapshot
            return Ok current, { state with LastSnapshot = current }
        | Ok connection ->
            let! announced =
                announceIfNeeded config state connection instanceId

            let announced, priorFailure =
                match
                    confirmAllPriorGenerationCleanup
                        config
                        (Some connection)
                        announced
                        worktreePath
                with
                | Ok confirmed -> confirmed, None
                | Error error -> announced, Some error

            let drainOnly, runtimeFailure =
                if connection.Version <> hostProtocolVersion then
                    true, None
                else
                    match
                        materializeRuntimeBundle config,
                        connection.RuntimeBundle
                    with
                    | Ok current, Some running ->
                        match
                            compactRuntimeBundles
                                config
                                (Some connection)
                                (Set.singleton
                                    current.Identity.BundleHash)
                        with
                        | Ok () ->
                            current.Identity <> running,
                            None
                        | Error error -> false, Some error
                    | Error error, _ -> false, Some error
                    | Ok _, None ->
                        false,
                        Some
                            "Durable terminal host omitted its immutable runtime bundle identity"

            let startFailure =
                match priorFailure with
                | Some error -> Some error
                | None -> runtimeFailure

            let reconcile failure =
                async {
                    match! getHostSessions config connection with
                    | Ok sessions
                        when sessions
                             |> List.exists (fun session ->
                                 isPath worktreePath session.Tab) ->
                        let announced =
                            withKnownSessionSupervisors
                                announced
                                sessions

                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshotReplacing
                                worktreePath
                                announced.LastSnapshot

                        return
                            Ok current,
                            { announced with LastSnapshot = current }
                    | Ok sessions ->
                        let announced =
                            withKnownSessionSupervisors
                                announced
                                sessions

                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshotReplacing
                                worktreePath
                                announced.LastSnapshot

                        return
                            Error
                                $"{failure}; reconciliation found no session for '{WorktreePath.value worktreePath}'",
                            { announced with LastSnapshot = current }
                    | Error reconcileError ->
                        let error =
                            $"{failure}; start reconciliation failed: {reconcileError}"

                        let current =
                            withFailure
                                worktreePath
                                error
                                announced.LastSnapshot

                        return
                            Error error,
                            { announced with LastSnapshot = current }
                }

            match startFailure, drainOnly with
            | Some error, _ ->
                let current =
                    withFailure
                        worktreePath
                        error
                        announced.LastSnapshot

                return Error error, { announced with LastSnapshot = current }
            | None, true ->
                match! getHostSessions config connection with
                | Error error -> return! reconcile error
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab) ->
                    let announced =
                        withKnownSessionSupervisors
                            announced
                            sessions

                    let current =
                        sessions
                        |> snapshot
                        |> mergeSnapshotReplacing
                            worktreePath
                            announced.LastSnapshot

                    return
                        Ok current,
                        { announced with LastSnapshot = current }
                | Ok [] ->
                    match!
                        request
                            config
                            connection
                            HttpMethod.Post
                            "/shutdown"
                            None
                    with
                    | Error error ->
                        return! reconcile error
                    | Ok _ ->
                        let retiredState =
                            { announced with
                                AnnouncedHost = None
                                KnownHost = None }

                        let deadline =
                            DateTimeOffset.UtcNow
                            + config.StartupTimeout

                        let rec waitForRetirement () =
                            async {
                                match! discoverHost config with
                                | Ok MissingHost ->
                                    return!
                                        startTerminal
                                            config
                                            instanceId
                                            retiredState
                                            worktreePath
                                | Ok (HealthyHost current)
                                    when sameConnectionOwner
                                             current
                                             connection ->
                                    if
                                        DateTimeOffset.UtcNow
                                        >= deadline
                                    then
                                        return!
                                            reconcile
                                                $"Timed out draining protocol-{connection.Version} durable terminal host"
                                    else
                                        do!
                                            Async.Sleep
                                                config.ProbeInterval

                                        return!
                                            waitForRetirement ()
                                | Ok (DeadHost(current, _))
                                    when sameConnectionOwner
                                             current
                                             connection ->
                                    match
                                        removeStaleState
                                            config
                                            current
                                    with
                                    | Ok Reclaimed ->
                                        return!
                                            startTerminal
                                                config
                                                instanceId
                                                retiredState
                                                worktreePath
                                    | Ok OwnershipChanged ->
                                        return!
                                            waitForRetirement ()
                                    | Ok ReclaimDeferred
                                    | Error _ when
                                        DateTimeOffset.UtcNow
                                        >= deadline
                                        ->
                                        return!
                                            reconcile
                                                $"Timed out reclaiming protocol-{connection.Version} durable terminal host"
                                    | Ok ReclaimDeferred
                                    | Error _ ->
                                        do!
                                            Async.Sleep
                                                config.ProbeInterval

                                        return!
                                            waitForRetirement ()
                                | Ok (HealthyHost current)
                                    when current.Version
                                         = hostProtocolVersion ->
                                    return!
                                        startTerminal
                                            config
                                            instanceId
                                            retiredState
                                            worktreePath
                                | Ok _
                                | Error _ when
                                    DateTimeOffset.UtcNow
                                    >= deadline
                                    ->
                                    return!
                                        reconcile
                                            $"Timed out waiting for protocol-{connection.Version} durable terminal ownership to retire"
                                | Ok _
                                | Error _ ->
                                    do!
                                        Async.Sleep
                                            config.ProbeInterval

                                    return!
                                        waitForRetirement ()
                            }

                        return! waitForRetirement ()
                | Ok sessions ->
                    let announced =
                        withKnownSessionSupervisors
                            announced
                            sessions

                    let error =
                        if
                            connection.Version
                            = hostProtocolVersion
                        then
                            "The running durable terminal host uses a different immutable runtime bundle; close its remaining tabs before starting a new terminal"
                        else
                            $"The protocol-{connection.Version} durable terminal host is in drain-only compatibility mode; close its remaining tabs before starting a new terminal"

                    let current =
                        sessions
                        |> snapshot
                        |> mergeSnapshotReplacing
                            worktreePath
                            announced.LastSnapshot
                        |> withFailure worktreePath error

                    return Error error, { announced with LastSnapshot = current }
            | None, _ ->
                let body =
                    JsonSerializer.Serialize(
                        {| worktreePath =
                            WorktreePath.value worktreePath |}
                    )

                match!
                    request
                        config
                        connection
                        HttpMethod.Post
                        "/sessions"
                        (Some body)
                with
                | Error error -> return! reconcile error
                | Ok content ->
                    match parseHostSessions content with
                    | Error error -> return! reconcile error
                    | Ok sessions ->
                        let announced =
                            withKnownSessionSupervisors
                                announced
                                sessions

                        let current =
                            sessions
                            |> snapshot
                            |> mergeSnapshotReplacing
                                worktreePath
                                announced.LastSnapshot

                        return
                            Ok current,
                            { announced with LastSnapshot = current }
    }

let private reclaimDeadHost config connection =
    match tryAcquireStartupLock config with
    | Error error -> Error error
    | Ok None -> Ok ReclaimDeferred
    | Ok (Some startupLock) ->
        use startupLock = startupLock
        removeStaleState config connection

let private reclaimKnownEmptyHost config connection =
    match tryAcquireStartupLock config with
    | Error error -> Error error
    | Ok None -> Ok ReclaimDeferred
    | Ok (Some startupLock) ->
        use startupLock = startupLock
        removeStoppedState config connection

let private hostFailure error state =
    let current = withHostFailure error state.LastSnapshot
    current, { state with LastSnapshot = current }

let private getTerminals instanceId state config =
    async {
        match! discoverHost config with
        | Ok MissingHost
            when state.KnownHost.IsNone
                 && (state.LastSnapshot.Tabs
                     |> List.exists isInterrupted
                     |> not) ->
            return
                EmbeddedTerminalSnapshot.empty,
                { state with
                    LastSnapshot = EmbeddedTerminalSnapshot.empty
                    AnnouncedHost = None }
        | Ok MissingHost when state.KnownHost.IsNone ->
            return state.LastSnapshot, state
        | Ok MissingHost ->
            let error =
                "Previously known durable terminal host metadata disappeared; terminal processes may have been interrupted"

            return hostFailure error state
        | Ok (DeadHost(connection, error)) ->
            let reclamation =
                reclaimDeadHost config connection

            match reclamation with
            | Error reclaimError ->
                Log.log "EmbeddedTerminal" reclaimError
            | Ok _ -> ()

            let current, failed =
                hostFailure error state

            let next =
                match reclamation with
                | Ok Reclaimed
                | Ok OwnershipChanged ->
                    { failed with
                        AnnouncedHost = None
                        KnownHost = None }
                | Ok ReclaimDeferred
                | Error _ -> failed

            return current, next
        | Error error ->
            Log.log "EmbeddedTerminal" error
            return hostFailure error state
        | Ok (HealthyHost connection) ->
            let! announced =
                announceIfNeeded config state connection instanceId

            match! getHostSessions config connection with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                return hostFailure error announced
            | Ok sessions ->
                let announced =
                    withKnownSessionSupervisors
                        announced
                        sessions

                let current =
                    sessions
                    |> snapshot
                    |> mergeSnapshot announced.LastSnapshot

                return current, { announced with LastSnapshot = current }
    }

let private waitForLegacyHostExit config connection =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let waitAgain continueWaiting timeoutError =
        async {
            if DateTimeOffset.UtcNow >= deadline then
                return Error timeoutError
            else
                do! Async.Sleep config.ProbeInterval
                return! continueWaiting ()
        }

    let rec validateReplacement () =
        async {
            match! discoverHost config with
            | Ok MissingHost -> return Ok LegacyRetired
            | Ok (HealthyHost current)
                when sameConnectionOwner current connection ->
                return!
                    waitAgain
                        validateReplacement
                        $"Timed out waiting for protocol-{connection.Version} durable terminal ownership to change"
            | Ok (HealthyHost current)
                when current.Version = hostProtocolVersion ->
                return Ok(LegacyReplaced current)
            | Ok (HealthyHost _) ->
                return
                    Error
                        $"Protocol-{connection.Version} durable terminal ownership changed to another legacy host"
            | Ok (DeadHost(current, _))
                when sameConnectionOwner current connection ->
                return! wait ()
            | Ok (DeadHost _) ->
                return!
                    waitAgain
                        validateReplacement
                        "Timed out waiting for replacement durable terminal host to become healthy"
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return Error error
            | Error _ ->
                do! Async.Sleep config.ProbeInterval
                return! validateReplacement ()
        }

    and wait () =
        async {
            match processIdentityMatches connection with
            | Ok false ->
                match reclaimKnownEmptyHost config connection with
                | Ok Reclaimed -> return Ok LegacyRetired
                | Ok OwnershipChanged ->
                    return! validateReplacement ()
                | Ok ReclaimDeferred ->
                    match readHostConnection config with
                    | Ok None -> return Ok LegacyRetired
                    | Ok (Some current)
                        when sameConnectionOwner current connection
                             |> not ->
                        return! validateReplacement ()
                    | Ok _
                        when DateTimeOffset.UtcNow >= deadline ->
                        return
                            Error
                                $"Timed out reclaiming protocol-{connection.Version} durable terminal metadata"
                    | Ok _ ->
                        do! Async.Sleep config.ProbeInterval
                        return! wait ()
                    | Error error -> return Error error
                | Error error -> return Error error
            | Ok true ->
                return!
                    waitAgain
                        wait
                        $"Timed out waiting for protocol-{connection.Version} durable terminal host PID {connection.Pid} to drain"
            | Error error -> return Error error
        }

    wait ()

let rec private closeTerminalStrict instanceId state config worktreePath =
    async {
        let closeFailure error failureSnapshot failureState =
            let current =
                withFailure worktreePath error failureSnapshot

            Error error, { failureState with LastSnapshot = current }

        let confirmedClosed sessions confirmedState =
            let current =
                sessions
                |> snapshot
                |> mergeSnapshot confirmedState.LastSnapshot
                |> withoutPath worktreePath

            Ok current, { confirmedState with LastSnapshot = current }

        let finishConfirmed connection sessions confirmedState =
            let confirmedState =
                withKnownSessionSupervisors
                    confirmedState
                    sessions

            async {
                if
                    connection.Version
                    = hostProtocolVersion
                    || not (List.isEmpty sessions)
                then
                    return confirmedClosed sessions confirmedState
                else
                    match!
                        request
                            config
                            connection
                            HttpMethod.Post
                            "/shutdown"
                            None
                    with
                    | Error error ->
                        return
                            closeFailure
                                $"Protocol-{connection.Version} terminal closed, but its empty host did not drain: {error}"
                                confirmedState.LastSnapshot
                                confirmedState
                    | Ok _ ->
                        match! waitForLegacyHostExit config connection with
                        | Ok LegacyRetired ->
                            return
                                confirmedClosed
                                    sessions
                                    { confirmedState with
                                        AnnouncedHost = None
                                        KnownHost = None }
                        | Ok (LegacyReplaced _) ->
                            let current =
                                sessions
                                |> snapshot
                                |> mergeSnapshot confirmedState.LastSnapshot
                                |> withoutPath worktreePath

                            return!
                                closeTerminalStrict
                                    instanceId
                                    { confirmedState with
                                        LastSnapshot = current
                                        AnnouncedHost = None
                                        KnownHost = None }
                                    config
                                    worktreePath
                        | Error error ->
                            return
                                closeFailure
                                    error
                                    confirmedState.LastSnapshot
                                    confirmedState
            }

        let reconcileClose connection failure reconcileState =
            async {
                match! getHostSessions config connection with
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab)
                         |> not ->
                    return!
                        finishConfirmed
                            connection
                            sessions
                            reconcileState
                | Ok sessions ->
                    let error =
                        $"{failure}; the durable host still reports the terminal session"

                    return
                        closeFailure
                            error
                            (sessions
                             |> snapshot
                             |> mergeSnapshot reconcileState.LastSnapshot)
                            reconcileState
                | Error reconcileError ->
                    let error =
                        $"{failure}; close reconciliation failed: {reconcileError}"

                    return
                        closeFailure
                            error
                            reconcileState.LastSnapshot
                            reconcileState
            }

        match! discoverHost config with
        | Ok MissingHost when state.KnownHost.IsNone ->
            match
                confirmAllPriorGenerationCleanup
                    config
                    None
                    state
                    worktreePath
            with
            | Error error ->
                return closeFailure error state.LastSnapshot state
            | Ok confirmed ->
                let current =
                    withoutPath
                        worktreePath
                        confirmed.LastSnapshot

                return
                    Ok current,
                    { confirmed with
                        LastSnapshot = current
                        AnnouncedHost = None }
        | Ok MissingHost ->
            let error =
                "Cannot confirm terminal cleanup because the previously known durable host disappeared"

            return closeFailure error state.LastSnapshot state
        | Ok (DeadHost(connection, reason)) ->
            match reclaimDeadHost config connection with
            | Error reclaimError ->
                let error =
                    $"Cannot retain dead durable-host evidence before terminal cleanup: {reclaimError}"

                return closeFailure error state.LastSnapshot state
            | Ok ReclaimDeferred ->
                let error =
                    $"Cannot confirm terminal cleanup while dead-host reclamation is owned by another manager: {reason}"

                return closeFailure error state.LastSnapshot state
            | Ok Reclaimed ->
                match
                    confirmAllPriorGenerationCleanup
                        config
                        None
                        state
                        worktreePath
                with
                | Error error ->
                    return
                        closeFailure
                            error
                            state.LastSnapshot
                            state
                | Ok confirmed ->
                    let current =
                        confirmed.LastSnapshot
                        |> withoutPath worktreePath

                    return
                        Ok current,
                        { confirmed with
                            LastSnapshot = current
                            AnnouncedHost = None
                            KnownHost = None }
            | Ok OwnershipChanged ->
                return!
                    closeTerminalStrict
                        instanceId
                        { state with
                            AnnouncedHost = None
                            KnownHost = None }
                        config
                        worktreePath
        | Error error ->
            Log.log "EmbeddedTerminal" error
            let actionable =
                $"Cannot discover the durable terminal host to confirm terminal cleanup: {error}"

            return closeFailure actionable state.LastSnapshot state
        | Ok (HealthyHost connection) ->
            let! announced =
                announceIfNeeded config state connection instanceId

            let confirmed =
                if connection.KernelOwnership then
                    confirmAllPriorGenerationCleanup
                        config
                        (Some connection)
                        announced
                        worktreePath
                else
                    Error kernelOwnershipError

            let announced =
                confirmed
                |> Result.defaultValue announced

            let! sessionsResult =
                match confirmed with
                | Ok _ ->
                    getHostSessions config connection
                | Error error -> async.Return(Error error)

            match sessionsResult with
            | Error error ->
                Log.log "EmbeddedTerminal" error
                let actionable =
                    $"Cannot list durable terminal sessions to confirm terminal cleanup: {error}"

                return
                    closeFailure
                        actionable
                        announced.LastSnapshot
                        announced
            | Ok sessions ->
                match sessions |> List.tryFind (fun session -> isPath worktreePath session.Tab) with
                | None ->
                    return!
                        finishConfirmed
                            connection
                            sessions
                            announced
                | Some session ->
                    let path = $"/sessions/{Uri.EscapeDataString session.Id}"

                    match!
                        request
                            config
                            connection
                            HttpMethod.Delete
                            path
                            None
                    with
                    | Error error ->
                        Log.log "EmbeddedTerminal" error
                        return!
                            reconcileClose
                                connection
                                $"Durable terminal close request failed: {error}"
                                announced
                    | Ok content ->
                        match parseHostSessions content with
                        | Error error ->
                            Log.log "EmbeddedTerminal" error
                            return!
                                reconcileClose
                                    connection
                                    $"Durable terminal close response was invalid: {error}"
                                    announced
                        | Ok remaining ->
                            if
                                remaining
                                |> List.exists (fun candidate ->
                                    isPath worktreePath candidate.Tab)
                            then
                                let error =
                                    "Durable terminal host did not remove the terminal session"

                                return
                                    closeFailure
                                        error
                                        (remaining
                                         |> snapshot
                                         |> mergeSnapshot announced.LastSnapshot)
                                        announced
                            else
                                return!
                                    finishConfirmed
                                        connection
                                        remaining
                                        announced
    }

let private closeTerminal instanceId state config worktreePath =
    let currentTab =
        state.LastSnapshot.Tabs
        |> List.tryFind (isPath worktreePath)

    let dismiss snapshot nextState =
        let current =
            snapshot |> withoutPath worktreePath

        current, { nextState with LastSnapshot = current }

    async {
        match currentTab with
        | None ->
            return state.LastSnapshot, state
        | Some tab when not (isInterrupted tab) ->
            let! result, next =
                closeTerminalStrict
                    instanceId
                    state
                    config
                    worktreePath

            return Result.defaultValue next.LastSnapshot result, next
        | Some _ ->
            match! discoverHost config with
            | Ok (HealthyHost connection) ->
                let! announced =
                    announceIfNeeded config state connection instanceId

                match! getHostSessions config connection with
                | Ok sessions
                    when sessions
                         |> List.exists (fun session ->
                             isPath worktreePath session.Tab) ->
                    let announced =
                        withKnownSessionSupervisors
                            announced
                            sessions

                    let! result, next =
                        closeTerminalStrict
                            instanceId
                            announced
                            config
                            worktreePath

                    return
                        Result.defaultValue next.LastSnapshot result,
                        next
                | Ok sessions ->
                    let announced =
                        withKnownSessionSupervisors
                            announced
                            sessions

                    return
                        dismiss
                            (sessions
                             |> snapshot
                             |> mergeSnapshot announced.LastSnapshot)
                            announced
                | Error error ->
                    Log.log "EmbeddedTerminal" error
                    return dismiss announced.LastSnapshot announced
            | Ok (DeadHost(connection, _)) ->
                match reclaimDeadHost config connection with
                | Ok _ -> ()
                | Error error ->
                    Log.log "EmbeddedTerminal" error

                return dismiss state.LastSnapshot state
            | Ok MissingHost ->
                return dismiss state.LastSnapshot state
            | Error error ->
                Log.log "EmbeddedTerminal" error
                return dismiss state.LastSnapshot state
    }

let private renewReservation
    config
    connection
    (reservationId: string)
    (cancellation: CancellationToken)
    =
    let path =
        $"/reservations/{Uri.EscapeDataString reservationId}/renew"

    let rec renew () =
        async {
            try
                do!
                    Task.Delay(
                        config.ReservationRenewalInterval,
                        cancellation
                    )
                    |> Async.AwaitTask

                match!
                    request
                        config
                        connection
                        HttpMethod.Post
                        path
                        None
                with
                | Ok _ -> return! renew ()
                | Error error ->
                    return
                        Error
                            $"Durable terminal cleanup reservation renewal failed: {error}"
            with :? OperationCanceledException ->
                return Ok ()
        }

    renew ()

let private releaseReservation
    config
    connection
    (reservationId: string)
    =
    let path =
        $"/reservations/{Uri.EscapeDataString reservationId}"

    request config connection HttpMethod.Delete path None
    |> AsyncResult.ignore

let private releaseReservationAndDrainLegacy
    config
    connection
    reservationId
    =
    asyncResult {
        do!
            releaseReservation
                config
                connection
                reservationId

        if connection.Version <> hostProtocolVersion then
            let! sessions =
                getHostSessions config connection

            if List.isEmpty sessions then
                do!
                    request
                        config
                        connection
                        HttpMethod.Post
                        "/shutdown"
                        None
                    |> AsyncResult.ignore

                do!
                    waitForLegacyHostExit
                        config
                        connection
                    |> AsyncResult.ignore

        return ()
    }

let private runReservedOperation
    (reservation: CleanupReservation)
    (operation: unit -> Async<Result<unit, string>>)
    (callerCancellation: CancellationToken)
    =
    task {
        try
            use renewalCancellation =
                new CancellationTokenSource()

            use mutationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation
                )

            let renewal =
                task {
                    try
                        return!
                            reservation.Lease.Renew
                                renewalCancellation.Token
                            |> fun workflow ->
                                Async.StartAsTask(
                                    workflow,
                                    cancellationToken = CancellationToken.None
                                )
                    with ex ->
                        return
                            Error
                                $"Durable terminal cleanup reservation renewal failed unexpectedly: {ex.Message}"
                }

            let mutation =
                task {
                    try
                        let! result =
                            operation ()
                            |> fun workflow ->
                                Async.StartAsTask(
                                    workflow,
                                    cancellationToken =
                                        mutationCancellation.Token
                                )

                        return ReservedResult result
                    with
                    | :? OperationCanceledException as cancellation
                        when mutationCancellation.IsCancellationRequested ->
                        return ReservedCancelled cancellation
                    | ex ->
                        return
                            ReservedResult(
                                Error
                                    $"Worktree mutation failed unexpectedly: {ex.Message}"
                            )
                }

            let! completed =
                Task.WhenAny(
                    [| mutation :> Task
                       renewal :> Task |]
                )

            let! operationOutcome, renewalResult, renewalFailedFirst =
                task {
                    if Object.ReferenceEquals(completed, mutation) then
                        let! operationOutcome = mutation
                        renewalCancellation.Cancel()
                        let! renewalResult = renewal
                        return operationOutcome, renewalResult, false
                    else
                        let! renewalResult = renewal

                        let renewalFailure =
                            match renewalResult with
                            | Error error -> Error error
                            | Ok () ->
                                Error
                                    "Durable terminal cleanup reservation renewal ended before the worktree mutation completed"

                        mutationCancellation.Cancel()
                        renewalCancellation.Cancel()
                        let! operationOutcome = mutation
                        return
                            operationOutcome,
                            renewalFailure,
                            true
                }

            let! releaseResult =
                task {
                    try
                        return!
                            reservation.Lease.Release ()
                            |> fun workflow ->
                                Async.StartAsTask(
                                    workflow,
                                    cancellationToken = CancellationToken.None
                                )
                    with ex ->
                        return
                            Error
                                $"Durable terminal cleanup reservation release failed unexpectedly: {ex.Message}"
                }

            let cleanupFailures =
                [ if not renewalFailedFirst then
                      match renewalResult with
                      | Ok () -> ()
                      | Error error -> yield error

                  match releaseResult with
                  | Ok () -> ()
                  | Error error ->
                      yield
                          $"Terminal cleanup reservation release failed: {error}" ]

            let withCleanupFailures result =
                let cleanupError =
                    String.concat "; " cleanupFailures

                match result, cleanupFailures with
                | Ok (), [] -> Ok ()
                | Error error, [] -> Error error
                | Ok (), _ -> Error cleanupError
                | Error error, _ ->
                    Error $"{error}; {cleanupError}"

            return
                match renewalFailedFirst, renewalResult, operationOutcome with
                | true, Error renewalError, ReservedResult(Error settlementError) ->
                    Error
                        $"{renewalError}; mutation settlement failed: {settlementError}"
                    |> withCleanupFailures
                    |> ReservedResult
                | true, Error renewalError, _ ->
                    Error renewalError
                    |> withCleanupFailures
                    |> ReservedResult
                | false, _, ReservedResult result ->
                    withCleanupFailures result |> ReservedResult
                | false, _, ReservedCancelled cancellation
                    when List.isEmpty cleanupFailures ->
                    ReservedCancelled cancellation
                | false, _, ReservedCancelled _ ->
                    let cleanupError =
                        String.concat "; " cleanupFailures

                    Error
                        $"Worktree mutation was cancelled; {cleanupError}"
                    |> ReservedResult
                | true, Ok (), _ ->
                    Error
                        "Durable terminal cleanup reservation renewal ended unexpectedly"
                    |> withCleanupFailures
                    |> ReservedResult
        finally
            reservation.WorktreeLock.Dispose()
    }

let private reserveOnCurrentHost
    config
    instanceId
    state
    connection
    worktreePath
    =
    async {
        let! announced =
            announceIfNeeded config state connection instanceId

        let confirmed =
            if connection.KernelOwnership then
                confirmAllPriorGenerationCleanup
                    config
                    (Some connection)
                    announced
                    worktreePath
            else
                Error kernelOwnershipError

        let announced =
            confirmed
            |> Result.defaultValue announced

        let reservationId =
            Guid.NewGuid().ToString("N")

        let body =
            JsonSerializer.Serialize(
                {| worktreePath = WorktreePath.value worktreePath
                   reservationId = reservationId |}
            )

        let reservationFailure releaseKnownLease error =
            async {
                let! release =
                    if releaseKnownLease then
                        releaseReservation
                            config
                            connection
                            reservationId
                    else
                        async.Return(Ok())

                let actionable =
                    match release with
                    | Ok () ->
                        $"Could not reserve authoritative terminal cleanup: {error}"
                    | Error releaseError ->
                        $"Could not reserve authoritative terminal cleanup: {error}; reservation release also failed: {releaseError}"

                let current =
                    withFailure
                        worktreePath
                        actionable
                        announced.LastSnapshot

                return
                    Error actionable,
                    { announced with LastSnapshot = current }
            }

        let! reservationResult =
            match confirmed with
            | Error error ->
                async.Return(Error(false, error))
            | Ok _ ->
                request
                    config
                    connection
                    HttpMethod.Post
                    "/reservations"
                    (Some body)
                |> Async.map (
                    Result.mapError (fun error ->
                        true, error)
                )

        match reservationResult with
        | Error (releaseKnownLease, error) ->
            return!
                reservationFailure
                    releaseKnownLease
                    error
        | Ok content ->
            match parseReservation content with
            | Error error ->
                return! reservationFailure true error
            | Ok reservation
                when reservation.Id <> reservationId ->
                return!
                    reservationFailure true
                        "Durable terminal host returned a different reservation identity"
            | Ok reservation ->
                let announced =
                    withKnownSessionSupervisors
                        announced
                        reservation.Sessions

                let current =
                    reservation.Sessions
                    |> snapshot
                    |> mergeSnapshot announced.LastSnapshot
                    |> withoutPath worktreePath

                let next =
                    { announced with LastSnapshot = current }

                return
                    Ok
                        { Renew =
                            renewReservation
                                config
                                connection
                                reservation.Id
                          Release =
                            fun () ->
                                releaseReservationAndDrainLegacy
                                    config
                                    connection
                                    reservation.Id },
                    next
    }

let private acquireWorktreeLock
    config
    worktreePath
    : Async<Result<IDisposable, string>> =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec acquire () =
        async {
            match tryAcquireWorktreeLock config worktreePath with
            | Ok (Some worktreeLock) ->
                return Ok(worktreeLock :> IDisposable)
            | Error error -> return Error error
            | Ok None when DateTimeOffset.UtcNow >= deadline ->
                return
                    Error
                        "Timed out waiting for terminal worktree ownership"
            | Ok None ->
                do! Async.Sleep config.ProbeInterval
                return! acquire ()
        }

    acquire ()

let private reserveTerminalCleanup
    config
    instanceId
    state
    worktreePath
    =
    let reserveCurrent currentState =
        async {
            match! ensureControlHost config with
            | Error error -> return Error error, currentState
            | Ok connection when not connection.KernelOwnership ->
                return Error kernelOwnershipError, currentState
            | Ok connection
                when connection.Version
                     >= 2 ->
                return!
                    reserveOnCurrentHost
                        config
                        instanceId
                        currentState
                        connection
                        worktreePath
            | Ok _ ->
                return
                    Error
                        "Legacy durable terminal host did not finish draining",
                    currentState
        }

    let confirmAndReserve currentState =
        async {
            match
                confirmAllPriorGenerationCleanup
                    config
                    None
                    currentState
                    worktreePath
            with
            | Error error -> return Error error, currentState
            | Ok confirmed -> return! reserveCurrent confirmed
        }

    async {
        match! discoverHost config with
        | Ok (HealthyHost connection)
            when not connection.KernelOwnership ->
            return Error kernelOwnershipError, state
        | Ok (HealthyHost connection)
            when connection.Version >= 2 ->
            return!
                reserveOnCurrentHost
                    config
                    instanceId
                    state
                    connection
                    worktreePath
        | Error error ->
            let actionable =
                $"Cannot reserve terminal cleanup because host discovery failed: {error}"

            return Error actionable, state
        | Ok (HealthyHost connection) ->
            match!
                request
                    config
                    connection
                    HttpMethod.Post
                    "/shutdown"
                    None
            with
            | Error error ->
                return
                    Error
                        $"Could not drain protocol-{connection.Version} durable terminal host: {error}",
                    state
            | Ok _ ->
                match! waitForLegacyHostExit config connection with
                | Error error -> return Error error, state
                | Ok retirement ->
                    let interrupted =
                        state.LastSnapshot
                        |> withHostFailure
                            $"the protocol-{connection.Version} host was drained during its bounded compatibility window"
                        |> withoutPath worktreePath

                    let retiredState =
                        { state with
                            LastSnapshot = interrupted
                            AnnouncedHost = None
                            KnownHost = None }

                    match retirement with
                    | LegacyRetired ->
                        return! reserveCurrent retiredState
                    | LegacyReplaced replacement ->
                        return!
                            reserveOnCurrentHost
                                config
                                instanceId
                                retiredState
                                replacement
                                worktreePath
        | Ok (DeadHost(connection, reason)) ->
            match reclaimDeadHost config connection with
            | Error error ->
                return
                    Error
                        $"Cannot retain dead durable-host evidence before reserving cleanup: {error}",
                    state
            | Ok ReclaimDeferred ->
                return
                    Error
                        $"Cannot reserve terminal cleanup while dead-host reclamation is owned by another manager: {reason}",
                    state
            | Ok Reclaimed
            | Ok OwnershipChanged ->
                return! reserveCurrent state
        | Ok MissingHost ->
            return! confirmAndReserve state
    }

let private waitForHostExit
    retainDeadGeneration
    config
    connection
    =
    let deadline = DateTimeOffset.UtcNow + config.StartupTimeout

    let rec wait () =
        async {
            match processIdentityMatches connection with
            | Ok false ->
                let reclamation =
                    if retainDeadGeneration then
                        reclaimDeadHost config connection
                    else
                        reclaimKnownEmptyHost config connection

                match reclamation with
                | Ok Reclaimed
                | Ok OwnershipChanged ->
                    return Ok ()
                | Ok ReclaimDeferred ->
                    match readHostConnection config with
                    | Ok None -> return Ok ()
                    | Ok (Some current)
                        when sameConnectionOwner current connection
                             |> not ->
                        return Ok ()
                    | _ when DateTimeOffset.UtcNow >= deadline ->
                        return
                            Error
                                "Timed out waiting to reclaim stopped durable terminal host metadata"
                    | _ ->
                        do! Async.Sleep config.ProbeInterval
                        return! wait ()
                | Error error -> return Error error
            | Ok true when DateTimeOffset.UtcNow < deadline ->
                do! Async.Sleep config.ProbeInterval
                return! wait ()
            | Ok true ->
                return
                    Error
                        $"Timed out waiting for durable terminal host PID {connection.Pid} to stop"
            | Error error when DateTimeOffset.UtcNow >= deadline ->
                return Error error
            | Error _ ->
                do! Async.Sleep config.ProbeInterval
                return! wait ()
        }

    wait ()

let private shutdown config =
    async {
        match! discoverHost config with
        | Error error -> return Error error
        | Ok MissingHost -> return Ok ()
        | Ok (DeadHost(connection, _)) ->
            return!
                waitForHostExit
                    true
                    config
                    connection
        | Ok (HealthyHost connection)
            when not connection.KernelOwnership ->
            return Error kernelOwnershipError
        | Ok (HealthyHost connection) ->
            match!
                request
                    config
                    connection
                    HttpMethod.Post
                    "/shutdown"
                    None
                |> AsyncResult.ignore
            with
            | Error error -> return Error error
            | Ok () ->
                return!
                    waitForHostExit
                        false
                        config
                        connection
    }

let private lockAcquisitionCancelled =
    "Terminal worktree ownership request was cancelled or timed out"

let private lockAcquisitionBusy =
    "Another terminal operation is already waiting for this worktree ownership"

let private replyLockFailure error request =
    match request with
    | PendingStart reply -> reply.Reply(Error error)
    | PendingCleanup reply -> reply.Reply(Error error)

let private disposeLockResult
    (result: Result<IDisposable, string>)
    =
    match result with
    | Ok worktreeLock -> worktreeLock.Dispose()
    | Error _ -> ()

let private pendingForPath
    (worktreePath: WorktreePath)
    (state: ManagerState)
    =
    state.PendingLocks
    |> Map.exists (fun _ pending ->
        Shared.PathUtils.pathEquals
            (WorktreePath.value worktreePath)
            (WorktreePath.value pending.WorktreePath))

let internal createWithLockAcquisition
    config
    (acquireLock:
        WorktreePath -> Async<Result<IDisposable, string>>)
    =
    let instanceId = Guid.NewGuid().ToString("N")

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let launchLockAcquisition token worktreePath =
                async {
                    let! result =
                        async {
                            try
                                return! acquireLock worktreePath
                            with ex ->
                                return
                                    Error
                                        $"Could not acquire terminal worktree ownership unexpectedly: {ex.Message}"
                        }

                    inbox.Post(LockAcquired(token, result))
                }
                |> fun acquisition ->
                    Async.Start(
                        acquisition,
                        cancellationToken = CancellationToken.None
                    )

            let beginLockAcquisition
                (token: LockAcquisitionToken)
                (cancellation: CancellationToken)
                (worktreePath: WorktreePath)
                (request: PendingLockRequest)
                (state: ManagerState)
                =
                if pendingForPath worktreePath state then
                    replyLockFailure lockAcquisitionBusy request
                    state
                elif cancellation.IsCancellationRequested then
                    replyLockFailure
                        lockAcquisitionCancelled
                        request

                    state
                else
                    let registration =
                        cancellation.Register(fun () ->
                            inbox.Post(CancelLockAcquisition token))

                    let pending =
                        { WorktreePath = worktreePath
                          Cancellation = cancellation
                          Registration = registration
                          Request = request }

                    launchLockAcquisition token worktreePath

                    { state with
                        PendingLocks =
                            state.PendingLocks
                            |> Map.add token pending }

            let cancelPendingLocks error state =
                state.PendingLocks
                |> Map.iter (fun _ pending ->
                    pending.Registration.Dispose()
                    replyLockFailure error pending.Request)

                { state with PendingLocks = Map.empty }

            let rec loop state =
                async {
                    let! message = inbox.Receive()

                    match message with
                    | Start(
                        token,
                        cancellation,
                        worktreePath,
                        reply
                      ) ->
                        let canonical =
                            canonicalWorktreePath worktreePath

                        let next =
                            beginLockAcquisition
                                token
                                cancellation
                                canonical
                                (PendingStart reply)
                                state

                        return! loop next
                    | Get reply ->
                        let! current, next =
                            getTerminals instanceId state config

                        reply.Reply current
                        return! loop next
                    | Close(worktreePath, reply) ->
                        let canonical =
                            canonicalWorktreePath worktreePath

                        let! current, next =
                            closeTerminal
                                instanceId
                                state
                                config
                                canonical

                        reply.Reply current
                        return! loop next
                    | CloseStrict(worktreePath, reply) ->
                        let canonical =
                            canonicalWorktreePath worktreePath

                        let! result, next =
                            closeTerminalStrict
                                instanceId
                                state
                                config
                                canonical

                        reply.Reply result
                        return! loop next
                    | ReserveCleanup(
                        token,
                        cancellation,
                        worktreePath,
                        reply
                      ) ->
                        let canonical =
                            canonicalWorktreePath worktreePath

                        let next =
                            beginLockAcquisition
                                token
                                cancellation
                                canonical
                                (PendingCleanup reply)
                                state

                        return! loop next
                    | CancelLockAcquisition token ->
                        match state.PendingLocks |> Map.tryFind token with
                        | None -> return! loop state
                        | Some pending ->
                            pending.Registration.Dispose()
                            replyLockFailure
                                lockAcquisitionCancelled
                                pending.Request

                            return!
                                loop
                                    { state with
                                        PendingLocks =
                                            state.PendingLocks
                                            |> Map.remove token }
                    | LockAcquired(token, acquisition) ->
                        match state.PendingLocks |> Map.tryFind token with
                        | None ->
                            disposeLockResult acquisition
                            return! loop state
                        | Some pending ->
                            pending.Registration.Dispose()
                            let acquiredState =
                                { state with
                                    PendingLocks =
                                        state.PendingLocks
                                        |> Map.remove token }

                            if
                                pending.Cancellation.IsCancellationRequested
                            then
                                disposeLockResult acquisition
                                replyLockFailure
                                    lockAcquisitionCancelled
                                    pending.Request

                                return! loop acquiredState
                            else
                                match acquisition, pending.Request with
                                | Error error, PendingStart reply ->
                                    let current =
                                        withFailure
                                            pending.WorktreePath
                                            error
                                            acquiredState.LastSnapshot

                                    reply.Reply(Error error)

                                    return!
                                        loop
                                            { acquiredState with
                                                LastSnapshot = current }
                                | Error error, PendingCleanup reply ->
                                    reply.Reply(Error error)
                                    return! loop acquiredState
                                | Ok worktreeLock, PendingStart reply ->
                                    let! result, next =
                                        async {
                                            use worktreeLock =
                                                worktreeLock

                                            return!
                                                startTerminal
                                                    config
                                                    instanceId
                                                    acquiredState
                                                    pending.WorktreePath
                                        }

                                    reply.Reply result
                                    return! loop next
                                | Ok worktreeLock, PendingCleanup reply ->
                                    try
                                        let! result, next =
                                            reserveTerminalCleanup
                                                config
                                                instanceId
                                                acquiredState
                                                pending.WorktreePath

                                        match result with
                                        | Ok lease ->
                                            reply.Reply(
                                                Ok
                                                    { Lease = lease
                                                      WorktreeLock =
                                                        worktreeLock }
                                            )
                                        | Error error ->
                                            worktreeLock.Dispose()
                                            reply.Reply(Error error)

                                        return! loop next
                                    with ex ->
                                        worktreeLock.Dispose()
                                        reply.Reply(
                                            Error
                                                $"Could not reserve terminal cleanup unexpectedly: {ex.Message}"
                                        )

                                        return! loop acquiredState
                    | ShutdownHost reply ->
                        let shutdownState =
                            cancelPendingLocks
                                "Durable terminal host shutdown cancelled pending worktree ownership"
                                state

                        let! result = shutdown config
                        reply.Reply result

                        let next =
                            match result with
                            | Ok () ->
                                { LastSnapshot =
                                    EmbeddedTerminalSnapshot.empty
                                  AnnouncedHost = None
                                  KnownHost = None
                                  PriorGenerationOwners = Map.empty
                                  KnownSessionSupervisors = Map.empty
                                  PriorGenerationBoundaries = Map.empty
                                  PendingLocks = Map.empty }
                            | Error error ->
                                let current =
                                    withHostFailure
                                        error
                                        shutdownState.LastSnapshot

                                { shutdownState with
                                    LastSnapshot = current }

                        return! loop next
                }

            loop
                { LastSnapshot = EmbeddedTerminalSnapshot.empty
                  AnnouncedHost = None
                  KnownHost = None
                  PriorGenerationOwners = Map.empty
                  KnownSessionSupervisors = Map.empty
                  PriorGenerationBoundaries = Map.empty
                  PendingLocks = Map.empty })

    Manager agent

let internal createWithConfig config =
    createWithLockAcquisition
        config
        (acquireWorktreeLock config)

let create () = createWithConfig (defaultConfig ())

let start (Manager agent) worktreePath =
    async {
        let! callerCancellation = Async.CancellationToken
        use timeoutCancellation =
            new CancellationTokenSource(60_000)

        use requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                timeoutCancellation.Token
            )

        let token =
            Guid.NewGuid() |> LockAcquisitionToken

        let! result =
            agent.PostAndAsyncReply(fun reply ->
                Start(
                    token,
                    requestCancellation.Token,
                    worktreePath,
                    reply
                ))

        if callerCancellation.IsCancellationRequested then
            return
                raise (
                    OperationCanceledException(
                        callerCancellation
                    )
                )

        return result
    }

let get (Manager agent) =
    agent.PostAndAsyncReply(Get, timeout = 60_000)

let close (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> Close(worktreePath, reply)),
        timeout = 60_000
    )

let internal closeStrict (Manager agent) worktreePath =
    agent.PostAndAsyncReply(
        (fun reply -> CloseStrict(worktreePath, reply)),
        timeout = 60_000
    )

let internal withReservedCleanup
    (Manager agent)
    worktreePath
    operation
    =
    async {
        let! callerCancellation = Async.CancellationToken
        let token =
            Guid.NewGuid() |> LockAcquisitionToken

        let bracket =
            task {
                let! reservation =
                    agent.PostAndAsyncReply(
                        (fun reply ->
                            ReserveCleanup(
                                token,
                                callerCancellation,
                                worktreePath,
                                reply
                            ))
                    )
                    |> fun workflow ->
                        Async.StartAsTask(
                            workflow,
                            cancellationToken = CancellationToken.None
                        )

                match reservation with
                | Error _
                    when callerCancellation.IsCancellationRequested ->
                    return
                        ReservedCancelled(
                            OperationCanceledException(
                                callerCancellation
                            )
                        )
                | Error error ->
                    return ReservedResult(Error error)
                | Ok acquired ->
                    return!
                        runReservedOperation
                            acquired
                            operation
                            callerCancellation
            }

        let! outcome = bracket |> Async.AwaitTask

        return
            match outcome with
            | ReservedResult result -> result
            | ReservedCancelled cancellation ->
                raise cancellation
    }

let internal shutdownHost (Manager agent) =
    agent.PostAndAsyncReply(ShutdownHost, timeout = 60_000)
