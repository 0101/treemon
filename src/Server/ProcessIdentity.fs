namespace Server

open System
open System.Diagnostics
open Shared

/// Exact identity of one operating-system process. The start timestamp distinguishes a reused PID
/// from the process that previously owned it.
[<Struct>]
type ProcessIdentity =
    private
    | ProcessIdentity of processId: int * processStartTimeUtcTicks: int64

module ProcessIdentity =
    let create processId processStartTimeUtcTicks =
        if processId <= 0 then
            Error "processId must be positive"
        elif processStartTimeUtcTicks <= 0L then
            Error "processStartTimeUtcTicks must be positive"
        else
            Ok(ProcessIdentity(processId, processStartTimeUtcTicks))

    let processId (ProcessIdentity(processId, _)) = processId

    let processStartTimeUtcTicks (ProcessIdentity(_, processStartTimeUtcTicks)) =
        processStartTimeUtcTicks

    let sortKey identity =
        processId identity, processStartTimeUtcTicks identity

    let sessionInstanceId identity =
        let processId, processStartTimeUtcTicks = sortKey identity
        SessionInstanceId $"{processId:x8}:{processStartTimeUtcTicks:x16}"

/// One injectable operating-system identity boundary. Resolving a PID returns the exact currently
/// running process identity, or None when that PID is not running. Exact liveness checks deliberately
/// reuse this same resolver so ingestion, shutdown waiting, and survivor verification cannot disagree
/// about PID reuse.
type ProcessIdentityResolver =
    { Resolve: int -> Result<ProcessIdentity option, string> }

module ProcessIdentityResolver =
    let create resolve = { Resolve = resolve }

    let resolve processId resolver =
        if processId <= 0 then
            Error "processId must be positive"
        else
            resolver.Resolve processId

    let isAlive resolver identity =
        identity
        |> ProcessIdentity.processId
        |> fun processId -> resolve processId resolver
        |> Result.map (Option.contains identity)

module ProcessIdentityResolverRuntime =
    let defaultResolver =
        ProcessIdentityResolver.create (fun processId ->
            try
                use childProcess = Process.GetProcessById processId

                if childProcess.HasExited then
                    Ok None
                else
                    childProcess.StartTime.ToUniversalTime().Ticks
                    |> ProcessIdentity.create processId
                    |> Result.map Some
            with
            | :? ArgumentException
            | :? InvalidOperationException -> Ok None
            | error ->
                Error $"Could not resolve process identity: {error.Message}")
