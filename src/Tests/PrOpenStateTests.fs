module Tests.PrOpenStateTests

open NUnit.Framework
open Server.PrOpenState
open Server.GithubPrStatus

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


/// GitHub filters open pull requests by `owner:branch`, so in a fork workflow - where the upstream
/// repository the pull request targets is owned by someone else entirely - only the owner of the
/// branch's *own* remote returns the pull request. Filtering under the upstream owner would answer
/// an empty list, which the classifier reads as a confirmed absence.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GithubHeadOwnerResolutionTests() =

    let repoRoot = @"Q:\code\treemon"
    let branch = "feature/sync"
    let upstream = { Owner = "upstreamowner"; Repo = "treemon" }

    /// Stands in for the two git reads: the remote the branch is configured to track, then that
    /// remote's URL, keyed by the remote name the resolver asked for. Fetch and push URLs answer
    /// from separate maps, so a resolver reading the wrong one fails rather than quietly returning
    /// an owner the push would never reach.
    let resolveWith
        (branchRemote: Result<string, OpenPrState>)
        (fetchUrls: Map<string, Result<string, OpenPrState>>)
        (pushUrls: Map<string, Result<string, OpenPrState>>)
        =
        let read (arguments: string list) =
            async {
                if arguments |> List.exists _.StartsWith("branch.") then
                    return branchRemote
                else
                    let urls =
                        if arguments |> List.contains "--push" then pushUrls else fetchUrls

                    return
                        urls
                        |> Map.tryFind (List.last arguments)
                        |> Option.defaultValue (Error UnknownPrState)
            }

        Server.PrStatus.resolveHeadOwnerWith read repoRoot branch
        |> Async.RunSynchronously

    /// The common remote: one URL serves both fetch and push.
    let resolve (branchRemote: Result<string, OpenPrState>) (remoteUrls: Map<string, Result<string, OpenPrState>>) =
        resolveWith branchRemote remoteUrls remoteUrls

    /// Asserted by shape rather than against a `Result` literal: the literal's other type argument
    /// infers as `obj`, which NUnit's structural compare never matches (see `TestUtils.assertOk`).
    let expectOwner (expected: string) (result: Result<string, OpenPrState>) =
        match result with
        | Ok owner -> Assert.That(owner, Is.EqualTo(expected))
        | Error state -> Assert.Fail($"Expected the head owner {expected}, got {state}")

    let expectUnknown (result: Result<string, OpenPrState>) =
        match result with
        | Ok owner -> Assert.Fail($"Expected an unknown state, got the head owner {owner}")
        | Error state -> Assert.That(state, Is.EqualTo(UnknownPrState))

    [<Test>]
    member _.``The branch's own remote is read for that branch``() =
        Assert.That(
            Server.PrStatus.branchRemoteArgs repoRoot branch,
            Is.EqualTo([ "-C"; repoRoot; "config"; "--get"; "branch.feature/sync.remote" ]))

    [<Test>]
    member _.``The remote is read for the URL a push would use``() =
        Assert.That(
            Server.PrStatus.remotePushUrlArgs repoRoot "origin",
            Is.EqualTo([ "-C"; repoRoot; "remote"; "get-url"; "--push"; "origin" ]))

    [<Test>]
    member _.``A remote pushing to a fork resolves the fork owner, not its fetch URL's owner``() =
        resolveWith
            (Ok "origin")
            (Map [ "origin", Ok "https://github.com/upstreamowner/treemon.git" ])
            (Map [ "origin", Ok "https://github.com/forkowner/treemon.git" ])
        |> expectOwner "forkowner"

    [<Test>]
    member _.``A remote with no readable push destination leaves the state unknown``() =
        resolveWith (Ok "origin") (Map [ "origin", Ok "https://github.com/upstreamowner/treemon.git" ]) Map.empty
        |> expectUnknown

    [<Test>]
    member _.``A remote pushing somewhere that is not GitHub leaves the state unknown``() =
        resolveWith
            (Ok "origin")
            (Map [ "origin", Ok "https://github.com/upstreamowner/treemon.git" ])
            (Map [ "origin", Ok "https://dev.azure.com/org/project/_git/treemon" ])
        |> expectUnknown

    [<Test>]
    member _.``A fork-owned head branch is queried under the fork, not the upstream repository``() =
        let result =
            resolve (Ok "fork") (Map [ "fork", Ok "https://github.com/forkowner/treemon.git" ])

        match result with
        | Error state -> Assert.Fail($"Expected a resolved head owner, got {state}")
        | Ok headOwner ->
            let path = openPrQueryArgs upstream headOwner branch |> List.last

            Assert.That(path, Does.Contain("/repos/upstreamowner/treemon/pulls?"))
            Assert.That(path, Does.Contain("head=forkowner:feature%2Fsync"))

    [<Test>]
    member _.``A branch published to the upstream repository keeps the upstream owner``() =
        resolve (Ok "origin") (Map [ "origin", Ok "https://github.com/upstreamowner/treemon" ])
        |> expectOwner "upstreamowner"

    [<Test>]
    member _.``A branch git records no remote for leaves the state unknown``() =
        resolve (Error UnknownPrState) Map.empty |> expectUnknown

    [<Test>]
    member _.``An unreadable remote URL leaves the state unknown``() =
        resolve (Ok "origin") Map.empty |> expectUnknown

    [<Test>]
    member _.``A remote that is not a GitHub repository leaves the state unknown``() =
        resolve (Ok "origin") (Map [ "origin", Ok "https://dev.azure.com/org/project/_git/treemon" ])
        |> expectUnknown
