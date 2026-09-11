namespace TerminalHost

open System

[<Struct>]
type internal DiagnosticProcessIdentity =
    { ProcessId: int
      StartTimeUtcTicks: int64 }

[<RequireQualifiedAccess>]
type internal ProcessCleanupStage =
    | OwnershipCaptured
    | OwnershipRecaptured
    | JobClosed
    | SurvivorsObserved
    | TerminationAttempted
    | Completed
    | UnresolvedSurvivors

type internal ProcessCleanupDiagnostic =
    { Stage: ProcessCleanupStage
      ProcessIdentities: DiagnosticProcessIdentity list }

[<RequireQualifiedAccess>]
type internal TerminalCloseOutcome =
    | Completed
    | ProcessPreparationFailed
    | DataPlaneFailed
    | ProcessCleanupFailed

type internal TerminalCloseDiagnostic =
    { TerminalSessionId: string
      Outcome: TerminalCloseOutcome }

[<RequireQualifiedAccess>]
type internal TerminalHostDiagnostic =
    | ProcessCleanup of ProcessCleanupDiagnostic
    | TerminalClose of TerminalCloseDiagnostic

[<RequireQualifiedAccess>]
module internal TerminalHostDiagnostics =
    [<Literal>]
    let maxListedValues = 8

    type Sink = TerminalHostDiagnostic -> unit

    let private processIdentityText identity =
        $"{identity.ProcessId}@{identity.StartTimeUtcTicks}"

    let private terminalSessionIdText value =
        match Guid.TryParseExact(value, "N") with
        | true, terminalSessionId ->
            terminalSessionId.ToString("N")
        | false, _ -> "invalid"

    let private boundedProcessFields identities =
        let all =
            identities
            |> List.map processIdentityText
            |> List.sort

        let shown =
            all
            |> List.truncate maxListedValues
            |> String.concat ","

        $"process_count={all.Length} processes=[{shown}] processes_omitted={max 0 (all.Length - maxListedValues)}"

    let format =
        function
        | TerminalHostDiagnostic.ProcessCleanup cleanup ->
            let stage =
                match cleanup.Stage with
                | ProcessCleanupStage.OwnershipCaptured ->
                    "ownership_captured"
                | ProcessCleanupStage.OwnershipRecaptured ->
                    "ownership_recaptured"
                | ProcessCleanupStage.JobClosed ->
                    "job_closed"
                | ProcessCleanupStage.SurvivorsObserved ->
                    "survivors_observed"
                | ProcessCleanupStage.TerminationAttempted ->
                    "termination_attempted"
                | ProcessCleanupStage.Completed ->
                    "completed"
                | ProcessCleanupStage.UnresolvedSurvivors ->
                    "unresolved_survivors"

            $"event=process_cleanup stage={stage} {boundedProcessFields cleanup.ProcessIdentities}"
        | TerminalHostDiagnostic.TerminalClose terminal ->
            let outcome =
                match terminal.Outcome with
                | TerminalCloseOutcome.Completed -> "completed"
                | TerminalCloseOutcome.ProcessPreparationFailed ->
                    "process_preparation_failed"
                | TerminalCloseOutcome.DataPlaneFailed ->
                    "data_plane_failed"
                | TerminalCloseOutcome.ProcessCleanupFailed ->
                    "process_cleanup_failed"

            $"event=terminal_close outcome={outcome} terminal={terminalSessionIdText terminal.TerminalSessionId}"

    let write: Sink =
        fun diagnostic ->
            try
                Console.Error.WriteLine(format diagnostic)
            with _ ->
                ()
