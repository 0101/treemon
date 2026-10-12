module Tests.WorktreeFocusTests

open System
open NUnit.Framework
open Shared
open Navigation
open OverviewData
open WorktreeFocus
open Tests.WorktreeFixtures

let private now = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)

let private worktree name activity =
    { baseWt with
        Path = WorktreePath name
        Branch = name
        SessionActivityAt = activity }

let private repo worktrees archived : RepoModel =
    { RepoId = RepoId "repo"
      Name = "repo"
      Worktrees = worktrees
      ArchivedWorktrees = archived
      IsReady = true
      IsCollapsed = false
      Provider = None
      BaseBranch = "main" }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorktreeFocusTests() =

    [<Test>]
    member _.``Focus cycle exposes the named windows and returns to the unfiltered state``() =
        let cycle =
            [ 1 .. 4 ]
            |> List.scan (fun current _ -> nextWindow current) Window.Off

        Assert.That(cycle |> List.map label, Is.EqualTo([ "off"; "2h"; "8h"; "24h"; "off" ]))

    [<Test>]
    member _.``Off preserves all repositories card lists order and metadata``() =
        let source =
            [ repo
                [ worktree "unknown" None
                  worktree "old" (Some(now.AddDays(-30.0))) ]
                [ { worktree "archive" None with IsArchived = true } ]
              { repo [] [] with RepoId = RepoId "empty"; IsCollapsed = true; IsReady = false } ]

        Assert.That(project Window.Off now source, Is.EqualTo source)

    [<Test>]
    member _.``Each enabled window uses an inclusive elapsed-hour cutoff``() =
        [ Window.Hours2, 2.0; Window.Hours8, 8.0; Window.Hours24, 24.0 ]
        |> List.iter (fun (window, hours) ->
            let cutoff = now.AddHours(-hours)
            let source =
                [ repo
                    [ worktree "before" (Some(cutoff.AddMilliseconds(-1.0)))
                      worktree "edge" (Some cutoff)
                      worktree "after" (Some(cutoff.AddMilliseconds 1.0))
                      worktree "unknown" None ]
                    [] ]

            Assert.That(
                (project window now source).Head.Worktrees |> List.map _.Branch,
                Is.EqualTo([ "edge"; "after" ]),
                label window))

    [<Test>]
    member _.``Session activity rather than commit time status or terminal presence decides recency``() =
        let recentClosed =
            { worktree "closed" (Some(now.AddMinutes(-30.0))) with
                CodingTool = NoSession
                LastCommitTime = now.AddDays(-30.0) }
        let oldOpen =
            { worktree "open" (Some(now.AddHours(-3.0))) with
                CodingTool = Working
                HasActiveSession = true
                CodingToolSince = Some now
                LastCommitTime = now }
        let unknownOpen =
            { worktree "unknown" None with CodingTool = Idle; HasActiveSession = true }

        Assert.That(
            (project Window.Hours2 now [ repo [ oldOpen; recentClosed; unknownOpen ] [] ]).Head.Worktrees
            |> List.map _.Branch,
            Is.EqualTo([ "closed" ]))

    [<Test>]
    member _.``Archive cards use the same cutoff while empty repository controls survive``() =
        let source =
            [ { repo
                    [ worktree "old" (Some(now.AddDays(-2.0))) ]
                    [ { worktree "recent-archive" (Some now) with IsArchived = true }
                      { worktree "old-archive" (Some(now.AddDays(-2.0))) with IsArchived = true } ]
                with IsCollapsed = true } ]
        let projected = project Window.Hours2 now source

        Assert.Multiple(fun () ->
            Assert.That(projected.Length, Is.EqualTo 1)
            Assert.That(projected.Head.Worktrees, Is.Empty)
            Assert.That(projected.Head.ArchivedWorktrees |> List.map _.Branch, Is.EqualTo([ "recent-archive" ]))
            Assert.That(projected.Head.IsCollapsed, Is.True)
            Assert.That(source.Head.Worktrees |> List.map _.Branch, Is.EqualTo([ "old" ])))

    [<Test>]
    member _.``Equivalent offset instants qualify across a daylight-saving transition``() =
        let afterTransition = DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4.0))
        let atCutoff = DateTimeOffset(2026, 3, 8, 0, 30, 0, TimeSpan.FromHours(-5.0))

        Assert.Multiple(fun () ->
            Assert.That(includes Window.Hours2 afterTransition (worktree "local" (Some atCutoff)), Is.True)
            Assert.That(includes Window.Hours2 afterTransition (worktree "utc" (Some(atCutoff.ToUniversalTime()))), Is.True)
            Assert.That(includes Window.Hours2 afterTransition (worktree "old" (Some(atCutoff.AddMilliseconds(-1.0)))), Is.False))

    [<Test>]
    member _.``Clock expiry and a subsequent real event change eligibility without modifying source data``() =
        let candidate = worktree "candidate" (Some now)
        let source = [ repo [ candidate ] [] ]
        let resumed = { candidate with SessionActivityAt = Some(now.AddHours 3.0) }

        Assert.Multiple(fun () ->
            Assert.That((project Window.Hours2 (now.AddHours 2.0) source).Head.Worktrees, Is.EqualTo([ candidate ]))
            Assert.That((project Window.Hours2 (now.AddHours(2.0).AddMilliseconds 1.0) source).Head.Worktrees, Is.Empty)
            Assert.That(
                (project Window.Hours2 (now.AddHours 3.0)
                    [ repo [ resumed ] [] ]).Head.Worktrees,
                Is.EqualTo([ resumed ])))

    [<Test>]
    member _.``Live Overview membership matches eligible cards and keeps every sibling marker``() =
        let marker identity status : SessionDot =
            { InstanceId = SessionInstanceId identity
              Status = status
              Skill = None
              ContextUsage = None }
        let recent =
            { worktree "recent" (Some now) with
                CodingTool = Working
                Sessions = [ marker "working" Working; marker "idle" Idle ]
                Planning = { BeadsPlanning.zero with Planned = 2 } }
        let old =
            { worktree "old" (Some(now.AddDays(-2.0))) with
                CodingTool = Idle
                Sessions = [ marker "old" Idle ]
                Planning = { BeadsPlanning.zero with Planned = 7 } }
        let archived = { recent with Path = WorktreePath "archived"; IsArchived = true }
        let overview =
            project Window.Hours2 now [ repo [ recent; old ] [ archived ] ]
            |> List.map toRepoWorktrees
            |> aggregate

        Assert.Multiple(fun () ->
            Assert.That(overview.Tasks |> List.map (fun bucket -> bucket.Kind, bucket.Count), Is.EqualTo([ (TaskBucketKind.Planned, 2) ]))
            Assert.That(overview.Agents |> List.map (fun group -> group.Kind, group.Count),
                Is.EqualTo([ (AgentGroupKind.Activity CurrentActivity.Working, 1); (AgentGroupKind.Idle, 1) ]))
            Assert.That(overview.Tasks.Head.Members |> List.map _.ScopedKey, Is.EqualTo([ "recent" ]))
            Assert.That(overview.Agents |> List.collect _.Members |> List.map _.ScopedKey, Is.EqualTo([ "recent"; "recent" ])))
