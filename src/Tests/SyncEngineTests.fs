module Tests.SyncEngineTests

open System
open System.Threading
open NUnit.Framework
open Shared
open Server.SyncEngine

let private emptyState : SyncAgentState = { Processes = Map.empty }

let private makeSyncState (processes: (string * SyncProcess) list) : SyncAgentState =
    { Processes = processes |> Map.ofList }

let private makeRunningProcess () : SyncProcess * CancellationTokenSource =
    let cts = new CancellationTokenSource()
    { State = SyncState.Running SyncStep.CheckClean
      CancellationTokenSource = cts }, cts

/// Runs a message that carries an AsyncReplyChannel through a one-shot
/// MailboxProcessor to obtain a real channel. Returns (replyValue, newState, sideEffects).
let private processWithReply (state: SyncAgentState) (build: AsyncReplyChannel<'reply> -> SyncMsg) =
    let capturedState = ref emptyState
    let capturedEffects = ref []
    let processed = new ManualResetEventSlim(false)

    let agent =
        MailboxProcessor<SyncMsg>.Start(fun inbox ->
            async {
                let! msg = inbox.Receive()
                let newState, effects = processMessage state msg
                capturedState.Value <- newState
                capturedEffects.Value <- effects
                processed.Set()
            })

    let replyResult = agent.PostAndAsyncReply(build) |> Async.RunSynchronously
    processed.Wait()
    replyResult, capturedState.Value, capturedEffects.Value

let private processBeginSync (state: SyncAgentState) (branch: string) =
    processWithReply state (fun rc -> BeginSync(branch, rc))

let private processCancelSync (state: SyncAgentState) (branch: string) =
    processWithReply state (fun rc -> CancelSync(branch, rc))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ProcessMessageTests() =

    [<Test>]
    member _.``BeginSync on idle branch returns Ok with a running process``() =
        let replyResult, newState, effects = processBeginSync emptyState "feature"

        Assert.That(replyResult |> Result.isOk, Is.True, "Reply should be Ok")
        Assert.That(newState.Processes.ContainsKey("feature"), Is.True, "Should have process for branch")
        match (newState.Processes |> Map.find "feature").State with
        | SyncState.Running _ -> ()
        | other -> Assert.Fail($"Expected Running process but got {other}")
        Assert.That(effects, Is.Empty, "BeginSync should not produce side effects")

    [<Test>]
    member _.``BeginSync on already running branch returns Error and unchanged state``() =
        let sp, _cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ]

        let replyResult, newState, effects = processBeginSync state "feature"

        Assert.That(replyResult |> Result.isError, Is.True, "Reply should be Error")
        Assert.That(newState.Processes, Is.EqualTo(state.Processes), "Processes should be unchanged")
        Assert.That(effects, Is.Empty)

    [<Test>]
    member _.``CompleteSync on running branch removes process and emits DisposeCts``() =
        let sp, cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ]

        let newState, effects = processMessage state (CompleteSync "feature")

        Assert.That(effects.Length, Is.EqualTo(1), "Should have one side effect")
        match effects[0] with
        | DisposeCts disposedCts -> Assert.That(Object.ReferenceEquals(disposedCts, cts), Is.True, "Should dispose the original CTS")
        | other -> Assert.Fail($"Expected DisposeCts but got {other}")

        Assert.That(newState.Processes.ContainsKey("feature"), Is.False, "Completed process should be removed from state")

    [<Test>]
    member _.``CompleteSync on already completed branch is idempotent``() =
        let cts = new CancellationTokenSource()
        let sp = { State = SyncState.Completed StepStatus.Succeeded; CancellationTokenSource = cts }
        let state = makeSyncState [ "feature", sp ]

        let newState, effects = processMessage state (CompleteSync "feature")

        Assert.That(effects, Is.Empty, "Should not produce side effects for already-completed")
        Assert.That(newState.Processes, Is.EqualTo(state.Processes), "State should be unchanged")

    [<Test>]
    member _.``CompleteSync on unknown branch is no-op``() =
        let newState, effects = processMessage emptyState (CompleteSync "ghost")

        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))

    [<Test>]
    member _.``CancelSync on running branch replies true, emits LogMessage and CancelCts``() =
        let sp, cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ]

        let reply, newState, effects = processCancelSync state "feature"

        Assert.That(reply, Is.True, "Should report that a running sync was cancelled")
        Assert.That(effects.Length, Is.EqualTo(2), "Should emit LogMessage and CancelCts")
        match effects[0] with
        | LogMessage msg -> Assert.That(msg, Does.Contain("feature"), "Log message should mention branch")
        | other -> Assert.Fail($"Expected LogMessage but got {other}")
        match effects[1] with
        | CancelCts cancelledCts -> Assert.That(Object.ReferenceEquals(cancelledCts, cts), Is.True, "Should cancel the original CTS")
        | other -> Assert.Fail($"Expected CancelCts but got {other}")

        Assert.That((newState.Processes |> Map.find "feature").State, Is.EqualTo(SyncState.Cancelled))

    [<Test>]
    member _.``CancelSync on non-running branch replies false and is a no-op``() =
        let cts = new CancellationTokenSource()
        let sp = { State = SyncState.Completed StepStatus.Succeeded; CancellationTokenSource = cts }
        let state = makeSyncState [ "feature", sp ]

        let reply, newState, effects = processCancelSync state "feature"

        Assert.That(reply, Is.False)
        Assert.That(effects, Is.Empty)
        Assert.That(newState.Processes, Is.EqualTo(state.Processes))

    [<Test>]
    member _.``CancelSync on unknown branch replies false and is a no-op``() =
        let reply, newState, effects = processCancelSync emptyState "ghost"

        Assert.That(reply, Is.False)
        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))

    [<Test>]
    member _.``UpdateProcessState updates existing process``() =
        let sp, _cts = makeRunningProcess ()
        let state = makeSyncState [ "feature", sp ]

        let newState, effects = processMessage state (UpdateProcessState("feature", SyncState.Running SyncStep.Pull))

        Assert.That(effects, Is.Empty)
        Assert.That((newState.Processes |> Map.find "feature").State, Is.EqualTo(SyncState.Running SyncStep.Pull))

    [<Test>]
    member _.``UpdateProcessState on unknown branch is no-op``() =
        let newState, effects = processMessage emptyState (UpdateProcessState("ghost", SyncState.Running SyncStep.Pull))

        Assert.That(effects, Is.Empty)
        Assert.That(newState, Is.EqualTo(emptyState))
