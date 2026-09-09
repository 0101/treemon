module Tests.LifecycleDiagnosticsTests

open System
open NUnit.Framework
open Server
open Server.SessionActivity

let private processIdentity index =
    ProcessIdentity.create
        (10_000 + index)
        (1_000_000L + int64 index)
    |> Result.defaultWith invalidOp

let private sessionId index =
    SessionId.create $"session-{index:D2}"
    |> Result.defaultWith invalidOp

let private terminalSessionId index =
    TerminalSessionId.create $"{index:x32}"
    |> Result.defaultWith invalidOp

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LifecycleDiagnosticFormattingTests() =

    [<Test>]
    member _.``Multiplicity formatting reports full counts and bounds listed identities``() =
        let diagnostic =
            LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved
                { Boundary =
                    LifecycleDiagnostics.ObservationBoundary.Replacement
                  SessionId = sessionId 1
                  ProcessIdentities =
                    [ 1..12 ] |> List.map processIdentity
                  TerminalSessionIds =
                    [ 1..12 ] |> List.map terminalSessionId
                  UnattributedProcessCount = 3 }

        let formatted =
            LifecycleDiagnostics.format diagnostic

        Assert.Multiple(fun () ->
            Assert.That(formatted, Does.Contain("event=same_session_multiplicity"))
            Assert.That(formatted, Does.Contain("processes_count=12"))
            Assert.That(formatted, Does.Contain("processes_omitted=4"))
            Assert.That(formatted, Does.Contain("terminal_origins_count=12"))
            Assert.That(formatted, Does.Contain("terminal_origins_omitted=4"))
            Assert.That(formatted, Does.Contain("unattributed_process_count=3"))
            Assert.That(formatted.Length, Is.LessThan(2048)))

    [<Test>]
    member _.``Forward-failure diagnostics retain typed outcomes without failure text``() =
        let formatted =
            LifecycleDiagnostics.ReplacementStage.Failed(
                LifecycleDiagnostics.ReplacementFailureKind.CommandDelivery,
                LifecycleDiagnostics.ReplacementHostOutcome.StagedHostRetained,
                Some(processIdentity 7)
            )
            |> LifecycleDiagnostics.Diagnostic.ReplacementTransition
            |> LifecycleDiagnostics.format

        Assert.Multiple(fun () ->
            Assert.That(formatted, Does.Contain("event=replacement stage=failed"))
            Assert.That(formatted, Does.Contain("failure=command_delivery"))
            Assert.That(formatted, Does.Contain("host_state=staged_host_retained"))
            Assert.That(formatted, Does.Contain("host_process=10007@1000007"))
            Assert.That(formatted.Length, Is.LessThan(2048)))

    [<Test>]
    member _.``Post-stop failure diagnostics report no current host without an identity``() =
        let formatted =
            LifecycleDiagnostics.ReplacementStage.Failed(
                LifecycleDiagnostics.ReplacementFailureKind.StagedHostLaunch,
                LifecycleDiagnostics.ReplacementHostOutcome.NoHostRunning,
                None
            )
            |> LifecycleDiagnostics.Diagnostic.ReplacementTransition
            |> LifecycleDiagnostics.format

        Assert.Multiple(fun () ->
            Assert.That(formatted, Does.Contain("failure=staged_host_launch"))
            Assert.That(formatted, Does.Contain("host_state=no_host_running"))
            Assert.That(formatted, Does.Contain("host_process=none")))
