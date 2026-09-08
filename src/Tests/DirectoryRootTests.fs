module Tests.DirectoryRootTests

open System
open System.IO
open NUnit.Framework
open Server
open Tests.TestUtils

/// The state file is written by an agent and rendered on a dashboard, so these cover what a folder
/// can put in it — including the shapes a half-written or hostile file takes.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DirectoryRootTests() =

    let withState (json: string) (action: string -> unit) =
        withTempDir "directory-root" (fun directory ->
            File.WriteAllText(Path.Combine(directory, DirectoryRoot.StateFileName), json)
            action directory)

    [<Test>]
    member _.``a declared folder reports its label, summary, time and busy flag``() =
        withState
            """{ "label": "PROJ-142", "summary": "Closed 3 tickets", "updatedAt": "2026-09-08T10:00:00Z", "busy": true }"""
            (fun directory ->
                match DirectoryRoot.tryReadState directory with
                | None -> Assert.Fail "expected the declared state to be read"
                | Some state ->
                    Assert.Multiple(fun () ->
                        Assert.That(state.Label, Is.EqualTo "PROJ-142")
                        Assert.That(state.Summary, Is.EqualTo "Closed 3 tickets")
                        Assert.That(state.UpdatedAt, Is.EqualTo(DateTimeOffset.Parse "2026-09-08T10:00:00Z"))
                        Assert.That(state.Busy, Is.True)))

    [<Test>]
    member _.``a folder that declares nothing is not a card``() =
        withTempDir "directory-root-absent" (fun directory ->
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    // A half-written file is indistinguishable from an absent one for this purpose, and throwing
    // here would take the whole repository's discovery with it.
    [<Test>]
    member _.``a malformed or truncated file declares nothing rather than throwing``() =
        withState """{ "label": "PROJ-142", "summ""" (fun directory ->
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    [<Test>]
    member _.``a file with no usable label declares nothing``() =
        withState """{ "summary": "work happened", "busy": true }""" (fun directory ->
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    [<Test>]
    member _.``a blank label is no label``() =
        withState """{ "label": "   " }""" (fun directory ->
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    // Everything here reaches a dashboard, so length is bounded at the boundary rather than trusted.
    [<Test>]
    member _.``over-long text is capped``() =
        let long = String('x', 5_000)

        withState $"""{{ "label": "{long}", "summary": "{long}" }}""" (fun directory ->
            match DirectoryRoot.tryReadState directory with
            | None -> Assert.Fail "expected the declared state to be read"
            | Some state ->
                Assert.Multiple(fun () ->
                    Assert.That(state.Label.Length, Is.LessThanOrEqualTo 120)
                    Assert.That(state.Summary.Length, Is.LessThanOrEqualTo 500)))

    [<Test>]
    member _.``a missing or unparseable timestamp does not discard the rest``() =
        withState """{ "label": "PROJ-142", "updatedAt": "not-a-date" }""" (fun directory ->
            match DirectoryRoot.tryReadState directory with
            | None -> Assert.Fail "expected the declared state to be read"
            | Some state ->
                Assert.Multiple(fun () ->
                    Assert.That(state.Label, Is.EqualTo "PROJ-142")
                    Assert.That(state.UpdatedAt, Is.EqualTo DateTimeOffset.MinValue)))

    // A state file dropped - or committed - into a repository must not quietly replace the branch,
    // dirty flag and PR its card is built from, for this checkout or anyone else's.
    [<Test>]
    member _.``a state file inside a git worktree is ignored, because git is authoritative there``() =
        withState """{ "label": "hijacked" }""" (fun directory ->
            Directory.CreateDirectory(Path.Combine(directory, ".git")) |> ignore
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    [<Test>]
    member _.``a linked worktree, whose .git is a file, is also authoritative``() =
        withState """{ "label": "hijacked" }""" (fun directory ->
            File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: /elsewhere/.git/worktrees/x")
            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    // The path becomes a known path, and a report naming this directory is matched against it after
    // normalization; the two spellings differ on Windows.
    [<Test>]
    member _.``the declared worktree path is normalized, so reports can match it``() =
        withState """{ "label": "PROJ-142" }""" (fun directory ->
            let state = (DirectoryRoot.tryReadState directory).Value
            let unnormalized = directory + string Path.DirectorySeparatorChar

            Assert.Multiple(fun () ->
                Assert.That(
                    (DirectoryRoot.worktreeInfo unnormalized state).Path,
                    Is.EqualTo(PathUtils.normalizePath directory))

                Assert.That(
                    (DirectoryRoot.gitData unnormalized state).Path,
                    Is.EqualTo(PathUtils.normalizePath directory))))

    // The whole point of the design: the card reads the record it always reads.
    [<Test>]
    member _.``the declared state fills the record a card already reads``() =
        withState
            """{ "label": "PROJ-142", "summary": "Closed 3 tickets", "updatedAt": "2026-09-08T10:00:00Z", "busy": true }"""
            (fun directory ->
                let state = (DirectoryRoot.tryReadState directory).Value
                let data = DirectoryRoot.gitData directory state
                let worktree = DirectoryRoot.worktreeInfo directory state

                Assert.Multiple(fun () ->
                    Assert.That(worktree.Path, Is.EqualTo(PathUtils.normalizePath directory))
                    Assert.That(worktree.Branch, Is.EqualTo(Some "PROJ-142"))
                    Assert.That(data.Branch, Is.EqualTo "PROJ-142")
                    Assert.That(data.LastCommitMessage, Is.EqualTo "Closed 3 tickets")
                    Assert.That(data.IsDirty, Is.True)
                    // Nothing git-shaped is invented.
                    Assert.That(data.Upstream, Is.EqualTo GitWorktree.NoUpstream)
                    Assert.That(data.MainBehindCount, Is.EqualTo 0)
                    Assert.That(data.WorkMetrics, Is.EqualTo None)))
