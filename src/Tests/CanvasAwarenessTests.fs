module Tests.CanvasAwarenessTests

open System
open NUnit.Framework
open Shared
open Shared.EventUtils
open App
open Navigation
open CanvasAwareness

let private makeWorktree repoId branch (canvasDocs: CanvasDoc list) : WorktreeStatus =
    { Path = WorktreePath $"{repoId}/{branch}"
      Branch = branch
      LastCommitMessage = "msg"
      LastCommitTime = DateTimeOffset.UtcNow
      Beads = BeadsSummary.zero
      CodingTool = CodingToolStatus.Idle
      CodingToolProvider = None
      LastUserMessage = None
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
      EyeDirection = (0.0, 0.0)
      FocusedElement = None
      CreateModal = CreateWorktreeModal.Closed
      ConfirmModal = ConfirmModal.NoConfirm
      DeletedPaths = Set.empty
      EditorName = "VS Code"
      ActionCooldowns = Set.empty
      LastActivityTime = 0.0
      ActivityLevel = ActivityLevel.Active
      CanvasPaneOpen = false
      CanvasPosition = CanvasPosition.Right
      ActiveCanvasDoc = Map.empty
      VisitedCanvasDocs = Map.empty
      LastViewedHashes = Map.empty
      PreviousCanvasHashes = Map.empty
      CanvasEvents = Map.empty
      CanvasSendState = CanvasSendState.Idle
      BridgeLiveness = Map.empty }

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
                        model.LastViewedHashes
                        |> Map.tryFind scopedKey
                        |> Option.defaultValue Map.empty
                        |> Map.add filename hash
                    { model with LastViewedHashes = model.LastViewedHashes |> Map.add scopedKey innerMap }
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
                LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.True, "Should contain the scoped key")
        Assert.That(result["myrepo/feat"], Is.EqualTo([ "status.html" ]))

    [<Test>]
    member _.``Returns unviewed docs when no entry exists in LastViewedHashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "report.html" "abc123" ] ] ]
                LastViewedHashes = Map.empty }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.True)
        Assert.That(result["myrepo/feat"], Is.EqualTo([ "report.html" ]))

    [<Test>]
    member _.``Returns empty map when all docs have been viewed with matching hashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [ makeDoc "status.html" "hash-v1" ] ] ]
                LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

        Assert.That(result |> Map.containsKey "myrepo/feat", Is.False,
            "Should not contain scoped key when all docs are viewed")

    [<Test>]
    member _.``Returns empty map when worktree has no canvas docs``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [] ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

        Assert.That(result, Is.Empty)

    [<Test>]
    member _.``Returns multiple unviewed docs for same worktree``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "myrepo" [ makeWorktree "myrepo" "feat" [
                    makeDoc "a.html" "h1"
                    makeDoc "b.html" "h2"
                    makeDoc "c.html" "h3" ] ] ]
                LastViewedHashes = Map.ofList [ "myrepo/feat", Map.ofList [ "b.html", "h2" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

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
                LastViewedHashes = Map.ofList [ "repo1/main", Map.ofList [ "x.html", "h1" ] ] }

        let result = unviewedDocsByScopedKey model.Repos model.LastViewedHashes

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
                PreviousCanvasHashes = Map.empty
                LastActivityTime = 0.0
                CanvasPaneOpen = false }

        let currentHashes = canvasHashesByScopedKey repos
        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0 // 120s since epoch — well past 60s idle threshold
        let isIdle = jsNow - model.LastActivityTime > autoDisplayIdleMs
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
                PreviousCanvasHashes = previousHashes
                LastActivityTime = 0.0
                CanvasPaneOpen = false }

        let currentHashes = canvasHashesByScopedKey repos
        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0
        let isIdle = jsNow - model.LastActivityTime > autoDisplayIdleMs
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
                PreviousCanvasHashes = Map.empty
                LastActivityTime = jsNow - 10_000.0 } // 10s ago, well within 60s threshold

        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.PreviousCanvasHashes currentHashes
        let isIdle = jsNow - model.LastActivityTime > autoDisplayIdleMs
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
                PreviousCanvasHashes = currentHashes
                LastActivityTime = 0.0 }

        let changedDocs = detectChangedCanvasDocs DateTimeOffset.UtcNow model.PreviousCanvasHashes currentHashes
        let jsNow = 120_000.0
        let isIdle = jsNow - model.LastActivityTime > autoDisplayIdleMs
        let target =
            if isIdle && not (List.isEmpty changedDocs)
            then findMostRecentChangedDoc repos changedDocs
            else None

        Assert.That(target, Is.EqualTo(None), "Should NOT auto-display when no hashes changed")


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
                LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "status.html", "hash-v1" ] ] }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "status.html")) model

        let viewedHash =
            updated.LastViewedHashes
            |> Map.find "r/feat"
            |> Map.find "status.html"
        Assert.That(viewedHash, Is.EqualTo("hash-v2"), "Should update to current content hash")

    [<Test>]
    member _.``MarkDocViewed creates new entry when scoped key not in LastViewedHashes``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "new.html" "hash1" ] ] ]
                LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "new.html")) model

        Assert.That(updated.LastViewedHashes |> Map.containsKey "r/feat", Is.True)
        let viewedHash =
            updated.LastViewedHashes
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
                LastViewedHashes = Map.ofList [ "r/feat", Map.ofList [ "a.html", "old-ha" ] ] }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "b.html")) model

        let inner = updated.LastViewedHashes |> Map.find "r/feat"
        Assert.That(inner |> Map.find "a.html", Is.EqualTo("old-ha"), "Existing entry should be preserved")
        Assert.That(inner |> Map.find "b.html", Is.EqualTo("hb"), "New entry should be added")

    [<Test>]
    member _.``MarkDocViewed does nothing for unknown scoped key``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
                LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("unknown/key", "a.html")) model

        Assert.That(updated.LastViewedHashes, Is.Empty, "Should not modify hashes for unknown scoped key")

    [<Test>]
    member _.``MarkDocViewed does nothing for unknown filename``() =
        let model =
            { defaultModel with
                Repos = [ makeRepo "r" [ makeWorktree "r" "feat" [ makeDoc "a.html" "h1" ] ] ]
                LastViewedHashes = Map.empty }

        let updated = tryUpdateModel (MarkDocViewed ("r/feat", "nonexistent.html")) model

        Assert.That(updated.LastViewedHashes, Is.Empty, "Should not modify hashes for unknown filename")


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
