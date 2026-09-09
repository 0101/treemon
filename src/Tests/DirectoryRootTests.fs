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


/// The declared repository names a directory Treemon runs git in, and it arrives from a file an
/// agent writes. These cover what it is allowed to name.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DeclaredRepoTests() =

    let stateDeclaring (repo: string option) =
        { DirectoryRoot.Label = "infra"
          DirectoryRoot.Summary = ""
          DirectoryRoot.UpdatedAt = DateTimeOffset.MinValue
          DirectoryRoot.Busy = false
          DirectoryRoot.Repo = repo }

    /// A folder holding a checkout, the shape an agent working across repositories has.
    let withRepoUnder (relative: string) (action: string -> unit) =
        withTempDir "declared-repo" (fun directory ->
            let repo = Path.Combine(directory, relative)
            Directory.CreateDirectory(Path.Combine(repo, ".git")) |> ignore
            action directory)

    [<Test>]
    member _.``a folder that declares a repository beneath it resolves to that repository``() =
        withRepoUnder "tenancy" (fun directory ->
            let resolved = DirectoryRoot.declaredRepo directory (stateDeclaring (Some "tenancy"))

            Assert.That(
                resolved |> Option.map PathUtils.normalizePath,
                Is.EqualTo(Some(PathUtils.normalizePath (Path.Combine(directory, "tenancy"))))
            ))

    [<Test>]
    member _.``a folder that declares no repository speaks only for itself``() =
        withRepoUnder "tenancy" (fun directory ->
            Assert.That(DirectoryRoot.declaredRepo directory (stateDeclaring None), Is.EqualTo None))

    // Naming something git does not recognise declares nothing, rather than pointing a git probe at
    // an arbitrary directory every refresh.
    [<Test>]
    member _.``a declared path that is not a worktree declares nothing``() =
        withRepoUnder "tenancy" (fun directory ->
            Directory.CreateDirectory(Path.Combine(directory, "notes")) |> ignore

            Assert.Multiple(fun () ->
                Assert.That(DirectoryRoot.declaredRepo directory (stateDeclaring (Some "notes")), Is.EqualTo None)
                Assert.That(DirectoryRoot.declaredRepo directory (stateDeclaring (Some "absent")), Is.EqualTo None)))

    // The value is written by an agent and names a directory Treemon runs git in, so it has to stay
    // inside the folder that declared it.
    [<Test>]
    member _.``a declared path may not escape the folder``() =
        withRepoUnder "tenancy" (fun directory ->
            let escapes =
                [ ".."
                  Path.Combine("..", "..", "etc")
                  Path.Combine("tenancy", "..", "..")
                  if OperatingSystem.IsWindows() then @"C:\Windows" else "/etc" ]

            Assert.Multiple(fun () ->
                for escape in escapes do
                    Assert.That(
                        DirectoryRoot.declaredRepo directory (stateDeclaring (Some escape)),
                        Is.EqualTo None,
                        $"'{escape}' must not resolve"
                    )))

    // The folder itself is not the repository it is working in; treating it as one would send git at
    // a directory that has already declined to be a repository.
    [<Test>]
    member _.``a folder may not declare itself``() =
        withRepoUnder "tenancy" (fun directory ->
            Assert.Multiple(fun () ->
                Assert.That(DirectoryRoot.declaredRepo directory (stateDeclaring (Some ".")), Is.EqualTo None)
                Assert.That(DirectoryRoot.declaredRepo directory (stateDeclaring (Some "")), Is.EqualTo None)))

    [<Test>]
    member _.``a nested repository path is read from the file``() =
        withRepoUnder (Path.Combine("git", "Centro")) (fun directory ->
            let state = stateDeclaring (Some "git/Centro")

            Assert.That(
                DirectoryRoot.declaredRepo directory state |> Option.map PathUtils.normalizePath,
                Is.EqualTo(Some(PathUtils.normalizePath (Path.Combine(directory, "git", "Centro"))))
            ))

    // A folder that sits above its repositories wants only to say which one its session belongs to.
    // Requiring a label there would make it name a card that is never drawn.
    [<Test>]
    member _.``a folder may declare a repository without asking for a card``() =
        withTempDir "declared-repo-only" (fun directory ->
            Directory.CreateDirectory(Path.Combine(directory, "git", "Centro", ".git")) |> ignore

            File.WriteAllText(
                Path.Combine(directory, DirectoryRoot.StateFileName),
                """{ "repo": "git/Centro" }"""
            )

            match DirectoryRoot.tryReadState directory with
            | None -> Assert.Fail "a folder declaring only a repository still declares something"
            | Some state ->
                Assert.Multiple(fun () ->
                    Assert.That(state.Repo, Is.EqualTo(Some "git/Centro"))
                    Assert.That(DirectoryRoot.describesCard state, Is.False, "no label, so no card")
                    Assert.That(DirectoryRoot.declaredRepo directory state, Is.Not.EqualTo None)))

    [<Test>]
    member _.``a folder that declares neither a label nor a repository declares nothing``() =
        withTempDir "declares-nothing" (fun directory ->
            File.WriteAllText(
                Path.Combine(directory, DirectoryRoot.StateFileName),
                """{ "summary": "busy elsewhere", "busy": true }"""
            )

            Assert.That(DirectoryRoot.tryReadState directory, Is.EqualTo None))

    // This runs on every report from a folder no worktree encloses, so a value the path APIs refuse
    // would not fail one request but every request that agent makes.
    [<Test>]
    member _.``a value the path APIs refuse declares nothing rather than throwing``() =
        withRepoUnder "tenancy" (fun directory ->
            let hostile =
                [ "a" + string (char 0) + "b"
                  String.replicate 300 "tenancy/"
                  "tenancy" + string (char 0) ]

            Assert.Multiple(fun () ->
                for value in hostile do
                    Assert.That(
                        DirectoryRoot.declaredRepo directory (stateDeclaring (Some value)),
                        Is.EqualTo None,
                        $"a hostile value of {value.Length} chars must not throw"
                    )))
