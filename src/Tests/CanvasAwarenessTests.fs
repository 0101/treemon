module Tests.CanvasAwarenessTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open AppTypes
open CanvasUpdate
open Navigation
open CanvasTypes
open CanvasState
open CanvasAwareness

let private makeWorktree repoId branch (canvasDocs: CanvasDoc list) : WorktreeStatus =
    { Path = WorktreePath $"{repoId}/{branch}"
      Branch = branch
      LastCommitMessage = "msg"
      LastCommitTime = DateTimeOffset.UtcNow
      Beads = BeadsSummary.zero
      Planning = BeadsPlanning.zero
      CodingTool = CodingToolStatus.Idle
      CodingToolProvider = None
      CodingToolSince = None
      CurrentSkill = None
      AgentActivity = None
      Sessions = []
      LastUserMessage = None
      LastAssistantMessage = None
      Pr = PrStatus.NoPr
      MainBehindCount = 0
      IsDirty = false
      WorkMetrics = None
      HasActiveSession = false
      HasTestFailureLog = false
      IsMainWorktree = false
      IsArchived = false
      CanvasDocs = canvasDocs }

let private makeRepo repoId worktrees : RepoModel =
    { RepoId = RepoId repoId
      Name = repoId
      Worktrees = worktrees
      ArchivedWorktrees = []
      IsReady = true
      IsCollapsed = false
      Provider = None
      BaseBranch = "main" }

let private makeDoc filename hash =
    { Filename = filename
      ContentHash = hash
      LastModified = DateTimeOffset.UtcNow
      OwnerSessionId = None
      Kind = AgentDoc }

let private makeSystemDoc filename hash =
    { Filename = filename
      ContentHash = hash
      LastModified = DateTimeOffset.UtcNow
      OwnerSessionId = None
      Kind = SystemView }

let private defaultModel : Model =
    { Repos = []
      IsLoading = false
      HasError = false
      SortMode = ByActivity
      IsCompact = false
      SchedulerEvents = []
      LatestByCategory = Map.empty
      BranchEvents = Map.empty
      SyncPending = Set.empty
      AppVersion = Some "1.0"
      DeployBranch = None
      SystemMetrics = None
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      EditorName = "VS Code"
      WorktreeSkills = []
      ActionCooldowns = Set.empty
      Activity = ActivityState.empty
      Mascot = MascotState.empty
      Canvas = CanvasState.empty
      OverviewPanelOpen = false
      SelectedOverviewGroup = None
      OverviewChartWindow = OverviewChartWindow.Hidden
      OverviewHistory = []
      OverviewHistoryNow = System.DateTimeOffset.Now }

/// Calls update and returns the model, ignoring the Cmd. Tolerates the
/// Fable.Remoting.Client proxy build failing under .NET, which surfaces as a
/// TypeInitializationException (eager static init) or an ArgumentException (the
/// lazy proxy in App.fs forced during Cmd construction). The model is computed
/// before the Cmd, so we re-derive it.
let private tryUpdateModel msg model =
    try
        let m, _ = update msg model
        m
    with
    | :? TypeInitializationException | :? ArgumentException ->
        match msg with
        | MarkDocViewed (scopedKey, filename) ->
            match findWorktree scopedKey model with
            | Some wt ->
                let currentHash =
                    wt.CanvasDocs
                    |> List.tryFind (fun d -> d.Filename = filename)
                    |> Option.map _.ContentHash
                match currentHash with
                | Some hash ->
                    let innerMap =
                        model.Canvas.LastViewedHashes
                        |> Map.tryFind scopedKey
                        |> Option.defaultValue Map.empty
                        |> Map.add filename hash
                    { model with Canvas.LastViewedHashes = model.Canvas.LastViewedHashes |> Map.add scopedKey innerMap }
                | None -> model
            | None -> model
        | _ -> reraise ()


// ── unviewedDocsByScopedKey ──────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UnviewedDocsByScopedKeyTests() =

    [<Test>]
    member _.``Returns unviewed docs when hashes differ from last viewed``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "hash-v2" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.True, "Should contain the scoped key")
        Assert.That(result["myrepo/feat"], Is.EqualTo([ "status.html" ]))

    [<Test>]
    member _.``Returns unviewed docs when no entry exists in LastViewedHashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "report.html" "abc123" ] ] ]
                Canvas.LastViewedHashes = Map.empty }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.True)
        Assert.That(result["myrepo/feat"], Is.EqualTo([ "report.html" ]))

    [<Test>]
    member _.``Returns empty map when all docs have been viewed with matching hashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "hash-v1" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.False,
            "Should not contain scoped key when all docs are viewed")

    [<Test>]
    member _.``Returns empty map when worktree has no canvas docs``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [] ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Returns multiple unviewed docs for same worktree``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [
                    makeDoc "a.html" "h1"
                    makeDoc "b.html" "h2"
                    makeDoc "c.html" "h3" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "b.html", "h2" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result["myrepo/feat"], Does.Contain("a.html"))
        Assert.That(result["myrepo/feat"], Does.Contain("c.html"))
        Assert.That(result["myrepo/feat"], Does.Not.Contain("b.html"),
            "b.html has matching hash, should not be unviewed")

    [<Test>]
    member _.``Handles multiple repos and worktrees``() =
        let model =
            { defaultModel with
                Repos = [
                    makeRepo "repo1" [ makeWorktree "repo1" "main" [ makeDoc "x.html" "h1" ] ]
                    makeRepo "repo2" [ makeWorktree "repo2" "dev" [ makeDoc "y.html" "h2" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "repo1/main", Map.ofList [ "x.html", "h1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.Canvas.LastViewedHashes

        Assert.That(result |> Map.containsKey "repo1/main", Is.False, "repo1/main is fully viewed")
        Assert.That(result |> Map.containsKey "repo2/dev", Is.True, "repo2/dev has no viewed hashes")


// ── detectCanvasEvents ───────────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DetectCanvasEventsTests() =

    [<Test>]
    member _.``Detects new doc as NewDoc``() =
        let prev = Map.empty
        let curr = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h1" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow prev curr

        Assert.That(result |> Map.containsKey "r/feat", Is.True)
        let events = result["r/feat"]
        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Filename, Is.EqualTo("report.html"))
        Assert.That(events[0].Kind, Is.EqualTo(NewDoc))

    [<Test>]
    member _.``Detects updated contentHash as UpdatedDoc``() =
        let prev = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h1" ] ]
        let curr = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h2" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow prev curr

        let events = result["r/feat"]
        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Filename, Is.EqualTo("report.html"))
        Assert.That(events[0].Kind, Is.EqualTo(UpdatedDoc), "Updated doc should have Kind=UpdatedDoc")

    [<Test>]
    member _.``Returns empty map when no changes``() =
        let hashes = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h1" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow hashes hashes

        Assert.That(result, Is.Empty, "No changes should produce no events")

    [<Test>]
    member _.``Detects both new and updated docs in same worktree``() =
        let prev = Map.ofList [ "r/feat", Map.ofList [ "existing.html", "h1" ] ]
        let curr = Map.ofList [ "r/feat", Map.ofList [ "existing.html", "h2"; "new.html", "h3" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow prev curr

        let events = result["r/feat"]
        Assert.That(events.Length, Is.EqualTo(2))
        let newDoc = events |> List.find (fun e -> e.Filename = "new.html")
        let updatedDoc = events |> List.find (fun e -> e.Filename = "existing.html")
        Assert.That(newDoc.Kind, Is.EqualTo(NewDoc))
        Assert.That(updatedDoc.Kind, Is.EqualTo(UpdatedDoc))

    [<Test>]
    member _.``Ignores removed docs``() =
        let prev = Map.ofList [ "r/feat", Map.ofList [ "old.html", "h1"; "kept.html", "h2" ] ]
        let curr = Map.ofList [ "r/feat", Map.ofList [ "kept.html", "h2" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow prev curr

        Assert.That(result, Is.Empty, "Removal of a doc without changes to remaining should produce no events")

    [<Test>]
    member _.``Handles multiple scoped keys independently``() =
        let prev = Map.ofList [ "r1/main", Map.ofList [ "a.html", "h1" ] ]
        let curr = Map.ofList [
            "r1/main", Map.ofList [ "a.html", "h1" ]
            "r2/dev", Map.ofList [ "b.html", "h2" ] ]

        let result = detectCanvasEvents DateTimeOffset.UtcNow prev curr

        Assert.That(result |> Map.containsKey "r1/main", Is.False, "r1/main unchanged")
        Assert.That(result |> Map.containsKey "r2/dev", Is.True, "r2/dev is new")


// ── Freshness gate (phantom "published" suppression on restart) ──────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasEventFreshnessGateTests() =

    let event filename kind : CanvasEvent =
        { Filename = filename; Timestamp = DateTimeOffset.UtcNow; Kind = kind }

    [<Test>]
    member _.``gate drops a phantom event whose doc mtime is stale``() =
        let now = DateTimeOffset.UtcNow
        let events = Map.ofList [ "r/feat", [ event "old.html" NewDoc ] ]
        let modified = Map.ofList [ "r/feat", Map.ofList [ "old.html", now.AddHours(-3.0) ] ]

        let result = gateCanvasEventsByFreshness now modified events

        Assert.That(result, Is.Empty, "A pre-existing doc (stale mtime) must not surface as a canvas event")

    [<Test>]
    member _.``gate keeps a fresh event and restamps it with the real mtime``() =
        let now = DateTimeOffset.UtcNow
        let mtime = now.AddMinutes(-1.0)
        let events = Map.ofList [ "r/feat", [ event "new.html" NewDoc ] ]
        let modified = Map.ofList [ "r/feat", Map.ofList [ "new.html", mtime ] ]

        let result = gateCanvasEventsByFreshness now modified events

        let evts = result["r/feat"]
        Assert.That(evts.Length, Is.EqualTo(1))
        Assert.That(evts[0].Filename, Is.EqualTo("new.html"))
        Assert.That(evts[0].Timestamp, Is.EqualTo(mtime), "Event is restamped with the file's real mtime")

    [<Test>]
    member _.``gate drops events for a doc missing from the modified map``() =
        let now = DateTimeOffset.UtcNow
        let events = Map.ofList [ "r/feat", [ event "ghost.html" UpdatedDoc ] ]

        let result = gateCanvasEventsByFreshness now Map.empty events

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``detect + gate suppresses a pre-existing doc reappearing after a restart``() =
        // Baseline was momentarily empty (restart scan gap); the doc reappears as absent->present,
        // which detectCanvasEvents alone reports as NewDoc — but its mtime is old, so the gate drops it.
        let now = DateTimeOffset.UtcNow
        let prev = Map.empty
        let curr = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h1" ] ]
        let modified = Map.ofList [ "r/feat", Map.ofList [ "report.html", now.AddDays(-2.0) ] ]

        let result = detectCanvasEvents now prev curr |> gateCanvasEventsByFreshness now modified

        Assert.That(result, Is.Empty, "A pre-existing doc reappearing after restart must not show as published")

    [<Test>]
    member _.``detect + gate still reports a genuinely fresh new doc``() =
        let now = DateTimeOffset.UtcNow
        let prev = Map.empty
        let curr = Map.ofList [ "r/feat", Map.ofList [ "report.html", "h1" ] ]
        let modified = Map.ofList [ "r/feat", Map.ofList [ "report.html", now.AddSeconds(-2.0) ] ]

        let result = detectCanvasEvents now prev curr |> gateCanvasEventsByFreshness now modified

        let evts = result["r/feat"]
        Assert.That(evts[0].Kind, Is.EqualTo(NewDoc), "A truly recent publish still surfaces")

    [<Test>]
    member _.``isCanvasDocFresh is true within the window and false outside it or when missing``() =
        let now = DateTimeOffset.UtcNow
        let modified = Map.ofList [ "r/feat", Map.ofList [
            "fresh.html", now.AddMinutes(-1.0)
            "stale.html", now.AddHours(-2.0) ] ]

        Assert.That(isCanvasDocFresh now modified "r/feat" "fresh.html", Is.True)
        Assert.That(isCanvasDocFresh now modified "r/feat" "stale.html", Is.False)
        Assert.That(isCanvasDocFresh now modified "r/feat" "missing.html", Is.False)
        Assert.That(isCanvasDocFresh now modified "r/ghost" "fresh.html", Is.False)


// ── Auto-display idle logic ──────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoDisplayIdleLogicTests() =

    let docWithTime filename hash (time: DateTimeOffset) =
        { Filename = filename; ContentHash = hash; LastModified = time; OwnerSessionId = None; Kind = AgentDoc }

    [<Test>]
    member _.``detectChangedCanvasDocs finds new filenames not present in previous``() =
        let prev = Map.ofList [ "r/feat", Map.ofList [ "old.html", "h1" ] ]
        let curr = Map.ofList [ "r/feat", Map.ofList [ "old.html", "h1"; "new.html", "h2" ] ]

        let result = detectChangedCanvasDocs DateTimeOffset.UtcNow prev curr

        Assert.That(result, Does.Contain(("r/feat", "new.html")))
        Assert.That(result.Length, Is.EqualTo(1), "Only the new doc should be detected")

    [<Test>]
    member _.``detectChangedCanvasDocs returns empty when no hashes changed``() =
        let hashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ]

        let result = detectChangedCanvasDocs DateTimeOffset.UtcNow hashes hashes

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``detectChangedCanvasDocs detects hash changes for existing filenames``() =
        let prev = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ]
        let curr = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h2" ] ]

        let result = detectChangedCanvasDocs DateTimeOffset.UtcNow prev curr

        Assert.That(result, Does.Contain(("r/feat", "a.html")), "Updated hash should be detected as changed")

    [<Test>]
    member _.``findMostRecentChangedDoc picks the most recently modified doc``() =
        let now = DateTimeOffset.UtcNow
        let repos = [
            makeRepo "r" [
                makeWorktree "r" "feat" [
                    docWithTime "old.html" "h1" (now.AddMinutes(-10.0))
                    docWithTime "recent.html" "h2" now
                    docWithTime "middle.html" "h3" (now.AddMinutes(-5.0)) ] ] ]

        let changedDocs = [ ("r/feat", "old.html"); ("r/feat", "recent.html"); ("r/feat", "middle.html") ]
        let result = findMostRecentChangedDoc repos changedDocs

        Assert.That(result, Is.EqualTo(Some ("r/feat", "recent.html")))

    [<Test>]
    member _.``Auto-display triggers when idle and doc hash changes``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docWithTime "new.html" "h1" now ] ] ]
        let model =
            { defaultModel with
                Repos = repos
                Activity.LastActivityTime = 0.0
                Canvas.PreviousCanvasHashes = Map.empty; Canvas.CanvasPaneOpen = false }

        let currentHashes = canvasHashesByScopedKey repos
        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.Canvas.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0 // 120s since epoch — well past 60s idle threshold
        let isIdle = jsNow - model.Activity.LastActivityTime > ActivityState.autoDisplayIdleMs
        let target =
            if isIdle && not (List.isEmpty changedDocs)
            then findMostRecentChangedDoc repos changedDocs
            else None

        Assert.That(target, Is.Not.Null.And.Not.EqualTo(None), "Should auto-display when idle + hash changed")
        Assert.That(target, Is.EqualTo(Some ("r/feat", "new.html")))

    [<Test>]
    member _.``Auto-display triggers when idle and existing doc content changes``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docWithTime "a.html" "h2" now ] ] ]
        let previousHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ]
        let model =
            { defaultModel with
                Repos = repos
                Activity.LastActivityTime = 0.0
                Canvas.PreviousCanvasHashes = previousHashes; Canvas.CanvasPaneOpen = false }

        let currentHashes = canvasHashesByScopedKey repos
        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.Canvas.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0
        let isIdle = jsNow - model.Activity.LastActivityTime > ActivityState.autoDisplayIdleMs
        let target =
            if isIdle && not (List.isEmpty changedDocs)
            then findMostRecentChangedDoc repos changedDocs
            else None

        Assert.That(target, Is.Not.Null.And.Not.EqualTo(None), "Should auto-display when idle + existing doc content changed")
        Assert.That(target, Is.EqualTo(Some ("r/feat", "a.html")))

    [<Test>]
    member _.``Auto-display does NOT trigger when user is active``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docWithTime "new.html" "h1" now ] ] ]
        let currentHashes = canvasHashesByScopedKey repos
        let jsNow = 120_000.0
        let model =
            { defaultModel with
                Repos = repos
                Activity.LastActivityTime = jsNow - 10_000.0 // 10s ago, well within 60s threshold
                Canvas.PreviousCanvasHashes = Map.empty }

        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.Canvas.PreviousCanvasHashes currentHashes
        let isIdle = jsNow - model.Activity.LastActivityTime > ActivityState.autoDisplayIdleMs
        let target =
            if isIdle && not (List.isEmpty changedDocs)
            then findMostRecentChangedDoc repos changedDocs
            else None

        Assert.That(target, Is.EqualTo(None), "Should NOT auto-display when user is active (within 60s)")

    [<Test>]
    member _.``Auto-display does NOT trigger when hashes unchanged``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docWithTime "a.html" "h1" now ] ] ]
        let currentHashes = canvasHashesByScopedKey repos
        let model =
            { defaultModel with
                Repos = repos
                Activity.LastActivityTime = 0.0
                Canvas.PreviousCanvasHashes = currentHashes }

        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.Canvas.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0
        let isIdle = jsNow - model.Activity.LastActivityTime > ActivityState.autoDisplayIdleMs
        let target =
            if isIdle && not (List.isEmpty changedDocs)
            then findMostRecentChangedDoc repos changedDocs
            else None

        Assert.That(target, Is.EqualTo(None), "Should NOT auto-display when no hashes changed")


// ── Queued-send delivery signal (clearWaitingOnDelivery) ─────────────
// A queued canvas message shows a "Waiting for session…" banner scoped to its target worktree.
// The banner must clear only when that *target* worktree's agent doc changes (a registering
// session's response) — never on an unrelated worktree's churn, which would falsely report
// delivery while the message is in fact still queued.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasSendStateTests() =

    [<Test>]
    member _.``Waiting clears to Idle when the target worktree's doc changes``() =
        let waiting = CanvasSendState.Waiting "r/feat"
        let agentChangedDocs = [ ("r/feat", "status.html") ]

        let result = clearWaitingOnDelivery waiting agentChangedDocs

        Assert.That(result, Is.EqualTo(CanvasSendState.Idle), "Target worktree's doc change is the delivery signal")

    [<Test>]
    member _.``Waiting is NOT cleared by an unrelated worktree's doc change``() =
        // Regression for Finding C-02: with several sessions live across worktrees, an unrelated
        // worktree's doc change used to dismiss the banner within one poll (~5s), falsely signalling
        // delivery even though the queued message was still waiting for *this* target's session.
        let waiting = CanvasSendState.Waiting "r/feat"
        let agentChangedDocs = [ ("r/other", "status.html") ]

        let result = clearWaitingOnDelivery waiting agentChangedDocs

        Assert.That(result, Is.EqualTo(waiting), "Unrelated worktree activity must NOT dismiss the waiting banner")

    [<Test>]
    member _.``Waiting clears only when the matching scopedKey is among several changed docs``() =
        let waiting = CanvasSendState.Waiting "r/feat"
        let unrelatedOnly = [ ("r/other", "a.html"); ("r/another", "b.html") ]
        let includingTarget = [ ("r/other", "a.html"); ("r/feat", "status.html") ]

        Assert.That(clearWaitingOnDelivery waiting unrelatedOnly, Is.EqualTo(waiting), "No matching scopedKey → stay Waiting")
        Assert.That(clearWaitingOnDelivery waiting includingTarget, Is.EqualTo(CanvasSendState.Idle), "Matching scopedKey present → clear")

    [<Test>]
    member _.``Waiting is NOT cleared when no docs changed``() =
        let waiting = CanvasSendState.Waiting "r/feat"

        let result = clearWaitingOnDelivery waiting []

        Assert.That(result, Is.EqualTo(waiting), "Empty change set must leave Waiting intact")

    [<Test>]
    member _.``Idle and Failed states pass through unchanged``() =
        let agentChangedDocs = [ ("r/feat", "status.html") ]

        Assert.That(clearWaitingOnDelivery CanvasSendState.Idle agentChangedDocs, Is.EqualTo(CanvasSendState.Idle))
        let failed = CanvasSendState.Failed "boom"
        Assert.That(clearWaitingOnDelivery failed agentChangedDocs, Is.EqualTo(failed), "A real failure must not be silently cleared")


// ── MarkDocViewed ────────────────────────────────────────────────────

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MarkDocViewedTests() =

    [<Test>]
    member _.``MarkDocViewed updates LastViewedHashes with current content hash``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "status.html" "hash-v2" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "status.html")) model

        let viewedHash =
            updated.Canvas.LastViewedHashes
            |> Map.find "r/feat"
            |> Map.find "status.html"
        Assert.That(viewedHash, Is.EqualTo("hash-v2"), "Should update to current content hash")

    [<Test>]
    member _.``MarkDocViewed creates new entry when scoped key not in LastViewedHashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "new.html" "hash1" ] ] ]
                Canvas.LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "new.html")) model

        Assert.That(updated.Canvas.LastViewedHashes |> Map.containsKey "r/feat", Is.True)
        let viewedHash =
            updated.Canvas.LastViewedHashes
            |> Map.find "r/feat"
            |> Map.find "new.html"
        Assert.That(viewedHash, Is.EqualTo("hash1"))

    [<Test>]
    member _.``MarkDocViewed preserves other entries in same scoped key``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [
                    makeDoc "a.html" "ha"
                    makeDoc "b.html" "hb" ] ] ]
                Canvas.LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "old-ha" ] ] }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "b.html")) model

        let inner = updated.Canvas.LastViewedHashes |> Map.find "r/feat"
        Assert.That(inner |> Map.find "a.html", Is.EqualTo("old-ha"), "Existing entry should be preserved")
        Assert.That(inner |> Map.find "b.html", Is.EqualTo("hb"), "New entry should be added")

    [<Test>]
    member _.``MarkDocViewed does nothing for unknown scoped key``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
                Canvas.LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("unknown/key", "a.html")) model

        Assert.That(updated.Canvas.LastViewedHashes, Is.Empty, "Should not modify hashes for unknown scoped key")

    [<Test>]
    member _.``MarkDocViewed does nothing for unknown filename``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
                Canvas.LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "nonexistent.html")) model

        Assert.That(updated.Canvas.LastViewedHashes, Is.Empty, "Should not modify hashes for unknown filename")


// ── SystemView exclusion from contentHash awareness ──────────────────
// A SystemView doc (the beads dashboard) has a stable file hash while its data changes, so it
// must never participate in contentHash-based awareness. Beads "newness" is surfaced on the
// worktree card via BeadsSummary instead. These tests prove a SystemView doc never contributes
// to unviewed counts, canvas events, seeded hashes, or idle auto-display.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SystemViewAwarenessTests() =

    let docWithTime filename hash (time: DateTimeOffset) kind =
        { Filename = filename; ContentHash = hash; LastModified = time; OwnerSessionId = None; Kind = kind }

    // unviewedDocsByScopedKey

    [<Test>]
    member _.``unviewedDocsByScopedKey ignores a never-viewed SystemView doc``() =
        let repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeSystemDoc "beads.html" "h1" ] ] ]

        let result = unviewedDocsByScopedKey repos Map.empty

        Assert.That(result, Is.Empty, "A SystemView doc must never count as unviewed even with no viewed hash")

    [<Test>]
    member _.``unviewedDocsByScopedKey ignores a SystemView doc whose hash changed``() =
        let repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeSystemDoc "beads.html" "h2" ] ] ]
        let lastViewed = Map.ofList [ "myrepo/feat", Map.ofList [ "beads.html", "h1" ] ]

        let result = unviewedDocsByScopedKey repos lastViewed

        Assert.That(result, Is.Empty, "A SystemView doc must never count as unviewed even when its hash changed")

    [<Test>]
    member _.``unviewedDocsByScopedKey returns only the AgentDoc when mixed with a SystemView doc``() =
        let repos =
            [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [
                makeDoc "status.html" "a1"
                makeSystemDoc "beads.html" "b1" ] ] ]

        let result = unviewedDocsByScopedKey repos Map.empty

        Assert.That(result["myrepo/feat"], Is.EqualTo([ "status.html" ]),
            "Only the AgentDoc should be unviewed; the SystemView doc is excluded")

    // canvasHashesByScopedKey + detectCanvasEvents

    [<Test>]
    member _.``canvasHashesByScopedKey excludes SystemView docs``() =
        let repos =
            [ makeRepo "r" [ makeWorktree "r" "feat" [
                makeDoc "status.html" "a1"
                makeSystemDoc "beads.html" "b1" ] ] ]

        let hashes = canvasHashesByScopedKey repos

        Assert.That(hashes["r/feat"] |> Map.containsKey "status.html", Is.True)
        Assert.That(hashes["r/feat"] |> Map.containsKey "beads.html", Is.False,
            "SystemView doc must not appear in the canvas hash map")

    [<Test>]
    member _.``canvasHashesByScopedKey drops a worktree whose only doc is a SystemView``() =
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeSystemDoc "beads.html" "b1" ] ] ]

        let hashes = canvasHashesByScopedKey repos

        Assert.That(hashes |> Map.containsKey "r/feat", Is.False,
            "A worktree with only a SystemView doc contributes no hashes")

    [<Test>]
    member _.``detectCanvasEvents produces no event for a changed SystemView doc``() =
        // currentHashes is built the production way (via canvasHashesByScopedKey), so the SystemView
        // doc never enters the hash maps that detectCanvasEvents compares.
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeSystemDoc "beads.html" "b2" ] ] ]
        let previousHashes = Map.ofList [ "r/feat", Map.ofList [ "beads.html", "b1" ] ]
        let currentHashes = canvasHashesByScopedKey repos

        let result = detectCanvasEvents DateTimeOffset.UtcNow previousHashes currentHashes

        Assert.That(result, Is.Empty, "A SystemView hash change must not produce a canvas event")

    [<Test>]
    member _.``detectCanvasEvents reports only the AgentDoc when mixed with a SystemView doc``() =
        let repos =
            [ makeRepo "r" [ makeWorktree "r" "feat" [
                makeDoc "status.html" "a1"
                makeSystemDoc "beads.html" "b1" ] ] ]
        let currentHashes = canvasHashesByScopedKey repos

        let result = detectCanvasEvents DateTimeOffset.UtcNow Map.empty currentHashes

        let events = result["r/feat"]
        Assert.That(events.Length, Is.EqualTo(1))
        Assert.That(events[0].Filename, Is.EqualTo("status.html"),
            "Only the AgentDoc should yield a NewDoc event")

    // seedLastViewedHashes

    [<Test>]
    member _.``seedLastViewedHashes does not seed a SystemView doc``() =
        let repos =
            [ makeRepo "r" [ makeWorktree "r" "feat" [
                makeDoc "status.html" "a1"
                makeSystemDoc "beads.html" "b1" ] ] ]

        let seeded = seedLastViewedHashes repos Map.empty

        Assert.That(seeded["r/feat"] |> Map.containsKey "status.html", Is.True)
        Assert.That(seeded["r/feat"] |> Map.containsKey "beads.html", Is.False,
            "SystemView doc must not be seeded into LastViewedHashes")

    [<Test>]
    member _.``seedLastViewedHashes seeds nothing for a worktree with only a SystemView doc``() =
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeSystemDoc "beads.html" "b1" ] ] ]

        let seeded = seedLastViewedHashes repos Map.empty

        Assert.That(seeded |> Map.containsKey "r/feat", Is.False,
            "A worktree whose only doc is a SystemView seeds no hashes")

    // findMostRecentChangedDoc

    [<Test>]
    member _.``findMostRecentChangedDoc ignores a SystemView doc``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docWithTime "beads.html" "b1" now SystemView ] ] ]

        let result = findMostRecentChangedDoc repos [ ("r/feat", "beads.html") ]

        Assert.That(result, Is.EqualTo(None),
            "A SystemView doc must never be selected as the auto-display target")

    [<Test>]
    member _.``findMostRecentChangedDoc picks the AgentDoc over a more recent SystemView doc``() =
        let now = DateTimeOffset.UtcNow
        let repos =
            [ makeRepo "r" [ makeWorktree "r" "feat" [
                docWithTime "status.html" "a1" (now.AddMinutes(-5.0)) AgentDoc
                docWithTime "beads.html" "b1" now SystemView ] ] ]

        let result = findMostRecentChangedDoc repos [ ("r/feat", "status.html"); ("r/feat", "beads.html") ]

        Assert.That(result, Is.EqualTo(Some ("r/feat", "status.html")),
            "Even though the SystemView doc is more recent, only the AgentDoc is eligible")


// ── NavigateCanvasDoc defense-in-depth (Finding 11) ──────────────────
// In-doc link clicks arrive as an untrusted postMessage carrying only a filename. It is committed
// to ActiveCanvasDoc only when isKnownCanvasDoc confirms it names a real CanvasDoc of the focused
// worktree; an unknown filename (e.g. one still carrying a ?query/#hash the interceptor failed to
// strip) is dropped rather than silently mis-tabbed onto the first doc.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type NavigateCanvasDocTests() =

    // Run the Elmish effects and collect the messages they dispatch. Used only on the accept path,
    // whose Cmd is Cmd.ofMsg — it forces neither the (lazy) Remoting proxy nor Fable.Core.JS.
    let dispatchedMsgs cmd : Msg list =
        let captured = ResizeArray<Msg>()
        cmd |> List.iter (fun effect -> effect (fun m -> captured.Add m))
        List.ofSeq captured

    [<Test>]
    member _.``a known doc filename switches to that tab``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "h1"; makeDoc "plan.html" "h2" ] ] ]
                FocusedElement = Some (Card "myrepo/feat") }

        let _, cmd = update (NavigateCanvasDoc "plan.html") model

        match dispatchedMsgs cmd with
        | [ SelectCanvasDoc (scopedKey, filename) ] ->
            Assert.That(scopedKey, Is.EqualTo("myrepo/feat"))
            Assert.That(filename, Is.EqualTo("plan.html"))
        | other -> Assert.Fail($"expected a single SelectCanvasDoc, got {other}")

    // The accept/drop decision is the pure isKnownCanvasDoc predicate (the drop branch itself calls
    // Fable.Core.JS.console.warn, dummy code that throws under .NET, so it can't run through update
    // here). These assert the gate that decides whether a navigation is committed or dropped.

    [<Test>]
    member _.``isKnownCanvasDoc accepts a real doc of the focused worktree``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "h1"; makeDoc "plan.html" "h2" ] ] ] }
        Assert.That(isKnownCanvasDoc model "myrepo/feat" "plan.html", Is.True,
            "A filename that matches a CanvasDoc.Filename must be accepted")

    [<Test>]
    member _.``isKnownCanvasDoc rejects a filename still carrying a ?query suffix``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "h1" ] ] ] }
        Assert.That(isKnownCanvasDoc model "myrepo/feat" "status.html?tab=errors", Is.False,
            "A suffixed name matches no bare CanvasDoc.Filename and must be dropped (Finding 11)")

    [<Test>]
    member _.``isKnownCanvasDoc rejects a filename naming no doc of the worktree``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "h1" ] ] ] }
        Assert.That(isKnownCanvasDoc model "myrepo/feat" "other.html", Is.False,
            "A filename that names no CanvasDoc of the worktree must be dropped")

    [<Test>]
    member _.``isKnownCanvasDoc rejects when the scoped key resolves to no worktree``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "h1" ] ] ] }
        Assert.That(isKnownCanvasDoc model "myrepo/ghost" "status.html", Is.False,
            "No worktree for the scoped key means nothing is known, so the navigation is dropped")


// ── Doc-side JS error (item 3: error overlay → canvas-doc-error banner) ───────
// A doc-side JS error is stored in Canvas.DocError stamped with the worktree + doc that EMITTED it —
// both ride along in the message (wt/doc fields) and are validated against that worktree's docs
// (DocJsError { ScopedKey; Filename; Message }) so the pane can show the banner only while that doc
// stays focused — navigating away auto-hides a stale error (the view gates on the stamp). Stamping
// with the emitter (not the active tab) means an async error from a hidden/background iframe — even in
// a non-focused worktree — is attributed correctly (focused-review A-02, C-06). It is a distinct
// source from CanvasSendState.Failed, and SelectCanvasDoc clears it so a tab switch never re-shows it.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DocErrorTests() =

    // A model whose focused card names a worktree carrying the given docs, so canvasDocError can
    // validate the emitting filename against them and stamp the error.
    let focusedModel (canvasDocs: CanvasDoc list) =
        { defaultModel with
            Repos = [ makeRepo "r" [ makeWorktree "r" "feat" canvasDocs ] ]
            FocusedElement = Some (Card "r/feat") }

    [<Test>]
    member _.``CanvasDocError stamps the error with the emitting doc``() =
        let model = focusedModel [ makeDoc "a.html" "ha" ]
        let updated = tryUpdateModel (CanvasDocError ("r/feat", "a.html", "Uncaught Error: boom (line 3:5)")) model
        Assert.That(updated.Canvas.DocError,
            Is.EqualTo(Some { ScopedKey = "r/feat"; Filename = "a.html"; Message = "Uncaught Error: boom (line 3:5)" }),
            "A doc-side JS error must be stored, stamped with the doc that emitted it (so the banner is doc-scoped)")

    [<Test>]
    member _.``an error is attributed to the emitter's worktree, not the focused one``() =
        // Visited docs stay mounted as hidden iframes and keep running JS, so an async error from a
        // HIDDEN doc in a DIFFERENT worktree (r/other → b.html) must be stamped with the emitter carried
        // in the message, even while a doc in r/feat is focused. (Pre-fix the reducer stamped the
        // FOCUSED worktree, misattributing a hidden worktree's error to the foreground — C-06.)
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "ha" ]
                                         makeWorktree "r" "other" [ makeDoc "b.html" "hb" ] ] ]
                FocusedElement = Some (Card "r/feat") }
        let updated = tryUpdateModel (CanvasDocError ("r/other", "b.html", "boom from hidden")) model
        Assert.That(updated.Canvas.DocError,
            Is.EqualTo(Some { ScopedKey = "r/other"; Filename = "b.html"; Message = "boom from hidden" }),
            "The error must be attributed to the emitter's worktree (r/other), not the focused worktree (r/feat)")

    [<Test>]
    member _.``CanvasDocError is dropped when the emitter's worktree is unknown``() =
        let model = focusedModel [ makeDoc "a.html" "ha" ]   // only r/feat exists
        let updated = tryUpdateModel (CanvasDocError ("r/ghost", "a.html", "boom")) model
        Assert.That(updated.Canvas.DocError, Is.EqualTo(None),
            "An error whose worktree is not known has nothing to attribute it to, so it is dropped")

    [<Test>]
    member _.``CanvasDocError is dropped when the emitter is not a known doc of its worktree``() =
        // Validation guard (mirrors NavigateCanvasDoc): only a filename naming a real doc of the
        // emitting worktree may raise a banner, so a stale/forged filename — e.g. from an archived or
        // deleted doc still posting from a lingering iframe — is dropped, never attributed.
        let model = focusedModel [ makeDoc "a.html" "ha" ]
        let updated = tryUpdateModel (CanvasDocError ("r/feat", "ghost.html", "boom")) model
        Assert.That(updated.Canvas.DocError, Is.EqualTo(None),
            "An error whose emitting filename is not a known doc of its worktree is dropped")

    [<Test>]
    member _.``CanvasDocError does NOT touch CanvasSendState (distinct source)``() =
        let baseModel = focusedModel [ makeDoc "a.html" "ha" ]
        let model = { baseModel with Canvas.CanvasSendState = CanvasSendState.Waiting "r/feat" }
        let updated = tryUpdateModel (CanvasDocError ("r/feat", "a.html", "boom")) model
        Assert.That(updated.Canvas.CanvasSendState, Is.EqualTo(CanvasSendState.Waiting "r/feat"),
            "Doc JS errors and message-delivery state are independent and must not overwrite each other")
        Assert.That(updated.Canvas.DocError |> Option.map _.Message, Is.EqualTo(Some "boom"))

    [<Test>]
    member _.``the newest doc error wins``() =
        let model = focusedModel [ makeDoc "a.html" "ha" ]
        let afterFirst = tryUpdateModel (CanvasDocError ("r/feat", "a.html", "first")) model
        let afterSecond = tryUpdateModel (CanvasDocError ("r/feat", "a.html", "second")) afterFirst
        Assert.That(afterSecond.Canvas.DocError |> Option.map _.Message, Is.EqualTo(Some "second"),
            "A fresh error replaces the prior one")

    [<Test>]
    member _.``DismissCanvasDocError clears the doc error``() =
        let baseModel = focusedModel [ makeDoc "a.html" "ha" ]
        let model = { baseModel with Canvas.DocError = Some { ScopedKey = "r/feat"; Filename = "a.html"; Message = "boom" } }
        let updated = tryUpdateModel DismissCanvasDocError model
        Assert.That(updated.Canvas.DocError, Is.EqualTo(None), "Dismiss must clear the banner")

    [<Test>]
    member _.``DismissCanvasDocError leaves CanvasSendState untouched``() =
        let model = { defaultModel with Canvas.DocError = Some { ScopedKey = "r/feat"; Filename = "a.html"; Message = "boom" }; Canvas.CanvasSendState = CanvasSendState.Failed "send failed" }
        let updated = tryUpdateModel DismissCanvasDocError model
        Assert.That(updated.Canvas.CanvasSendState, Is.EqualTo(CanvasSendState.Failed "send failed"),
            "Dismissing the doc-error banner must not clear an unrelated send failure")

    [<Test>]
    member _.``SelectCanvasDoc clears a stale doc error (so a tab switch back never re-shows it)``() =
        let baseModel = focusedModel [ makeDoc "a.html" "ha"; makeDoc "b.html" "hb" ]
        let model = { baseModel with Canvas.DocError = Some { ScopedKey = "r/feat"; Filename = "a.html"; Message = "stale" } }
        let updated = tryUpdateModel (SelectCanvasDoc ("r/feat", "b.html")) model
        Assert.That(updated.Canvas.DocError, Is.EqualTo(None),
            "Switching tabs clears the stored error so it can never re-show when you switch back")


// ── ShareCanvasDocResult + ClipboardWriteResult banner state (client share feature) ─────────
// A successful share does NOT immediately claim "link copied": the Ok arm clears a stale Failed
// send-state (so the red delivery-error and green success banners never stack) plus any stale notice,
// then defers the banner to the async clipboard write. ClipboardWriteResult routes that write's real
// outcome back in — Ok → "Shared — link copied", Error → a "copy it manually: <url>" correction (F6)
// — and is pure, so both outcomes drive straight through `update`. The share Error arm calls
// Fable.Core.JS.console.error (dummy code that throws under .NET), so its send-state transition is
// extracted into the pure preserveWaitingOnShareFailure helper and locked here instead. A live Waiting
// banner is independent and survives both a share success and a share failure.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ShareCanvasDocResultTests() =

    let shareResult : CanvasShareResult =
        { Url = "https://acct.blob.core.windows.net/canvas/x/status.html?sig=s"; Title = "Status" }

    let modelWithSendState state =
        { defaultModel with Canvas = { defaultModel.Canvas with CanvasSendState = state } }

    [<Test>]
    member _.``Ok defers the banner — it does not claim a copy before the write settles (F6)``() =
        // The share succeeded but the async clipboard write hasn't settled, so the Ok arm must NOT
        // pre-emptively claim "link copied" (that would lie if the write is later rejected). It clears
        // any stale notice and defers the banner to ClipboardWriteResult.
        let updated, _ = update (ShareCanvasDocResult ("r/feat", "status.html", Ok shareResult)) (modelWithSendState CanvasSendState.Idle)
        Assert.That(updated.Canvas.ShareNotice, Is.EqualTo(None),
            "The 'link copied' banner must wait for the clipboard write's actual outcome (F6)")

    [<Test>]
    member _.``Ok clears a stale Failed send-state so the error and success banners never stack``() =
        let updated, _ = update (ShareCanvasDocResult ("r/feat", "status.html", Ok shareResult)) (modelWithSendState (CanvasSendState.Failed "earlier failure"))
        Assert.That(updated.Canvas.CanvasSendState, Is.EqualTo(CanvasSendState.Idle),
            "A prior delivery error must be cleared on a successful share (no red + green banner at once)")
        Assert.That(updated.Canvas.ShareNotice, Is.EqualTo(None),
            "The success banner is deferred to the clipboard write; nothing is claimed yet")

    [<Test>]
    member _.``Ok preserves an independent Waiting send-state``() =
        let updated, _ = update (ShareCanvasDocResult ("r/feat", "status.html", Ok shareResult)) (modelWithSendState (CanvasSendState.Waiting "r/feat"))
        Assert.That(updated.Canvas.CanvasSendState, Is.EqualTo(CanvasSendState.Waiting "r/feat"),
            "A live 'waiting for session' banner is an independent fact and must survive a share")

    // F6: the "link copied" banner reflects the async clipboard write's ACTUAL outcome, routed back
    // through ClipboardWriteResult. A landed write confirms the copy; a rejected write (transient
    // activation / focus lost across the share round-trip, revoked permission, or an unsupported
    // clipboard API) drops the false claim and surfaces the raw URL for a manual copy. This arm is pure
    // (the rejection is logged in writeClipboardCmd's .catch), so — unlike the share Error arm — both
    // outcomes can be driven straight through `update`.
    [<Test>]
    member _.``ClipboardWriteResult Ok raises the 'link copied' banner``() =
        let updated, _ = update (ClipboardWriteResult (shareResult.Url, Ok ())) defaultModel
        Assert.That(updated.Canvas.ShareNotice, Is.EqualTo(Some "Shared — link copied"))

    [<Test>]
    member _.``ClipboardWriteResult Error corrects the claim and surfaces the link for a manual copy (F6)``() =
        let updated, _ = update (ClipboardWriteResult (shareResult.Url, Error "NotAllowedError")) defaultModel
        match updated.Canvas.ShareNotice with
        | Some notice ->
            Assert.That(notice, Does.Not.Contain("link copied"),
                "A rejected clipboard write must NOT claim the link was copied (F6)")
            Assert.That(notice, Does.Contain(shareResult.Url),
                "The raw SAS URL must be surfaced so the user can copy it manually")
        | None -> Assert.Fail("A settled clipboard write must still raise the share banner")

    // The Error arm can't be driven through `update` (its direct Fable.Core.JS.console.error throws
    // under .NET), so its send-state transition lives in the pure preserveWaitingOnShareFailure helper
    // and is locked below. F7 regression: a share failure must NOT clobber a live "Waiting for
    // session" banner — a queued message may still be delivered, so Waiting is never a failure.
    [<Test>]
    member _.``Error preserves an independent Waiting send-state (F7 regression)``() =
        Assert.That(preserveWaitingOnShareFailure (CanvasSendState.Waiting "r/feat") "share boom",
            Is.EqualTo(CanvasSendState.Waiting "r/feat"),
            "A live 'waiting for session' banner is independent and must survive a share failure")

    [<Test>]
    member _.``Error raises the delivery-error banner from Idle``() =
        Assert.That(preserveWaitingOnShareFailure CanvasSendState.Idle "share boom",
            Is.EqualTo(CanvasSendState.Failed "share boom"))

    [<Test>]
    member _.``Error replaces a stale Failed with the latest message``() =
        Assert.That(preserveWaitingOnShareFailure (CanvasSendState.Failed "old failure") "share boom",
            Is.EqualTo(CanvasSendState.Failed "share boom"),
            "A retried share that fails again surfaces the latest error, not a stale one")
// ── mostRecentUnviewedDoc (focus-retarget target picker) ─────────────
// Picks the worktree's most recently modified *unviewed* AgentDoc — the doc that "selecting the
// worktree" should surface. Viewed docs (hash matches LastViewedHashes) and SystemView docs are
// excluded, so an already-seen doc or the beads dashboard never wins.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MostRecentUnviewedDocTests() =

    let docAt filename hash (time: DateTimeOffset) kind =
        { Filename = filename; ContentHash = hash; LastModified = time; OwnerSessionId = None; Kind = kind }

    [<Test>]
    member _.``picks the most recently modified unviewed AgentDoc``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [
            docAt "old.html" "h1" (now.AddMinutes(-10.0)) AgentDoc
            docAt "new.html" "h2" now AgentDoc
            docAt "mid.html" "h3" (now.AddMinutes(-5.0)) AgentDoc ] ] ]
        Assert.That(mostRecentUnviewedDoc repos Map.empty "r/feat", Is.EqualTo(Some "new.html"))

    [<Test>]
    member _.``ignores a doc whose hash matches the last viewed hash``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [
            docAt "old.html" "h1" (now.AddMinutes(-10.0)) AgentDoc
            docAt "new.html" "h2" now AgentDoc ] ] ]
        let viewed = Map.ofList [ "r/feat", Map.ofList [ "new.html", "h2" ] ]
        Assert.That(mostRecentUnviewedDoc repos viewed "r/feat", Is.EqualTo(Some "old.html"),
            "the most-recent doc is already viewed, so the older unviewed doc wins")

    [<Test>]
    member _.``ignores a SystemView doc even when it is the most recent``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [
            docAt "status.html" "a1" (now.AddMinutes(-5.0)) AgentDoc
            docAt "beads.html" "b1" now SystemView ] ] ]
        Assert.That(mostRecentUnviewedDoc repos Map.empty "r/feat", Is.EqualTo(Some "status.html"),
            "a SystemView must never be treated as newly published")

    [<Test>]
    member _.``returns None when every AgentDoc is viewed``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docAt "a.html" "h1" now AgentDoc ] ] ]
        let viewed = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ]
        Assert.That(mostRecentUnviewedDoc repos viewed "r/feat", Is.EqualTo(None))

    [<Test>]
    member _.``returns None for a scoped key that names no worktree``() =
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
        Assert.That(mostRecentUnviewedDoc repos Map.empty "r/ghost", Is.EqualTo(None))


// ── Focus-transition retarget (select-the-worktree surfaces THAT doc) ─
// When focus transitions ONTO a worktree card that has an unviewed (newly published/updated) doc,
// its ActiveCanvasDoc is retargeted to that doc — via update(SetFocus …), the message every
// "select the worktree" entry point funnels through. No retarget when the card is already focused
// (manual tab choice preserved) or nothing is unviewed. The doc is marked viewed only when the pane
// is open (it is actually shown).

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FocusRetargetTests() =

    // Run the Cmd's effects and collect the messages they dispatch. The retarget Cmds are only
    // Cmd.ofMsg/Cmd.none, so this forces neither the (lazy) Remoting proxy nor Fable.Core.JS.
    let dispatchedMsgs cmd : Msg list =
        let captured = ResizeArray<Msg>()
        cmd |> List.iter (fun effect -> effect (fun m -> captured.Add m))
        List.ofSeq captured

    let docAt filename hash (time: DateTimeOffset) =
        { Filename = filename; ContentHash = hash; LastModified = time; OwnerSessionId = None; Kind = AgentDoc }

    // Worktree "r/feat" with a.html (older, viewed) and b.html (newer, unviewed). ActiveCanvasDoc
    // sticks on a.html (a prior manual/last-open selection).
    let modelWithUnviewed paneOpen focused =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [
            docAt "a.html" "h1" (now.AddMinutes(-10.0))
            docAt "b.html" "h2" now ] ] ]
        { defaultModel with
            Repos = repos
            FocusedElement = focused
            Canvas.CanvasPaneOpen = paneOpen
            Canvas.ActiveCanvasDoc = Map.ofList [ "r/feat", "a.html" ]
            Canvas.LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ] }

    [<Test>]
    member _.``focusing a card with an unviewed doc retargets ActiveCanvasDoc to that doc``() =
        let model = modelWithUnviewed false None
        let updated, _ = update (SetFocus (Some (Card "r/feat"))) model
        Assert.That(updated.Canvas.ActiveCanvasDoc |> Map.tryFind "r/feat", Is.EqualTo(Some "b.html"),
            "selecting the worktree surfaces the newly published/updated doc, not the sticky last-open one")

    [<Test>]
    member _.``no retarget when the card is already the focused element``() =
        let model = modelWithUnviewed false (Some (Card "r/feat"))
        let updated, _ = update (SetFocus (Some (Card "r/feat"))) model
        Assert.That(updated.Canvas.ActiveCanvasDoc |> Map.tryFind "r/feat", Is.EqualTo(Some "a.html"),
            "re-focusing the already-focused card must preserve a manual in-worktree tab choice")

    [<Test>]
    member _.``no retarget when there are no unviewed docs``() =
        let now = DateTimeOffset.UtcNow
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ docAt "a.html" "h1" now ] ] ]
        let model =
            { defaultModel with
                Repos = repos
                FocusedElement = None
                Canvas.ActiveCanvasDoc = Map.ofList [ "r/feat", "a.html" ]
                Canvas.LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ] }
        let updated, _ = update (SetFocus (Some (Card "r/feat"))) model
        Assert.That(updated.Canvas.ActiveCanvasDoc |> Map.tryFind "r/feat", Is.EqualTo(Some "a.html"),
            "with nothing unviewed the sticky last-open selection stays")

    [<Test>]
    member _.``retarget marks the doc viewed when the pane is open``() =
        let model = modelWithUnviewed true None
        let updated, cmd = update (SetFocus (Some (Card "r/feat"))) model
        Assert.That(updated.Canvas.ActiveCanvasDoc |> Map.tryFind "r/feat", Is.EqualTo(Some "b.html"))
        Assert.That(dispatchedMsgs cmd |> List.contains (MarkDocViewed ("r/feat", "b.html")), Is.True,
            "an open pane shows the doc, so it is marked viewed")

    [<Test>]
    member _.``retarget does NOT mark viewed when the pane is closed``() =
        let model = modelWithUnviewed false None
        let updated, cmd = update (SetFocus (Some (Card "r/feat"))) model
        Assert.That(dispatchedMsgs cmd, Is.Empty,
            "a closed pane does not display the doc, so nothing is marked viewed")
        Assert.That(unviewedDocsByScopedKey updated.Repos updated.Canvas.LastViewedHashes |> Map.containsKey "r/feat", Is.True,
            "the doc stays unviewed (badge preserved) until the pane actually shows it")

    [<Test>]
    member _.``SetFocusNoRetarget focuses the card without retargeting or marking viewed (idle path)``() =
        // The idle auto-display path opens the pane, focuses the card, then selects its OWN target
        // doc. It must use the no-retarget focus set so it never re-enters the active-user retarget —
        // otherwise (pane already open) the retarget would mark mostRecentUnviewedDoc viewed before
        // the intended doc is shown (finding F1). Pane open + b.html unviewed is exactly that state.
        let model = modelWithUnviewed true None
        let updated, cmd = update (SetFocusNoRetarget (Some (Card "r/feat"))) model
        Assert.That(updated.FocusedElement, Is.EqualTo(Some (Card "r/feat")),
            "the focus is still applied")
        Assert.That(updated.Canvas.ActiveCanvasDoc |> Map.tryFind "r/feat", Is.EqualTo(Some "a.html"),
            "the sticky active doc is untouched — the idle path selects its own target via SelectCanvasDoc")
        Assert.That(dispatchedMsgs cmd, Is.Empty,
            "no MarkDocViewed/morph — SetFocusNoRetarget must not re-enter the retarget")


// A canvas doc that posts a message with no top-level string `action` cannot be routed by
// CanvasPane (there is no action to switch on), so the listener surfaces it here instead of dropping
// it silently. The malformed message carries no self-identifying wt/doc fields, so the reducer
// attributes the banner to the active *visible* doc (a known doc of a known worktree by construction).
// With no active visible doc there is nothing to attribute it to, so it is dropped — and it never
// touches the unrelated message-delivery state.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type MalformedDocMessageTests() =

    // A model whose focused card names a worktree carrying the given docs, so activeVisibleDoc
    // resolves to the first doc and the malformed-message banner can be attributed to it.
    let focusedModel (canvasDocs: CanvasDoc list) =
        { defaultModel with
            Repos = [ makeRepo "r" [ makeWorktree "r" "feat" canvasDocs ] ]
            FocusedElement = Some (Card "r/feat") }

    [<Test>]
    member _.``CanvasMalformedDocMessage raises the doc-error banner on the active visible doc``() =
        let model = focusedModel [ makeDoc "a.html" "ha" ]
        let updated = tryUpdateModel CanvasMalformedDocMessage model
        match updated.Canvas.DocError with
        | Some err ->
            Assert.That(err.ScopedKey, Is.EqualTo("r/feat"),
                "The banner is attributed to the active visible doc's worktree")
            Assert.That(err.Filename, Is.EqualTo("a.html"),
                "The banner is attributed to the active visible doc")
            Assert.That(err.Message, Does.Contain("action"),
                "The user-facing message must explain the missing `action` field")
        | None ->
            Assert.Fail("A malformed message from the active doc must raise the banner, not be dropped silently")

    [<Test>]
    member _.``CanvasMalformedDocMessage uses the active doc, not the first doc of the worktree``() =
        // With b.html selected, activeVisibleDoc resolves to b.html (not the worktree's first doc),
        // so the banner is stamped with the doc the user is actually looking at.
        let baseModel = focusedModel [ makeDoc "a.html" "ha"; makeDoc "b.html" "hb" ]
        let model = { baseModel with Canvas.ActiveCanvasDoc = Map.ofList [ "r/feat", "b.html" ] }
        let updated = tryUpdateModel CanvasMalformedDocMessage model
        Assert.That(updated.Canvas.DocError |> Option.map _.Filename, Is.EqualTo(Some "b.html"),
            "The banner is attributed to the active doc (b.html), not the worktree's first doc (a.html)")

    [<Test>]
    member _.``CanvasMalformedDocMessage is dropped when there is no active visible doc``() =
        // No focused card → activeVisibleDoc = None → nothing to attribute the banner to.
        let updated = tryUpdateModel CanvasMalformedDocMessage defaultModel
        Assert.That(updated.Canvas.DocError, Is.EqualTo(None),
            "With no active visible doc the malformed message has nothing to attribute it to, so no banner is raised")

    [<Test>]
    member _.``CanvasMalformedDocMessage does NOT touch CanvasSendState (distinct source)``() =
        let baseModel = focusedModel [ makeDoc "a.html" "ha" ]
        let model = { baseModel with Canvas.CanvasSendState = CanvasSendState.Waiting "r/feat" }
        let updated = tryUpdateModel CanvasMalformedDocMessage model
        Assert.That(updated.Canvas.CanvasSendState, Is.EqualTo(CanvasSendState.Waiting "r/feat"),
            "Surfacing a malformed message must not overwrite unrelated message-delivery state")
        Assert.That(updated.Canvas.DocError |> Option.isSome, Is.True,
            "The banner is still raised alongside the untouched send state")


// ── LoadLastViewedHashes merge (init-race safety) ────────────────────
// loadLastViewedHashes races the first DataLoaded seeding in init. The handler must MERGE the
// server hashes with a seed of the currently-known docs, not overwrite — otherwise an empty/partial
// server map arriving last wipes the seed, making already-present docs look unviewed (which the
// focus retarget would then act on). These guard that already-present docs stay viewed while genuine
// server-recorded updates still register as unviewed.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LoadLastViewedHashesTests() =

    [<Test>]
    member _.``an empty server map does not wipe the seed for already-present docs``() =
        // Simulates LoadLastViewedHashes arriving AFTER DataLoaded seeded the docs, with a fresh
        // server that has no saved hashes. The docs must stay viewed, not become unviewed.
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
        let seeded = { defaultModel with Repos = repos; Canvas.LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ] }
        let updated = tryUpdateModel (LoadLastViewedHashes Map.empty) seeded
        Assert.That(unviewedDocsByScopedKey updated.Repos updated.Canvas.LastViewedHashes, Is.Empty,
            "an empty server map must not make a seeded, already-present doc look unviewed")

    [<Test>]
    member _.``a server-recorded stale hash still registers the doc as unviewed``() =
        // The doc is now at h2 but the server last saw h1 (updated while away) → stays unviewed.
        let repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h2" ] ] ]
        let model = { defaultModel with Repos = repos }
        let updated = tryUpdateModel (LoadLastViewedHashes (Map.ofList [ "r/feat", Map.ofList [ "a.html", "h1" ] ])) model
        Assert.That(updated.Canvas.LastViewedHashes["r/feat"]["a.html"], Is.EqualTo("h1"),
            "the server value is kept (not overwritten by the current hash), so the update still registers")
        Assert.That(unviewedDocsByScopedKey updated.Repos updated.Canvas.LastViewedHashes |> Map.containsKey "r/feat", Is.True,
            "a doc updated since the server last saw it must remain unviewed")


// ── SelectOverviewWorktree archived-row guard (finding F4) ───────────────────
// A task-bucket breakdown can list ARCHIVED worktrees as clickable rows (only the Done bucket filters
// archived; other buckets keep them). Archived worktrees have no focusable card (visibleFocusTargets
// scans only repo.Worktrees; archived entries render in the separate archive section, never .focused).
// So SelectOverviewWorktree must treat a scopedKey that resolves to no focusable card as a no-op
// rather than setting an invalid FocusedElement that produces no visible focus/scroll and gets reset.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SelectOverviewWorktreeGuardTests() =

    // A repo whose "feat" worktree is live and whose "old" worktree is archived (lives in
    // ArchivedWorktrees, so it has no focusable card even though its scopedKey looks routable).
    let repoWithArchived () =
        let live = makeWorktree "r" "feat" []
        let archived = { makeWorktree "r" "old" [] with IsArchived = true }
        { makeRepo "r" [ live ] with ArchivedWorktrees = [ archived ] }

    [<Test>]
    member _.``an archived worktree scopedKey does not set FocusedElement (F4)``() =
        let model = { defaultModel with Repos = [ repoWithArchived () ]; FocusedElement = None }
        let updated, _ = update (SelectOverviewWorktree "r/old") model
        Assert.That(updated.FocusedElement, Is.EqualTo(None),
            "clicking an archived breakdown row must be a no-op, not an invalid Card focus (F4)")

    [<Test>]
    member _.``an archived scopedKey leaves an existing focus untouched (F4)``() =
        let model = { defaultModel with Repos = [ repoWithArchived () ]; FocusedElement = Some (Card "r/feat") }
        let updated, _ = update (SelectOverviewWorktree "r/old") model
        Assert.That(updated.FocusedElement, Is.EqualTo(Some (Card "r/feat")),
            "a dead archived-row click must not clobber the current focus target (F4)")
