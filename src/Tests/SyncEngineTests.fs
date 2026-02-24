module Tests.SyncEngineTests

open System
open System.Threading
open NUnit.Framework
open Shared
open Server.SyncEngine

let private emptyState : SyncAgentState =
    { Processes = Map.empty
      Events = Map.empty }

let private makeSyncState (processes: (string * SyncProcess) list) (events: (string * CardEvent list) list) : SyncAgentState =
    { Processes = processes |> Map.ofList
      Events = events |> Map.ofList }

let private makeRunningProcess () : SyncProcess * CancellationTokenSource =
    let cts = new CancellationTokenSource()
    { State = SyncState.Running SyncStep.CheckClean
      CancellationTokenSource = cts }, cts

let private makeEvent source message status : CardEvent =
    { Source = source
      Message = message
      Timestamp = DateTimeOffset.UtcNow
      Status = Some status
      Duration = None }

/// Processes a BeginSync message through a one-shot MailboxProcessor to obtain
/// a real AsyncReplyChannel. Returns (replyValue, newState, sideEffects).
let private processBeginSync (state: SyncAgentState) (branch: string) =
    let capturedState = ref emptyState
    let capturedEffects = ref []

    let agent =
        MailboxProcessor<SyncMsg>.Start(fun inbox ->
            async {
                let! msg = inbox.Receive()
                let newState, effects = processMessage state msg
                capturedState.Value <- newState
                capturedEffects.Value <- effects
            })

    let replyResult =
        agent.PostAndAsyncReply(fun rc -> BeginSync(branch, rc))
        |> Async.RunSynchronously

    replyResult, capturedState.Value, capturedEffects.Value

/// Processes a GetAllEvents message through a one-shot MailboxProcessor.
/// Returns (eventsMap, newState, sideEffects).
let private processGetAllEvents (state: SyncAgentState) =
    let capturedState = ref emptyState
    let capturedEffects = ref []

    let agent =
        MailboxProcessor<SyncMsg>.Start(fun inbox ->
            async {
                let! msg = inbox.Receive()
                let newState, effects = processMessage state msg
                capturedState.Value <- newState
                capturedEffects.Value <- effects
            })

    let replyResult =
        agent.PostAndAsyncReply(fun rc -> GetAllEvents rc)
        |> Async.RunSynchronously

    replyResult, capturedState.Value, capturedEffects.Value

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProcessMessageTests() =

    [<Test>]
    member _.``BeginSync on idle branch returns Ok with new process and initial event``() =
        let replyResult, newState, effects = processBeginSync emptyState "feature"

        Assert.That(replyResult |> Result.isOk, Is.True, "Reply should be Ok")
        Assert.That(newState.Processes.ContainsKey("feature"), Is.True, "Should have process for branch")
        Assert.That(newState.Events.ContainsKey("feature"), Is.True, "Should have events for branch")
        let featureEvents = newState.Events |> Map.find "feature"
        Assert.That(featureEvents.Length, Is.EqualTo(1), "Should have one initial event")
        Assert.That(featureEvents.[0].Source, Is.EqualTo("sync"))
        Assert.That(featureEvents.[0].Status, Is.EqualTo(Some StepStatus.Running))
        Assert.That(effects, Is.Empty, "BeginSync should not produce side effects")

    [<Test>]
    member _.``BeginSync on already running branch returns Error and unchanged state``() =
        let sp, _cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ] []

        let replyResult, newState, effects = processBeginSync state "feature"

        Assert.That(replyResult |> Result.isError, Is.True, "Reply should be Error")
        Assert.That(newState.Processes, Is.EqualTo(state.Processes), "Processes should be unchanged")
        Assert.That(effects, Is.Empty)

    [<Test>]
    member _.``CompleteSync on running branch returns updated state with DisposeCts effect``() =
        let sp, cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ] [ "feature", [ makeEvent "sync" "running" StepStatus.Running ] ]

        let newState, effects = processMessage state (CompleteSync("feature", StepStatus.Succeeded))

        Assert.That(effects.Length, Is.EqualTo(1), "Should have one side effect")
        match effects.[0] with
        | DisposeCts disposedCts -> Assert.That(Object.ReferenceEquals(disposedCts, cts), Is.True, "Should dispose the original CTS")
        | other -> Assert.Fail($"Expected DisposeCts but got {other}")

        let runningEvents =
            newState.Events
            |> Map.tryFind "feature"
            |> Option.defaultValue []
            |> List.filter (fun e -> e.Status = Some StepStatus.Running)

        Assert.That(runningEvents, Is.Empty, "Running events should be cleared")

    [<Test>]
    member _.``CompleteSync on already completed branch is idempotent``() =
        let cts = new CancellationTokenSource()
        let sp = { State = SyncState.Completed StepStatus.Succeeded; CancellationTokenSource = cts }
        let state = makeSyncState [ "feature", sp ] []

        let newState, effects = processMessage state (CompleteSync("feature", StepStatus.Failed "oops"))

        Assert.That(effects, Is.Empty, "Should not produce side effects for already-completed")
        Assert.That(newState.Processes, Is.EqualTo(state.Processes), "State should be unchanged")

    [<Test>]
    member _.``CompleteSync on unknown branch is no-op``() =
        let newState, effects = processMessage emptyState (CompleteSync("ghost", StepStatus.Succeeded))

        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))

    [<Test>]
    member _.``CancelSync on running branch emits CancelCts and DisposeCts``() =
        // The spec says CancelSync should emit ONLY CancelCts (not DisposeCts).
        // This test documents the CURRENT behavior before the bug fix in tm-sync-58a.
        // After that fix, this test should assert effects.Length = 1 with only CancelCts.
        let sp, cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ] []

        let newState, effects = processMessage state (CancelSync "feature")

        Assert.That(effects.Length, Is.EqualTo(2), "Current behavior: CancelCts + DisposeCts (pre-fix)")

        let hasCancelCts =
            effects |> List.exists (function CancelCts c -> Object.ReferenceEquals(c, cts) | _ -> false)
        Assert.That(hasCancelCts, Is.True, "Should emit CancelCts for the running CTS")

        Assert.That((newState.Processes |> Map.find "feature").State, Is.EqualTo(SyncState.Cancelled))
        Assert.That(newState.Events.ContainsKey("feature"), Is.True)

    [<Test>]
    member _.``CancelSync on non-running branch is no-op``() =
        let cts = new CancellationTokenSource()
        let sp = { State = SyncState.Completed StepStatus.Succeeded; CancellationTokenSource = cts }
        let state = makeSyncState [ "feature", sp ] []

        let newState, effects = processMessage state (CancelSync "feature")

        Assert.That(effects, Is.Empty)
        Assert.That(newState.Processes, Is.EqualTo(state.Processes))

    [<Test>]
    member _.``CancelSync on unknown branch is no-op``() =
        let newState, effects = processMessage emptyState (CancelSync "ghost")

        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))

    [<Test>]
    member _.``PushEvent appends event to branch list``() =
        let event1 = makeEvent "sync" "first" StepStatus.Running
        let state = makeSyncState [] [ "feature", [ event1 ] ]

        let event2 = makeEvent "sync" "second" StepStatus.Succeeded
        let newState, effects = processMessage state (PushEvent("feature", event2))

        Assert.That(effects, Is.Empty)
        let events = newState.Events |> Map.find "feature"
        Assert.That(events.Length, Is.EqualTo(2))
        Assert.That(events.[0].Message, Is.EqualTo("second"), "New event should be prepended (cons)")
        Assert.That(events.[1].Message, Is.EqualTo("first"))

    [<Test>]
    member _.``PushEvent creates branch entry if not present``() =
        let event = makeEvent "sync" "hello" StepStatus.Running
        let newState, effects = processMessage emptyState (PushEvent("feature", event))

        Assert.That(effects, Is.Empty)
        Assert.That(newState.Events.ContainsKey("feature"), Is.True)
        Assert.That((newState.Events |> Map.find "feature").Length, Is.EqualTo(1))

    [<Test>]
    member _.``GetAllEvents returns full events map via reply``() =
        let events =
            [ "feature", [ makeEvent "sync" "a" StepStatus.Running ]
              "main", [ makeEvent "sync" "b" StepStatus.Succeeded ] ]

        let state = makeSyncState [] events

        let replyResult, newState, effects = processGetAllEvents state

        Assert.That(replyResult.Count, Is.EqualTo(2))
        Assert.That(replyResult.ContainsKey("feature"), Is.True)
        Assert.That(replyResult.ContainsKey("main"), Is.True)
        Assert.That(effects, Is.Empty)
        Assert.That(newState.Events, Is.EqualTo(state.Events), "State should be unchanged")

    [<Test>]
    member _.``UpdateProcessState updates existing process``() =
        let sp, _cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ] []

        let newState, effects = processMessage state (UpdateProcessState("feature", SyncState.Running SyncStep.Pull))

        Assert.That(effects, Is.Empty)
        Assert.That((newState.Processes |> Map.find "feature").State, Is.EqualTo(SyncState.Running SyncStep.Pull))

    [<Test>]
    member _.``UpdateProcessState on unknown branch is no-op``() =
        let newState, effects = processMessage emptyState (UpdateProcessState("ghost", SyncState.Running SyncStep.Pull))

        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))
