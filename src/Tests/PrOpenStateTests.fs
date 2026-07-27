module Tests.PrOpenStateTests

open NUnit.Framework
open Server.PrOpenState

/// The push decision reads a failed lookup as "unknown", never as "no open pull request", so these
/// cover the process-level failures no fixture can express: a provider CLI that is absent and one
/// that answers with a non-zero exit (how both `gh` and `az` report authentication failures).
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OpenPrLookupFailureTests() =

    let expectUnknown (result: Result<string, OpenPrState>) =
        match result with
        | Ok response -> Assert.Fail($"Expected an unknown state, got a response of {String.length response} chars")
        | Error state -> Assert.That(state, Is.EqualTo(UnknownPrState))

    [<Test>]
    member _.``A provider CLI that cannot start leaves the state unknown``() =
        runQuery "PR" "treemon-missing-provider-cli" [ "pr"; "list" ]
        |> Async.RunSynchronously
        |> expectUnknown

    [<Test>]
    member _.``A provider command that exits non-zero leaves the state unknown``() =
        runQuery "PR" "git" [ "rev-parse"; "--verify"; "treemon-missing-revision" ]
        |> Async.RunSynchronously
        |> expectUnknown
