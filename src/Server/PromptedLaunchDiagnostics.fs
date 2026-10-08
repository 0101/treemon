module Server.PromptedLaunchDiagnostics

open System
open System.Diagnostics
open System.Text.Json
open Shared
open Server.SessionActivity

[<RequireQualifiedAccess>]
type internal Milestone =
    | Requested
    | CreatingWorktree
    | WorktreeCreated
    | PostForkStarted
    | PostForkCompleted
    | DiscoveringWorktree
    | WorktreeDiscovered
    | StartingCommand
    | TerminalStarted
    | BridgeMatched
    | SendingPrompt
    | Accepted

[<RequireQualifiedAccess>]
type internal DeliveryFailure =
    | HttpRejected of status: int * bodyLength: int
    | Cancelled
    | TransportFailed of exn

[<RequireQualifiedAccess>]
type internal RegistrationAvailability =
    | Missing
    | OtherSessions of SessionId list
    | WrongWorktree of string
    | Matching

[<RequireQualifiedAccess>]
type internal Failure =
    | CreationFailed of string
    | DiscoveryFailed
    | WorktreeNotDiscovered of count: int
    | TerminalStartFailed of string
    | StartupFailed of StartupPromptFailure * RegistrationAvailability
    | Unexpected of exn

[<RequireQualifiedAccess>]
type internal Event =
    | CreationRequested of CreateWorktreeRequest
    | Progress of Milestone
    | PostForkSkipped
    | PostForkCompleted of Result<unit, string>
    | PostForkCrashed of exn
    | WorktreeDiscovered of count: int
    | CommandStarting of CodingToolProvider option * command: string * promptLength: int * timeout: TimeSpan
    | TerminalStarted of EmbeddedTerminalId
    | PromptReserved of Guid
    | BridgeMatched of ProcessIdentity option * TerminalSessionId option
    | PromptSending
    | PromptAccepted of afterCancellation: bool
    | DeliveryFailed of DeliveryFailure
    | Failed of Failure
    | PromptRemoved
    | CleanupStarted
    | CleanupCompleted of Result<unit, string>
    | LaunchCompleted of Result<unit, string>
    | AutoLaunchSkipped

let private milestoneInfo =
    function
    | Milestone.Requested -> 0, "requested"
    | Milestone.CreatingWorktree -> 1, "creating-worktree"
    | Milestone.WorktreeCreated -> 2, "worktree-created"
    | Milestone.PostForkStarted -> 3, "post-fork-started"
    | Milestone.PostForkCompleted -> 4, "post-fork-completed"
    | Milestone.DiscoveringWorktree -> 5, "discovering-worktree"
    | Milestone.WorktreeDiscovered -> 6, "worktree-discovered"
    | Milestone.StartingCommand -> 7, "starting-command"
    | Milestone.TerminalStarted -> 8, "terminal-started"
    | Milestone.BridgeMatched -> 9, "bridge-matched"
    | Milestone.SendingPrompt -> 10, "sending-prompt"
    | Milestone.Accepted -> 11, "accepted"

let internal deliveryFailureDetails =
    function
    | DeliveryFailure.HttpRejected(status, length) ->
        box {| reason = "http-rejected"; status = status; bodyLength = length |}
    | DeliveryFailure.Cancelled ->
        box {| reason = "delivery-cancelled" |}
    | DeliveryFailure.TransportFailed error ->
        box {| reason = "transport-failed"; ``exception`` = error.ToString() |}

let private resultDetails =
    function
    | Ok() -> box {| outcome = "succeeded" |}
    | Error reason -> box {| outcome = "failed"; reason = reason |}

let private failureDetails milestone =
    function
    | Failure.CreationFailed reason ->
        box {| reason = "creation-failed"; detail = reason |}
    | Failure.DiscoveryFailed ->
        box {| reason = "worktree-listing-failed" |}
    | Failure.WorktreeNotDiscovered count ->
        box {| reason = "worktree-missing-from-discovery"; discoveredCount = count |}
    | Failure.TerminalStartFailed reason ->
        box {| reason = "terminal-start-failed"; detail = reason |}
    | Failure.Unexpected error ->
        box {| reason = "unexpected-failure"; ``exception`` = error.ToString() |}
    | Failure.StartupFailed(failure, registration) ->
        let available, sessionIds, registeredWorktree =
            match registration with
            | RegistrationAvailability.Missing -> "missing", [], None
            | RegistrationAvailability.OtherSessions ids -> "other-sessions", ids, None
            | RegistrationAvailability.WrongWorktree path -> "wrong-worktree", [], Some path
            | RegistrationAvailability.Matching -> "matching", [], None
        let reason =
            match failure with
            | StartupPromptFailure.Rejected -> "startup-rejected"
            | StartupPromptFailure.TimedOut ->
                if fst (milestoneInfo milestone) >= fst (milestoneInfo Milestone.SendingPrompt) then
                    "startup-acceptance-not-observed"
                else
                    match registration with
                    | RegistrationAvailability.Missing -> "bridge-registration-not-observed"
                    | RegistrationAvailability.OtherSessions _ -> "no-matching-bridge-registration"
                    | RegistrationAvailability.WrongWorktree _ -> "bridge-registered-in-different-worktree"
                    | RegistrationAvailability.Matching -> "matching-bridge-not-dispatched"
        box
            {| reason = reason
               registration = available
               registeredWorktree = Option.toObj registeredWorktree
               otherSessionCount = sessionIds.Length
               otherSessionIds = sessionIds |> List.truncate LifecycleDiagnostics.maxListedValues |> List.map SessionId.value |}

type private Snapshot =
    { Sequence: int64
      Milestone: Milestone
      TerminalId: EmbeddedTerminalId option }

type internal Attempt
    (
        sessionId: SessionId,
        worktreePath: WorktreePath,
        elapsedMilliseconds: unit -> int64,
        write: string -> Result<unit, Log.WriteError>
    ) =
    let gate = obj ()
    // Bridge delivery can precede the terminal-start reply; serialize observations without regressing progress.
    let mutable snapshot =
        { Sequence = 0L; Milestone = Milestone.Requested; TerminalId = None }

    member _.SessionId = sessionId
    member _.WorktreePath = worktreePath

    member _.Record(event: Event) =
        lock gate (fun () ->
            let name, milestone, terminalId, details =
                match event with
                | Event.CreationRequested request ->
                    "creation-requested", None, None,
                    Some(box {| repository = request.RepoId
                                branch = BranchName.value request.BranchName
                                baseBranch = BranchName.value request.BaseBranch
                                originalPrompt = Option.toObj request.Prompt
                                skill = Option.toObj request.Skill |})
                | Event.Progress milestone ->
                    snd (milestoneInfo milestone), Some milestone, None, None
                | Event.PostForkSkipped ->
                    "post-fork-skipped", Some Milestone.PostForkCompleted, None, Some(box {| reason = "no-hook" |})
                | Event.PostForkCompleted result ->
                    "post-fork-completed", Some Milestone.PostForkCompleted, None, Some(resultDetails result)
                | Event.PostForkCrashed error ->
                    "post-fork-completed", Some Milestone.PostForkCompleted, None,
                    Some(box {| outcome = "failed"; ``exception`` = error.ToString() |})
                | Event.WorktreeDiscovered count ->
                    "worktree-discovered", Some Milestone.WorktreeDiscovered, None, Some(box {| count = count |})
                | Event.CommandStarting(provider, command, length, timeout) ->
                    "command-starting", Some Milestone.StartingCommand, None,
                    Some(box {| provider = string (provider |> Option.defaultValue CodingToolProvider.Default)
                                command = command
                                promptLength = length
                                acceptanceTimeoutMs = timeout.TotalMilliseconds |})
                | Event.TerminalStarted id ->
                    "terminal-started", Some Milestone.TerminalStarted, Some id, None
                | Event.PromptReserved id ->
                    "prompt-reserved", None, None, Some(box {| queueId = id |})
                | Event.BridgeMatched(identity, terminal) ->
                    let processDetails =
                        identity
                        |> Option.map (fun value ->
                            let pid, ticks = ProcessIdentity.sortKey value
                            box {| pid = pid; startTimeUtcTicks = ticks |})
                    "bridge-matched", Some Milestone.BridgeMatched, None,
                    Some(box {| ``process`` = Option.toObj processDetails
                                terminalOrigin = terminal |> Option.map TerminalSessionId.value |> Option.toObj |})
                | Event.PromptSending ->
                    "prompt-sending", Some Milestone.SendingPrompt, None, None
                | Event.PromptAccepted late ->
                    "prompt-accepted", Some Milestone.Accepted, None, Some(box {| afterCancellation = late |})
                | Event.DeliveryFailed failure ->
                    "prompt-delivery-failed", None, None, Some(deliveryFailureDetails failure)
                | Event.Failed failure ->
                    "failed", None, None, Some(failureDetails snapshot.Milestone failure)
                | Event.PromptRemoved -> "prompt-removed", None, None, None
                | Event.CleanupStarted -> "cleanup-started", None, None, None
                | Event.CleanupCompleted result ->
                    "cleanup-completed", None, None, Some(resultDetails result)
                | Event.LaunchCompleted result ->
                    "launch-completed", None, None, Some(resultDetails result)
                | Event.AutoLaunchSkipped -> "auto-launch-skipped", None, None, Some(box {| reason = "no-prompt" |})

            let nextMilestone =
                milestone
                |> Option.filter (fun next -> fst (milestoneInfo next) > fst (milestoneInfo snapshot.Milestone))
                |> Option.defaultValue snapshot.Milestone
            snapshot <-
                { Sequence = snapshot.Sequence + 1L
                  Milestone = nextMilestone
                  TerminalId = terminalId |> Option.orElse snapshot.TerminalId }

            let message =
                JsonSerializer.Serialize(
                    {| event = name
                       launchId = SessionId.value sessionId
                       worktree = WorktreePath.value worktreePath
                       sequence = snapshot.Sequence
                       elapsedMs = elapsedMilliseconds ()
                       lastMilestone = snd (milestoneInfo snapshot.Milestone)
                       terminalId = snapshot.TerminalId |> Option.map EmbeddedTerminalId.value |> Option.toObj
                       data = Option.toObj details |})
            write message
            |> Result.mapError (Log.reportWriteFailure "Launch" message))

    member this.Emit event = this.Record event |> ignore

let internal create sessionId worktreePath =
    let timer = Stopwatch.StartNew()
    Attempt(sessionId, worktreePath, (fun () -> timer.ElapsedMilliseconds), Log.tryLog "Launch")
