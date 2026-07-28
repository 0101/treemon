module Tests.DiffEndpointTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Shared
open global.Server

let private assertJson (expected: string) (actual: string) =
    let expectedNode = JsonNode.Parse(expected)
    let actualNode = JsonNode.Parse(actual)

    Assert.That(
        JsonNode.DeepEquals(expectedNode, actualNode),
        Is.True,
        $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}"
    )

[<RequireQualifiedAccess>]
type private ExpectedLayerCount =
    | Available of int
    | Error of string

let private withLayerCounts committed local untracked (json: string) =
    let countJson =
        function
        | ExpectedLayerCount.Available count ->
            JsonNode.Parse($"""{{"status":"ready","fileCount":{count}}}""")
        | ExpectedLayerCount.Error status ->
            JsonNode.Parse($"""{{"status":"{status}","fileCount":null}}""")

    let root = JsonNode.Parse(json).AsObject()
    let counts = JsonObject()
    counts["committed"] <- countJson committed
    counts["local"] <- countJson local
    counts["untracked"] <- countJson untracked
    root["layerCounts"] <- counts
    root.ToJsonString()

let private withUniformLayerCount count =
    withLayerCounts
        (ExpectedLayerCount.Available count)
        (ExpectedLayerCount.Available count)
        (ExpectedLayerCount.Available count)

let private withUniformLayerError status =
    withLayerCounts
        (ExpectedLayerCount.Error status)
        (ExpectedLayerCount.Error status)
        (ExpectedLayerCount.Error status)

let private getResponseBody (response: HttpResponseMessage) : string =
    response.Content.ReadAsStringAsync()
    |> Async.AwaitTask
    |> TestUtils.runAsync

let private get
    (client: HttpClient)
    (url: string)
    : HttpResponseMessage =
    client.GetAsync(url)
    |> Async.AwaitTask
    |> TestUtils.runAsync

let private getWithHost
    (client: HttpClient)
    (host: string)
    (url: string)
    : HttpResponseMessage =
    use request = new HttpRequestMessage(HttpMethod.Get, url)
    request.Headers.Host <- host

    client.SendAsync(request)
    |> Async.AwaitTask
    |> TestUtils.runAsync

let private worktreeUrl
    (baseUrl: string)
    (worktreePath: string)
    (endpoint: string)
    =
    let encoded =
        worktreePath
        |> PathUtils.normalizePath
        |> Uri.EscapeDataString

    $"{baseUrl}/{encoded}/{endpoint}"

let private agentKnowingWithConfiguration
    (worktreePaths: string list)
    upstreamRemote
    baseBranch
    : MailboxProcessor<SchedulerState.StateMsg> =
    let agent = SchedulerState.createAgent ()

    let worktrees =
        worktreePaths
        |> List.map (fun path ->
            let info: GitWorktree.WorktreeInfo =
                { Path = PathUtils.normalizePath path
                  Head = ""
                  Branch = Some "test" }

            info)

    let repoId = RepoId "diff-endpoint-tests"
    agent.Post(
        RefreshScheduler.repositoryDiscoveryUpdate
            repoId
            (Some worktrees)
            upstreamRemote
            baseBranch
    )

    agent.PostAndAsyncReply(SchedulerState.GetState)
    |> TestUtils.runAsync
    |> ignore

    agent

let private agentKnowing worktreePaths =
    agentKnowingWithConfiguration worktreePaths "origin" "main"

let private withDiffServerConfiguration
    responseDeadlineMs
    (worktreePaths: string list)
    upstreamRemote
    baseBranch
    (service: WorktreeDiffApi.Service)
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (action: MailboxProcessor<SchedulerState.StateMsg> -> HttpClient -> string -> unit)
    =
    let port = TestUtils.getFreeTcpPort ()
    let agent =
        agentKnowingWithConfiguration
            worktreePaths
            upstreamRemote
            baseBranch

    use host =
        CanvasDocServer.createHostWithDiffDeadline
            responseDeadlineMs
            agent
            service
            newIdentity
            port

    host.StartAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    try
        use client = new HttpClient()
        client.DefaultRequestHeaders.Add(
            WorktreeDiffApi.viewerHeaderName,
            Guid.NewGuid().ToString("D")
        )
        action agent client $"http://127.0.0.1:{port}"
    finally
        host.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()

let private withDiffServerDeadline
    responseDeadlineMs
    (worktreePaths: string list)
    (service: WorktreeDiffApi.Service)
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (action: HttpClient -> string -> unit)
    =
    withDiffServerConfiguration
        responseDeadlineMs
        worktreePaths
        "origin"
        "main"
        service
        newIdentity
        (fun _ client baseUrl -> action client baseUrl)

let private withDiffServer
    worktreePaths
    service
    newIdentity
    action
    =
    withDiffServerDeadline
        ProcessRunner.argumentListResponseDeadlineMs
        worktreePaths
        service
        newIdentity
        action

let private fakeService
    (summary:
        Result<
            WorktreeDiff.WorktreeDiffSummary,
            WorktreeDiff.WorktreeDiffError
         >)
    (file:
        WorktreeDiff.WorktreeDiffEntry
            -> Result<
                WorktreeDiff.WorktreeDiffFile,
                WorktreeDiff.WorktreeDiffError
             >)
    : WorktreeDiffApi.Service =
    let counts: WorktreeDiff.WorktreeDiffLayerCounts =
        match summary with
        | Ok value ->
            { CommittedCount = Ok value.Files.Length
              LocalCount = Ok value.Files.Length
              UntrackedCount = Ok value.Files.Length }
        | Error error ->
            { CommittedCount = Error error
              LocalCount = Error error
              UntrackedCount = Error error }

    { GetSummary = fun _ _ _ -> async.Return summary
      GetLayerCounts = fun _ _ -> async.Return counts
      GetFile = fun _ _ _ _ entry -> async.Return(file entry) }

let private summaryIdentity
    (displayPath: string)
    (json: string)
    =
    use doc = JsonDocument.Parse(json)

    let file =
        doc.RootElement.GetProperty("files").EnumerateArray()
        |> Seq.find (fun file ->
            file.GetProperty("displayPath").GetString() = displayPath)

    file.GetProperty("identity").GetString()

let private summaryPaths (json: string) =
    use doc = JsonDocument.Parse(json)

    doc.RootElement.GetProperty("files").EnumerateArray()
    |> Seq.map _.GetProperty("displayPath").GetString()
    |> Set.ofSeq

let private layerQuery committed local untracked =
    let value enabled = if enabled then "true" else "false"

    $"?committed={value committed}&local={value local}&untracked={value untracked}"

let private entry
    (path: string)
    (oldPath: string option)
    (status: WorktreeDiff.WorktreeDiffStatus)
    : WorktreeDiff.WorktreeDiffEntry =
    { Path = path
      OldPath = oldPath
      LinesAdded = None
      LinesRemoved = None
      Status = status }

let private summary
    (files: WorktreeDiff.WorktreeDiffEntry list)
    : WorktreeDiff.WorktreeDiffSummary =
    { BaseRef = "origin/main"
      MergeBase = "merge-base"
      Files = files }

let private availableLayerCounts committed local untracked : WorktreeDiff.WorktreeDiffLayerCounts =
    { CommittedCount = Ok committed
      LocalCount = Ok local
      UntrackedCount = Ok untracked }

let private uniformLayerCounts count =
    availableLayerCounts count count count

let private serializedLayerCounts =
    { AlreadyCommitted = DiffLayerCountResult.Available 2
      LocalChanges = DiffLayerCountResult.Available 3
      Untracked = DiffLayerCountResult.BaseError }

let private fileSummary
    (identity: string)
    (path: string)
    (oldPath: string option)
    (change: DiffChangeKind)
    : DiffFileSummary =
    { Identity = identity
      DisplayPath = path
      OldDisplayPath = oldPath
      LinesAdded = None
      LinesRemoved = None
      Change = change }

let private replaceIdentity
    (store: WorktreeDiffApi.DiffIdentityStore)
    worktree
    viewer
    mergeBase
    identity
    =
    let changed =
        entry
            "changed.txt"
            None
            WorktreeDiff.Modified

    store.Replace(
        worktree,
        viewer,
        mergeBase,
        [ fileSummary
              identity
              changed.Path
              changed.OldPath
              DiffChangeKind.Modified,
          changed ]
    )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffIdentityStoreTests() =

    [<Test>]
    member _.``viewer snapshots use a per-worktree LRU bound``() =
        let store = WorktreeDiffApi.createIdentityStore ()
        let worktree = Path.Combine(Path.GetTempPath(), $"treemon-diff-store-{Guid.NewGuid():N}")

        let viewers =
            [ 1..WorktreeDiffApi.maxViewerSnapshotsPerWorktree ]
            |> List.map (fun index ->
                Guid.NewGuid(),
                $"identity-{index}")

        viewers
        |> List.iter (fun (viewer, identity) ->
            replaceIdentity
                store
                worktree
                viewer
                "merge-base"
                identity
            |> TestUtils.runAsync)

        let firstViewer, firstIdentity = List.head viewers
        let secondViewer, secondIdentity = viewers[1]
        let newestViewer = Guid.NewGuid()
        let newestIdentity = "identity-newest"

        store.Resolve(worktree, firstViewer, firstIdentity)
        |> TestUtils.runAsync
        |> ignore

        replaceIdentity
            store
            worktree
            newestViewer
            "merge-base"
            newestIdentity
        |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(
                store.Resolve(worktree, secondViewer, secondIdentity)
                |> TestUtils.runAsync,
                Is.EqualTo(
                    None:
                        (string
                         * DiffFileSummary
                         * WorktreeDiff.WorktreeDiffEntry) option
                )
            )
            Assert.That(
                store.Resolve(worktree, firstViewer, firstIdentity)
                |> TestUtils.runAsync
                |> Option.map (fun (mergeBase, file, _) ->
                    mergeBase, file.Identity),
                Is.EqualTo(Some("merge-base", firstIdentity))
            )
            Assert.That(
                store.Resolve(worktree, newestViewer, newestIdentity)
                |> TestUtils.runAsync
                |> Option.map (fun (mergeBase, file, _) ->
                    mergeBase, file.Identity),
                Is.EqualTo(Some("merge-base", newestIdentity))
            ))

    [<Test>]
    member _.``remove and prune prevent stale identities from surviving path reuse``() =
        let store = WorktreeDiffApi.createIdentityStore ()
        let retained = Path.Combine(Path.GetTempPath(), $"treemon-diff-retained-{Guid.NewGuid():N}")
        let reused = Path.Combine(Path.GetTempPath(), $"treemon-diff-reused-{Guid.NewGuid():N}")
        let viewer = Guid.NewGuid()

        replaceIdentity store retained viewer "retained-base" "retained-id"
        |> TestUtils.runAsync

        replaceIdentity store reused viewer "old-base" "old-id"
        |> TestUtils.runAsync

        store.Prune(Set.ofList [ retained; reused ])
        |> TestUtils.runAsync

        store.RemoveWorktree(reused)
        |> TestUtils.runAsync

        replaceIdentity store reused viewer "new-base" "new-id"
        |> TestUtils.runAsync

        store.Prune(Set.singleton reused)
        |> TestUtils.runAsync

        Assert.Multiple(fun () ->
            Assert.That(
                store.Resolve(reused, viewer, "old-id")
                |> TestUtils.runAsync,
                Is.EqualTo(
                    None:
                        (string
                         * DiffFileSummary
                         * WorktreeDiff.WorktreeDiffEntry) option
                )
            )
            Assert.That(
                store.Resolve(reused, viewer, "new-id")
                |> TestUtils.runAsync
                |> Option.map (fun (mergeBase, file, _) ->
                    mergeBase, file.Identity),
                Is.EqualTo(Some("new-base", "new-id"))
            )
            Assert.That(
                store.Resolve(retained, viewer, "retained-id")
                |> TestUtils.runAsync,
                Is.EqualTo(
                    None:
                        (string
                         * DiffFileSummary
                         * WorktreeDiff.WorktreeDiffEntry) option
                )
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffSerializationTests() =

    let file =
        { fileSummary
              "opaque-1"
              "new.txt"
              (Some "old.txt")
              DiffChangeKind.Renamed with
            LinesAdded = Some 4
            LinesRemoved = Some 2 }

    [<Test>]
    member _.``summary results serialize as a stable tagged renderer-neutral contract``() =
        let cases =
            [ (DiffSummaryResult.Ready
                   { BaseRef = "origin/main"
                     FileCount = 1
                     Files = [ file ] },
               """{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}],"layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.Clean "main",
               """{"status":"clean","baseRef":"main","fileCount":0,"files":[],"layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.FilteredEmpty,
               """{"status":"filtered-empty","fileCount":0,"files":[],"layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.Stale,
               """{"status":"stale","layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.BaseError,
               """{"status":"base-error","layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.TimedOut,
               """{"status":"timeout","layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.GitError,
               """{"status":"git-error","layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""")
              (DiffSummaryResult.TooManyFiles 1001,
               """{"status":"too-many-files","minimumFileCount":1001,"layerCounts":{"committed":{"status":"ready","fileCount":2},"local":{"status":"ready","fileCount":3},"untracked":{"status":"base-error","fileCount":null}}}""") ]

        cases
        |> List.iter (fun (result, expected) ->
            result
            |> WorktreeDiffApi.serializeSummaryResult serializedLayerCounts
            |> assertJson expected)

    [<Test>]
    member _.``file results serialize every semantic state without renderer concepts``() =
        let cases =
            [ DiffFileResult.Text(file, "patch"),
              """{"status":"text","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":"patch"}"""
              DiffFileResult.Deleted(file, "deleted patch"),
              """{"status":"deleted","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":"deleted patch"}"""
              DiffFileResult.Replacement(file, "tracked patch", DiffReplacementKind.Binary),
              """{"status":"replacement","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":"tracked patch","replacement":"binary"}"""
              DiffFileResult.Replacement(file, "tracked patch", DiffReplacementKind.Symlink),
              """{"status":"replacement","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":"tracked patch","replacement":"symlink"}"""
              DiffFileResult.Binary file,
              """{"status":"binary","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}"""
              DiffFileResult.Oversized file,
              """{"status":"oversized","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}"""
              DiffFileResult.Truncated file,
              """{"status":"truncated","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}"""
              DiffFileResult.Symlink(file, Some "link patch"),
              """{"status":"symlink","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":"link patch"}"""
              DiffFileResult.Symlink(file, None),
              """{"status":"symlink","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"},"patch":null}"""
              DiffFileResult.Unavailable file,
              """{"status":"unavailable","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}"""
              DiffFileResult.TimedOut file,
              """{"status":"timeout","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}"""
              DiffFileResult.GitError file,
              """{"status":"git-error","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":4,"linesRemoved":2,"change":"renamed"}}""" ]

        cases
        |> List.iter (fun (result, expected) ->
            result
            |> WorktreeDiffApi.serializeFileResult
            |> assertJson expected)

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type DiffEndpointHttpTests() =

    let fakePath name =
        Path.Combine(
            Path.GetTempPath(),
            $"treemon-diff-endpoint-{name}-{Guid.NewGuid():N}"
        )

    [<Test>]
    member _.``summary accepts every fixed layer combination and preserves no-query compatibility``() =
        let worktree = fakePath "layers"
        let committed = entry "committed.txt" None WorktreeDiff.Modified
        let local = entry "local.txt" None WorktreeDiff.Modified
        let untracked = entry "untracked.txt" None WorktreeDiff.Untracked

        let filesFor (layers: WorktreeDiff.WorktreeDiffLayers) =
            [ if layers.AlreadyCommitted then committed
              if layers.LocalChanges then local
              if layers.Untracked then untracked ]

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ layers ->
                    async.Return(Ok(summary (filesFor layers)))
              GetLayerCounts =
                fun _ _ -> async.Return(availableLayerCounts 1 1 1)
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "Layer summary test does not load files" }

        withDiffServer
            [ worktree ]
            service
            _.Path
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"

                use defaultResponse = get client summaryUrl
                let defaultBody = getResponseBody defaultResponse

                Assert.Multiple(fun () ->
                    Assert.That(defaultResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    Assert.That(
                        summaryPaths defaultBody,
                        Is.EqualTo(
                            Set.ofList
                                [ committed.Path
                                  local.Path
                                  untracked.Path ]
                        )
                    ))

                [ false, false, false
                  false, false, true
                  false, true, false
                  false, true, true
                  true, false, false
                  true, false, true
                  true, true, false
                  true, true, true ]
                |> List.iter (fun (includeCommitted, includeLocal, includeUntracked) ->
                    use response =
                        get
                            client
                            (summaryUrl
                             + layerQuery
                                 includeCommitted
                                 includeLocal
                                 includeUntracked)

                    let body = getResponseBody response
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    if
                        not includeCommitted
                        && not includeLocal
                        && not includeUntracked
                    then
                        body
                        |> assertJson
                            ("""{"status":"filtered-empty","fileCount":0,"files":[]}"""
                             |> withUniformLayerCount 1)
                    else
                        let expected =
                            filesFor
                                { AlreadyCommitted = includeCommitted
                                  LocalChanges = includeLocal
                                  Untracked = includeUntracked }
                            |> List.map _.Path
                            |> Set.ofList

                        Assert.That(summaryPaths body, Is.EqualTo(expected))))

    [<Test>]
    member _.``summary exposes independent layer counts while a local-only comparison survives a missing base``() =
        let worktree = fakePath "layer-count-errors"
        let local = entry "local.txt" None WorktreeDiff.Modified
        let expectedSelection: WorktreeDiff.WorktreeDiffLayers =
            { AlreadyCommitted = false
              LocalChanges = true
              Untracked = false }
        let counts: WorktreeDiff.WorktreeDiffLayerCounts =
            { CommittedCount =
                Error(WorktreeDiff.BaseNotFound("main", "origin/main"))
              LocalCount = Ok 2
              UntrackedCount = Ok 1 }

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ selected ->
                    Assert.That(selected, Is.EqualTo(expectedSelection))
                    async.Return(Ok(summary [ local ]))
              GetLayerCounts =
                fun _ _ -> async.Return counts
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "Layer count summary test does not load files" }

        withDiffServer
            [ worktree ]
            service
            _.Path
            (fun client baseUrl ->
                use response =
                    get
                        client
                        (worktreeUrl baseUrl worktree "diff-summary"
                         + layerQuery false true false)

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                response
                |> getResponseBody
                |> assertJson
                    ("""{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"local.txt","displayPath":"local.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"modified"}]}"""
                     |> withLayerCounts
                         (ExpectedLayerCount.Error "base-error")
                         (ExpectedLayerCount.Available 2)
                         (ExpectedLayerCount.Available 1)))

    [<Test>]
    member _.``no-query summary does not report an untracked-only worktree as clean``() =
        let tempDir = fakePath "untracked-only"
        let repoDir = Path.Combine(tempDir, "repo")

        try
            GitTestHelpers.initRepoOnMain repoDir
            GitTestHelpers.gitAssert repoDir "checkout -b feature"
            File.WriteAllText(Path.Combine(repoDir, "untracked.txt"), "untracked")

            withDiffServer
                [ repoDir ]
                WorktreeDiffApi.liveService
                (fun _ -> "untracked-id")
                (fun client baseUrl ->
                    use response =
                        get
                            client
                            (worktreeUrl baseUrl repoDir "diff-summary")

                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    response
                    |> getResponseBody
                    |> assertJson
                        ("""{"status":"ready","baseRef":"main","fileCount":1,"files":[{"identity":"untracked-id","displayPath":"untracked.txt","oldDisplayPath":null,"linesAdded":1,"linesRemoved":0,"change":"untracked"}]}"""
                         |> withLayerCounts
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 1)))
        finally
            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, recursive = true)
                with _ ->
                    ()

    [<Test>]
    member _.``filter refresh makes prior identities stale and preserves selected patch layers``() =
        let worktree = fakePath "filter-stale"
        let changed = entry "changed.txt" None WorktreeDiff.Modified

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ _ ->
                    async.Return(Ok(summary [ changed ]))
              GetLayerCounts =
                fun _ _ -> async.Return(uniformLayerCounts 1)
              GetFile =
                fun _ _ _ layers _ ->
                    let patch =
                        $"{layers.AlreadyCommitted},{layers.LocalChanges},{layers.Untracked}"

                    async.Return(Ok(WorktreeDiff.Text patch)) }

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl = worktreeUrl baseUrl worktree "diff-file"

                use committedResponse =
                    get client (summaryUrl + layerQuery true false false)

                let committedIdentity =
                    committedResponse
                    |> getResponseBody
                    |> summaryIdentity changed.Path

                use localResponse =
                    get client (summaryUrl + layerQuery false true false)

                let localIdentity =
                    localResponse
                    |> getResponseBody
                    |> summaryIdentity changed.Path

                use staleResponse =
                    get client $"{fileUrl}?identity={committedIdentity}"

                use currentResponse =
                    get client $"{fileUrl}?identity={localIdentity}"

                Assert.Multiple(fun () ->
                    Assert.That(localIdentity, Is.Not.EqualTo(committedIdentity))
                    Assert.That(staleResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                    Assert.That(getResponseBody staleResponse, Is.EqualTo("Unknown diff identity"))
                    Assert.That(currentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK)))

                currentResponse
                |> getResponseBody
                |> assertJson (
                    DiffFileResult.Text(
                        fileSummary
                            localIdentity
                            changed.Path
                            None
                            DiffChangeKind.Modified,
                        "False,True,False"
                    )
                    |> WorktreeDiffApi.serializeFileResult
                ))

    [<Test>]
    member _.``out-of-order ready summary returns stale while latest identities resolve``() =
        let worktree = fakePath "filter-race"
        let changed = entry "changed.txt" None WorktreeDiff.Modified
        let firstStarted =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseFirst =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ layers ->
                    async {
                        if layers.AlreadyCommitted && not layers.LocalChanges then
                            firstStarted.TrySetResult(true) |> ignore
                            let! _ = releaseFirst.Task |> Async.AwaitTask
                            ()

                        return Ok(summary [ changed ])
                    }
              GetLayerCounts =
                fun _ _ -> async.Return(uniformLayerCounts 1)
              GetFile =
                fun _ _ _ layers _ ->
                    let patch =
                        $"{layers.AlreadyCommitted},{layers.LocalChanges},{layers.Untracked}"

                    async.Return(Ok(WorktreeDiff.Text patch)) }

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl = worktreeUrl baseUrl worktree "diff-file"

                let firstTask =
                    client.GetAsync(summaryUrl + layerQuery true false false)

                firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
                |> Async.AwaitTask
                |> TestUtils.runAsync
                |> ignore

                use secondResponse =
                    get client (summaryUrl + layerQuery false true false)

                let secondIdentity =
                    secondResponse
                    |> getResponseBody
                    |> summaryIdentity changed.Path

                releaseFirst.TrySetResult(true) |> ignore
                use firstResponse = firstTask.GetAwaiter().GetResult()

                use currentResponse =
                    get client $"{fileUrl}?identity={secondIdentity}"

                Assert.Multiple(fun () ->
                    Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    firstResponse
                    |> getResponseBody
                    |> assertJson ("""{"status":"stale"}""" |> withUniformLayerCount 1)
                    Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    Assert.That(currentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK)))

                currentResponse
                |> getResponseBody
                |> assertJson (
                    DiffFileResult.Text(
                        fileSummary
                            secondIdentity
                            changed.Path
                            None
                            DiffChangeKind.Modified,
                        "False,True,False"
                    )
                    |> WorktreeDiffApi.serializeFileResult
                ))

    [<Test>]
    member _.``older filtered-empty request delayed in layer counts cannot clear newer identities``() =
        let worktree = fakePath "filtered-empty-layer-count-race"
        let changed = entry "changed.txt" None WorktreeDiff.Modified
        let firstLayerCountsStarted =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseFirstLayerCounts =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ _ -> async.Return(Ok(summary [ changed ]))
              GetLayerCounts =
                fun _ _ ->
                    async {
                        if firstLayerCountsStarted.TrySetResult(true) then
                            let! _ =
                                releaseFirstLayerCounts.Task
                                |> Async.AwaitTask
                            ()

                        return uniformLayerCounts 1
                    }
              GetFile =
                fun _ _ _ layers _ ->
                    let patch =
                        $"{layers.AlreadyCommitted},{layers.LocalChanges},{layers.Untracked}"

                    async.Return(Ok(WorktreeDiff.Text patch)) }

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl = worktreeUrl baseUrl worktree "diff-file"

                let filteredEmptyTask =
                    client.GetAsync(summaryUrl + layerQuery false false false)

                firstLayerCountsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
                |> Async.AwaitTask
                |> TestUtils.runAsync
                |> ignore

                use selectedResponse =
                    get client (summaryUrl + layerQuery false true false)

                let selectedIdentity =
                    selectedResponse
                    |> getResponseBody
                    |> summaryIdentity changed.Path

                releaseFirstLayerCounts.TrySetResult(true) |> ignore
                use filteredEmptyResponse =
                    filteredEmptyTask.GetAwaiter().GetResult()

                use currentFileResponse =
                    get client $"{fileUrl}?identity={selectedIdentity}"

                Assert.Multiple(fun () ->
                    Assert.That(filteredEmptyResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    filteredEmptyResponse
                    |> getResponseBody
                    |> assertJson ("""{"status":"stale"}""" |> withUniformLayerCount 1)
                    Assert.That(selectedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    Assert.That(currentFileResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK)))

                currentFileResponse
                |> getResponseBody
                |> assertJson (
                    DiffFileResult.Text(
                        fileSummary
                            selectedIdentity
                            changed.Path
                            None
                            DiffChangeKind.Modified,
                        "False,True,False"
                    )
                    |> WorktreeDiffApi.serializeFileResult
                ))

    [<Test>]
    member _.``out-of-order clean summary returns stale while latest identities resolve``() =
        let worktree = fakePath "clean-filter-race"
        let changed = entry "changed.txt" None WorktreeDiff.Modified
        let firstStarted =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseFirst =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ layers ->
                    async {
                        if layers.AlreadyCommitted && not layers.LocalChanges then
                            firstStarted.TrySetResult(true) |> ignore
                            let! _ = releaseFirst.Task |> Async.AwaitTask
                            return Ok(summary [])
                        else
                            return Ok(summary [ changed ])
                    }
              GetLayerCounts =
                fun _ _ -> async.Return(uniformLayerCounts 1)
              GetFile =
                fun _ _ _ layers _ ->
                    let patch =
                        $"{layers.AlreadyCommitted},{layers.LocalChanges},{layers.Untracked}"

                    async.Return(Ok(WorktreeDiff.Text patch)) }

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl = worktreeUrl baseUrl worktree "diff-file"

                let firstTask =
                    client.GetAsync(summaryUrl + layerQuery true false false)

                firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
                |> Async.AwaitTask
                |> TestUtils.runAsync
                |> ignore

                use secondResponse =
                    get client (summaryUrl + layerQuery false true false)

                let secondIdentity =
                    secondResponse
                    |> getResponseBody
                    |> summaryIdentity changed.Path

                releaseFirst.TrySetResult(true) |> ignore
                use firstResponse = firstTask.GetAwaiter().GetResult()
                use currentResponse =
                    get client $"{fileUrl}?identity={secondIdentity}"

                Assert.Multiple(fun () ->
                    Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    firstResponse
                    |> getResponseBody
                    |> assertJson ("""{"status":"stale"}""" |> withUniformLayerCount 1)
                    Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    Assert.That(currentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK)))

                currentResponse
                |> getResponseBody
                |> assertJson (
                    DiffFileResult.Text(
                        fileSummary
                            secondIdentity
                            changed.Path
                            None
                            DiffChangeKind.Modified,
                        "False,True,False"
                    )
                    |> WorktreeDiffApi.serializeFileResult
                ))

    [<Test>]
    member _.``malformed and unsupported summary filters are rejected before Git``() =
        let worktree = fakePath "invalid-layers"

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ _ ->
                    failwith "Invalid filters reached diff summary"
              GetLayerCounts =
                fun _ _ ->
                    failwith "Invalid filters reached diff layer counts"
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "Invalid filters reached diff file" }

        withDiffServer
            [ worktree ]
            service
            (fun _ -> failwith "Invalid filters issued an identity")
            (fun client baseUrl ->
                let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"

                [ "?committed=true&local=true"
                  "?committed=yes&local=true&untracked=false"
                  "?committed=true&local=true&untracked=false&path=secret"
                  "?committed=true&committed=false&local=true&untracked=false"
                  "?layer=committed" ]
                |> List.iter (fun query ->
                    use response = get client (summaryUrl + query)
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    Assert.That(
                        getResponseBody response,
                        Is.EqualTo("Invalid diff-summary query")
                    )))

    [<Test>]
    member _.``earlier Git work consumes the shared deadline before a later command times out``() =
        let worktree = fakePath "timeout-deadline"
        let responseDeadlineMs = 3_000

        let runDelayedGit deadline seconds =
            ProcessRunner.capture
                { ProcessRunner.Spawn.create "git" with
                    Context = "DiffEndpointDeadlineTest"
                    Limits = ProcessRunner.CaptureLimits.tiny
                    Deadline = ProcessRunner.SharedDeadline deadline }
                [ "-c"
                  $"alias.pause=!sleep {seconds}"
                  "pause" ]

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun deadline _ _ ->
                    async {
                        let! earlier = runDelayedGit deadline 1

                        match earlier with
                        | Ok output when output.ExitCode = 0 ->
                            let! later = runDelayedGit deadline 30

                            return
                                match later with
                                | Error ProcessRunner.TimedOut ->
                                    Error(
                                        WorktreeDiff.GitTimedOut
                                            WorktreeDiff.EnumerateTracked
                                    )
                                | other ->
                                    failwith $"Expected later Git timeout, got {other}"
                        | other ->
                            return
                                failwith $"Expected earlier Git success, got {other}"
                    }
              GetLayerCounts =
                fun _ _ -> async.Return(uniformLayerCounts 0)
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "File endpoint was not expected" }

        withDiffServerDeadline
            responseDeadlineMs
            [ worktree ]
            service
            (fun _ -> "unused")
            (fun client baseUrl ->
                let stopwatch = Stopwatch.StartNew()

                use response =
                    get client (worktreeUrl baseUrl worktree "diff-summary")

                let body = getResponseBody response
                stopwatch.Stop()

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                body
                |> assertJson ("""{"status":"timeout"}""" |> withUniformLayerCount 0)

                Assert.That(
                    stopwatch.ElapsedMilliseconds,
                    Is.LessThan(int64 responseDeadlineMs),
                    $"Complete timeout response took {stopwatch.ElapsedMilliseconds} ms"
                ))

    [<Test>]
    member _.``loopback Host headers can access diff document and asset routes``() =
        let worktree = fakePath "loopback-host"
        let canvasDir = Path.Combine(worktree, ".agents", "canvas")
        let filename = "secret.html"
        let rawHtml = "<!doctype html><html><head><title>Secret</title></head><body>repository document</body></html>"
        let changed =
            { entry "changed.txt" None WorktreeDiff.Modified with
                LinesAdded = Some 7
                LinesRemoved = Some 3 }

        let service =
            fakeService
                (Ok(summary [ changed ]))
                (fun _ -> Ok(WorktreeDiff.Text "repository patch"))

        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(Path.Combine(canvasDir, filename), rawHtml)

        try
            withDiffServer
                [ worktree ]
                service
                (fun _ -> "issued-id")
                (fun client baseUrl ->
                    let port = Uri(baseUrl).Port
                    let summaryUrl = worktreeUrl baseUrl worktree "diff-summary"
                    let fileUrl = worktreeUrl baseUrl worktree "diff-file"
                    let documentUrl = worktreeUrl baseUrl worktree filename
                    let assetUrl = baseUrl + DiffAssets.cssPath
                    let expectedDocument =
                        rawHtml
                        |> CanvasExport.injectAtHead (CanvasDocServer.buildInjection AgentDoc filename)

                    let expectedAsset =
                        DiffAssets.tryFind DiffAssets.cssPath
                        |> Option.map _.Content
                        |> Option.defaultWith (fun () -> failwith "Expected diff CSS asset")

                    [ $"127.0.0.1:{port}"; $"localhost:{port}" ]
                    |> List.iter (fun host ->
                        use summaryResponse = getWithHost client host summaryUrl
                        Assert.That(summaryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        summaryResponse
                        |> getResponseBody
                        |> assertJson
                            ("""{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"linesAdded":7,"linesRemoved":3,"change":"modified"}]}"""
                             |> withUniformLayerCount 1)

                        use fileResponse = getWithHost client host $"{fileUrl}?identity=issued-id"
                        Assert.That(fileResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        fileResponse
                        |> getResponseBody
                        |> assertJson
                            """{"status":"text","file":{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"linesAdded":7,"linesRemoved":3,"change":"modified"},"patch":"repository patch"}"""

                        use documentResponse = getWithHost client host documentUrl
                        Assert.That(documentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(getResponseBody documentResponse, Is.EqualTo(expectedDocument))

                        use assetResponse = getWithHost client host assetUrl
                        Assert.That(assetResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(getResponseBody assetResponse, Is.EqualTo(expectedAsset))))
        finally
            if Directory.Exists(worktree) then
                Directory.Delete(worktree, true)

    [<Test>]
    member _.``attacker Host header is rejected before diff document and asset handlers``() =
        let worktree = fakePath "attacker-host"
        let canvasDir = Path.Combine(worktree, ".agents", "canvas")
        let filename = "secret.html"

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ _ ->
                    failwith "Attacker Host reached diff-summary"
              GetLayerCounts =
                fun _ _ ->
                    failwith "Attacker Host reached diff layer counts"
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "Attacker Host reached diff-file" }

        Directory.CreateDirectory(canvasDir) |> ignore
        File.WriteAllText(
            Path.Combine(canvasDir, filename),
            "<!doctype html><html><head></head><body>repository document</body></html>"
        )

        try
            withDiffServer
                [ worktree ]
                service
                (fun _ -> failwith "Attacker Host issued a diff identity")
                (fun client baseUrl ->
                    let port = Uri(baseUrl).Port
                    let host = $"attacker.example:{port}"

                    [ worktreeUrl baseUrl worktree "diff-summary"
                      worktreeUrl baseUrl worktree "diff-file?identity=forged"
                      worktreeUrl baseUrl worktree filename
                      baseUrl + DiffAssets.cssPath ]
                    |> List.iter (fun url ->
                        use response = getWithHost client host url
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                        Assert.That(getResponseBody response, Is.EqualTo("Invalid Host header"))))
        finally
            if Directory.Exists(worktree) then
                Directory.Delete(worktree, true)

    [<Test>]
    member _.``summary issues opaque identities and file requests resolve only through them``() =
        let worktree = fakePath "success"

        let renamed =
            entry
                "new.txt"
                (Some "old.txt")
                WorktreeDiff.Renamed

        let untracked =
            entry
                "untracked.txt"
                None
                WorktreeDiff.Untracked

        let replaced =
            entry
                "replaced.txt"
                None
                (WorktreeDiff.TrackedAndUntracked WorktreeDiff.Deleted)

        let service =
            fakeService
                (Ok(summary [ renamed; replaced; untracked ]))
                (fun file ->
                    match file.Status with
                    | WorktreeDiff.Renamed ->
                        Ok(WorktreeDiff.Text "rename patch")
                    | WorktreeDiff.Untracked ->
                        Ok(WorktreeDiff.Text "untracked patch")
                    | WorktreeDiff.TrackedAndUntracked _ ->
                        Ok(WorktreeDiff.Text "replacement patch")
                    | _ -> failwith "Unexpected file")

        let identities =
            Map.ofList
                [ "new.txt", "rename-id"
                  "replaced.txt", "replacement-id"
                  "untracked.txt", "untracked-id" ]

        withDiffServer
            [ worktree ]
            service
            (fun file -> identities[file.Path])
            (fun client baseUrl ->
                let summaryUrl =
                    worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file"

                use summaryResponse = get client summaryUrl
                let summaryBody = getResponseBody summaryResponse

                Assert.That(summaryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                assertJson
                    ("""{"status":"ready","baseRef":"origin/main","fileCount":3,"files":[{"identity":"rename-id","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":null,"linesRemoved":null,"change":"renamed"},{"identity":"replacement-id","displayPath":"replaced.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"modified"},{"identity":"untracked-id","displayPath":"untracked.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"untracked"}]}"""
                     |> withUniformLayerCount 3)
                    summaryBody

                use renameResponse =
                    get client $"{fileUrl}?identity=rename-id"

                renameResponse
                |> getResponseBody
                |> assertJson
                    """{"status":"text","file":{"identity":"rename-id","displayPath":"new.txt","oldDisplayPath":"old.txt","linesAdded":null,"linesRemoved":null,"change":"renamed"},"patch":"rename patch"}"""

                use replacementResponse =
                    get client $"{fileUrl}?identity=replacement-id"

                replacementResponse
                |> getResponseBody
                |> assertJson
                    """{"status":"text","file":{"identity":"replacement-id","displayPath":"replaced.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"modified"},"patch":"replacement patch"}"""

                use untrackedResponse =
                    get client $"{fileUrl}?identity=untracked-id"

                untrackedResponse
                |> getResponseBody
                |> assertJson
                    """{"status":"text","file":{"identity":"untracked-id","displayPath":"untracked.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"untracked"},"patch":"untracked patch"}""")

    [<Test>]
    member _.``clean and summary error states replace the identity map without partial files``() =
        let cases =
            [ Ok(summary []),
              ("""{"status":"clean","baseRef":"origin/main","fileCount":0,"files":[]}"""
               |> withUniformLayerCount 0)
              Error(
                  WorktreeDiff.BaseNotFound(
                      "main",
                      "origin/main"
                  )
              ),
              ("""{"status":"base-error"}""" |> withUniformLayerError "base-error")
              Error(
                  WorktreeDiff.GitFailed(
                      WorktreeDiff.ResolveMergeBase,
                      1
                  )
              ),
              ("""{"status":"git-error"}""" |> withUniformLayerError "git-error")
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.ResolveBase),
              ("""{"status":"timeout"}""" |> withUniformLayerError "timeout")
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.ResolveMergeBase),
              ("""{"status":"timeout"}""" |> withUniformLayerError "timeout")
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.EnumerateTracked),
              ("""{"status":"timeout"}""" |> withUniformLayerError "timeout")
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.EnumerateUntracked),
              ("""{"status":"timeout"}""" |> withUniformLayerError "timeout")
              Error(WorktreeDiff.TooManyFiles 1001),
              ("""{"status":"too-many-files","minimumFileCount":1001}"""
               |> withUniformLayerError "git-error") ]

        cases
        |> List.iter (fun (result, expected) ->
            let worktree = fakePath "summary-state"

            let service =
                fakeService result (fun _ ->
                    failwith "No file request should resolve")

            withDiffServer
                [ worktree ]
                service
                (fun _ -> failwith "No identity should be issued")
                (fun client baseUrl ->
                    let fileUrl =
                        worktreeUrl baseUrl worktree "diff-file"

                    use response =
                        get client (worktreeUrl baseUrl worktree "diff-summary")

                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    response |> getResponseBody |> assertJson expected

                    use fileResponse =
                        get
                            client
                            $"{fileUrl}?identity=forged"

                    Assert.That(fileResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                    Assert.That(getResponseBody fileResponse, Is.EqualTo("Unknown diff identity"))))

    [<Test>]
    member _.``a defensive over-limit success is rejected before identities are issued``() =
        let worktree = fakePath "bounded-map"

        let files =
            [ 1..WorktreeDiff.maxWorktreeDiffFiles + 1 ]
            |> List.map (fun index ->
                entry
                    $"file-{index}.txt"
                    None
                    WorktreeDiff.Untracked)

        let service =
            fakeService
                (Ok(summary files))
                (fun _ -> failwith "No over-limit file may resolve")

        withDiffServer
            [ worktree ]
            service
            (fun _ -> failwith "No over-limit identity may be issued")
            (fun client baseUrl ->
                use response =
                    get client (worktreeUrl baseUrl worktree "diff-summary")

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                response
                |> getResponseBody
                |> assertJson
                    ("""{"status":"too-many-files","minimumFileCount":1001}"""
                     |> withUniformLayerCount 1001))

    [<Test>]
    member _.``file route maps deleted binary oversized truncated symlink unavailable and Git error states``() =
        let worktree = fakePath "file-states"

        let entries =
            [ entry "deleted.txt" None WorktreeDiff.Deleted
              entry
                  "binary-replacement.dat"
                  None
                  (WorktreeDiff.TrackedAndUntracked WorktreeDiff.Deleted)
              entry
                  "symlink-replacement.txt"
                  None
                  (WorktreeDiff.TrackedAndUntracked WorktreeDiff.Deleted)
              entry "binary.dat" None WorktreeDiff.Modified
              entry "oversized.txt" None WorktreeDiff.Untracked
              { entry "truncated.txt" None WorktreeDiff.Modified with
                  LinesAdded = Some 20_000
                  LinesRemoved = Some 0 }
              entry "link.txt" None WorktreeDiff.Untracked
              entry "missing.txt" None WorktreeDiff.Untracked
              entry "timeout.txt" None WorktreeDiff.Modified
              entry "git-error.txt" None WorktreeDiff.Modified ]

        let identityFor
            (file: WorktreeDiff.WorktreeDiffEntry)
            =
            "id-" + Path.GetFileNameWithoutExtension(file.Path)

        let service =
            fakeService
                (Ok(summary entries))
                (fun file ->
                    match file.Path with
                    | "deleted.txt" ->
                        Ok(WorktreeDiff.DeletedFile "deleted patch")
                    | "binary-replacement.dat" ->
                        Ok(
                            WorktreeDiff.Replacement(
                                "tracked binary replacement patch",
                                WorktreeDiff.WorktreeDiffReplacement.BinaryContent
                            )
                        )
                    | "symlink-replacement.txt" ->
                        Ok(
                            WorktreeDiff.Replacement(
                                "tracked symlink replacement patch",
                                WorktreeDiff.WorktreeDiffReplacement.SymbolicLink
                            )
                        )
                    | "binary.dat" -> Ok WorktreeDiff.Binary
                    | "oversized.txt" -> Ok WorktreeDiff.Oversized
                    | "truncated.txt" -> Ok WorktreeDiff.Truncated
                    | "link.txt" ->
                        Ok(WorktreeDiff.Symlink None)
                    | "missing.txt" ->
                        Error WorktreeDiff.FileUnavailable
                    | "timeout.txt" ->
                        Error(
                            WorktreeDiff.GitTimedOut
                                WorktreeDiff.LoadFile
                        )
                    | "git-error.txt" ->
                        Error(
                            WorktreeDiff.GitFailed(
                                WorktreeDiff.LoadFile,
                                1
                            )
                        )
                    | other -> failwith $"Unexpected file {other}")

        let expectedResult
            (file: WorktreeDiff.WorktreeDiffEntry)
            =
            let descriptor =
                fileSummary
                    (identityFor file)
                    file.Path
                    file.OldPath
                    (match file.Status with
                     | WorktreeDiff.Added ->
                         DiffChangeKind.Added
                     | WorktreeDiff.Modified ->
                         DiffChangeKind.Modified
                     | WorktreeDiff.Deleted ->
                         DiffChangeKind.Deleted
                     | WorktreeDiff.Renamed ->
                         DiffChangeKind.Renamed
                     | WorktreeDiff.Untracked ->
                         DiffChangeKind.Untracked
                     | WorktreeDiff.TrackedAndUntracked _ ->
                         DiffChangeKind.Modified)

            let descriptor =
                { descriptor with
                    LinesAdded = file.LinesAdded
                    LinesRemoved = file.LinesRemoved }

            match file.Path with
            | "deleted.txt" ->
                DiffFileResult.Deleted(
                    descriptor,
                    "deleted patch"
                )
            | "binary-replacement.dat" ->
                DiffFileResult.Replacement(
                    descriptor,
                    "tracked binary replacement patch",
                    DiffReplacementKind.Binary
                )
            | "symlink-replacement.txt" ->
                DiffFileResult.Replacement(
                    descriptor,
                    "tracked symlink replacement patch",
                    DiffReplacementKind.Symlink
                )
            | "binary.dat" ->
                DiffFileResult.Binary descriptor
            | "oversized.txt" ->
                DiffFileResult.Oversized descriptor
            | "truncated.txt" ->
                DiffFileResult.Truncated descriptor
            | "link.txt" ->
                DiffFileResult.Symlink(descriptor, None)
            | "missing.txt" ->
                DiffFileResult.Unavailable descriptor
            | "timeout.txt" ->
                DiffFileResult.TimedOut descriptor
            | "git-error.txt" ->
                DiffFileResult.GitError descriptor
            | other -> failwith $"Unexpected file {other}"

        withDiffServer
            [ worktree ]
            service
            identityFor
            (fun client baseUrl ->
                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file"

                use summaryResponse =
                    get client (worktreeUrl baseUrl worktree "diff-summary")

                Assert.That(summaryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                entries
                |> List.iter (fun file ->
                    let identity = identityFor file

                    use response =
                        get
                            client
                            $"{fileUrl}?identity={identity}"

                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    response
                    |> getResponseBody
                    |> assertJson (
                        expectedResult file
                        |> WorktreeDiffApi.serializeFileResult
                    )))

    [<Test>]
    member _.``refreshing a summary makes every prior identity stale``() =
        let worktree = fakePath "stale"
        let changed = entry "changed.txt" None WorktreeDiff.Modified

        let service =
            fakeService
                (Ok(summary [ changed ]))
                (fun _ -> Ok(WorktreeDiff.Text "patch"))

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl =
                    worktreeUrl baseUrl worktree "diff-summary"
                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file"

                use firstResponse = get client summaryUrl
                let firstIdentity =
                    firstResponse
                    |> getResponseBody
                    |> summaryIdentity "changed.txt"

                use secondResponse = get client summaryUrl
                let secondIdentity =
                    secondResponse
                    |> getResponseBody
                    |> summaryIdentity "changed.txt"

                Assert.That(secondIdentity, Is.Not.EqualTo(firstIdentity))

                use staleResponse =
                    get
                        client
                        $"{fileUrl}?identity={firstIdentity}"

                Assert.That(staleResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                Assert.That(getResponseBody staleResponse, Is.EqualTo("Unknown diff identity"))

                use currentResponse =
                    get
                        client
                        $"{fileUrl}?identity={secondIdentity}"

                Assert.That(currentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                currentResponse
                |> getResponseBody
                |> assertJson (
                    DiffFileResult.Text(
                        fileSummary
                            secondIdentity
                            "changed.txt"
                            None
                            DiffChangeKind.Modified,
                        "patch"
                    )
                    |> WorktreeDiffApi.serializeFileResult
                ))

    [<Test>]
    member _.``interleaved viewer instances retain independent identity snapshots``() =
        let worktree = fakePath "interleaved-viewers"
        let changed = entry "changed.txt" None WorktreeDiff.Modified

        let service =
            fakeService
                (Ok(summary [ changed ]))
                (fun _ -> Ok(WorktreeDiff.Text "patch"))

        withDiffServer
            [ worktree ]
            service
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun firstClient baseUrl ->
                use secondClient = new HttpClient()
                secondClient.DefaultRequestHeaders.Add(
                    WorktreeDiffApi.viewerHeaderName,
                    Guid.NewGuid().ToString("D")
                )

                let summaryUrl =
                    worktreeUrl baseUrl worktree "diff-summary"

                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file"

                use firstSummary = get firstClient summaryUrl
                let firstIdentity =
                    firstSummary
                    |> getResponseBody
                    |> summaryIdentity "changed.txt"

                use secondSummary = get secondClient summaryUrl
                let secondIdentity =
                    secondSummary
                    |> getResponseBody
                    |> summaryIdentity "changed.txt"

                use firstFile =
                    get
                        firstClient
                        $"{fileUrl}?identity={firstIdentity}"

                use secondFile =
                    get
                        secondClient
                        $"{fileUrl}?identity={secondIdentity}"

                Assert.That(firstFile.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(secondFile.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                use refreshedFirstSummary = get firstClient summaryUrl
                let refreshedFirstIdentity =
                    refreshedFirstSummary
                    |> getResponseBody
                    |> summaryIdentity "changed.txt"

                use staleFirstFile =
                    get
                        firstClient
                        $"{fileUrl}?identity={firstIdentity}"

                use retainedSecondFile =
                    get
                        secondClient
                        $"{fileUrl}?identity={secondIdentity}"

                use currentFirstFile =
                    get
                        firstClient
                        $"{fileUrl}?identity={refreshedFirstIdentity}"

                Assert.Multiple(fun () ->
                    Assert.That(firstIdentity, Is.Not.EqualTo(secondIdentity))
                    Assert.That(refreshedFirstIdentity, Is.Not.EqualTo(firstIdentity))
                    Assert.That(staleFirstFile.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                    Assert.That(getResponseBody staleFirstFile, Is.EqualTo("Unknown diff identity"))
                    Assert.That(retainedSecondFile.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                    Assert.That(currentFirstFile.StatusCode, Is.EqualTo(HttpStatusCode.OK))))

    [<Test>]
    member _.``diff endpoints require one opaque viewer instance header``() =
        let worktree = fakePath "viewer-header"
        let changed = entry "changed.txt" None WorktreeDiff.Modified

        let service =
            fakeService
                (Ok(summary [ changed ]))
                (fun _ -> Ok(WorktreeDiff.Text "patch"))

        withDiffServer
            [ worktree ]
            service
            (fun _ -> "issued-id")
            (fun _ baseUrl ->
                let summaryUrl =
                    worktreeUrl baseUrl worktree "diff-summary"

                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file?identity=issued-id"

                use missingClient = new HttpClient()
                use missingSummary = get missingClient summaryUrl
                use missingFile = get missingClient fileUrl

                use invalidClient = new HttpClient()
                invalidClient.DefaultRequestHeaders.Add(
                    WorktreeDiffApi.viewerHeaderName,
                    "not-a-viewer"
                )

                use invalidSummary = get invalidClient summaryUrl

                Assert.Multiple(fun () ->
                    Assert.That(missingSummary.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    Assert.That(getResponseBody missingSummary, Is.EqualTo("Invalid diff viewer"))
                    Assert.That(missingFile.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    Assert.That(getResponseBody missingFile, Is.EqualTo("Invalid diff-file query"))
                    Assert.That(invalidSummary.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    Assert.That(getResponseBody invalidSummary, Is.EqualTo("Invalid diff viewer"))))

    [<Test>]
    member _.``unknown worktrees and arbitrary roots refs paths or identities never reach Git``() =
        let known = fakePath "known"
        let unknown = Path.Combine(fakePath "outside", "..", "outside-secret")

        let neverCallService: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ _ _ ->
                    failwith "Unknown worktree reached diff summary"
              GetLayerCounts =
                fun _ _ ->
                    failwith "Unknown worktree reached diff layer counts"
              GetFile =
                fun _ _ _ _ _ ->
                    failwith "Unknown identity reached diff file" }

        withDiffServer
            [ known ]
            neverCallService
            (fun _ -> failwith "Unknown worktree issued an identity")
            (fun client baseUrl ->
                use unknownResponse =
                    get
                        client
                        (worktreeUrl baseUrl unknown "diff-summary")

                Assert.That(unknownResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                Assert.That(getResponseBody unknownResponse, Is.EqualTo("Unknown worktree")))

        let changed = entry "changed.txt" None WorktreeDiff.Modified

        let service =
            fakeService
                (Ok(summary [ changed ]))
                (fun _ -> Ok(WorktreeDiff.Text "repository secret"))

        withDiffServer
            [ known ]
            service
            (fun _ -> "issued-id")
            (fun client baseUrl ->
                let summaryUrl =
                    worktreeUrl baseUrl known "diff-summary"
                let fileUrl =
                    worktreeUrl baseUrl known "diff-file"

                [ "?baseRef=HEAD"
                  $"?root={Uri.EscapeDataString(unknown)}"
                  "?path=../outside-secret" ]
                |> List.iter (fun query ->
                    use response = get client (summaryUrl + query)
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                    Assert.That(getResponseBody response, Is.EqualTo("Invalid diff-summary query")))

                use beforeSummary =
                    get
                        client
                        $"{fileUrl}?identity=issued-id"

                Assert.That(beforeSummary.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                Assert.That(getResponseBody beforeSummary, Is.EqualTo("Unknown diff identity"))

                use missingIdentity =
                    get client fileUrl

                Assert.That(missingIdentity.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                Assert.That(getResponseBody missingIdentity, Is.EqualTo("Invalid diff-file query"))

                use summaryResponse = get client summaryUrl
                Assert.That(summaryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                summaryResponse
                |> getResponseBody
                |> assertJson
                    ("""{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"modified"}]}"""
                     |> withUniformLayerCount 1)

                [ $"?identity=issued-id&path={Uri.EscapeDataString(unknown)}",
                  HttpStatusCode.BadRequest,
                  "Invalid diff-file query"
                  $"?identity={Uri.EscapeDataString(unknown)}",
                  HttpStatusCode.NotFound,
                  "Unknown diff identity"
                  "?identity=../outside-secret",
                  HttpStatusCode.NotFound,
                  "Unknown diff identity"
                  "?identity=forged",
                  HttpStatusCode.NotFound,
                  "Unknown diff identity" ]
                |> List.iter (fun (query, expectedStatus, expectedBody) ->
                    use response =
                        get
                            client
                            (fileUrl + query)

                    let body = getResponseBody response
                    Assert.That(response.StatusCode, Is.EqualTo(expectedStatus))
                    Assert.That(body, Is.EqualTo(expectedBody))))

    [<Test>]
    member _.``canvas diff uses scheduler repo configuration for root and linked worktrees``() =
        let tempDir = fakePath "scheduler-config"
        let repoDir = Path.Combine(tempDir, "repo")
        let linkedDir = Path.Combine(tempDir, "linked")
        let configPath = Path.Combine(repoDir, ".treemon.json")

        try
            GitTestHelpers.initRepoOnMain repoDir
            GitTestHelpers.gitOk repoDir [ "branch"; "-M"; "dev" ]
            File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "base")
            GitTestHelpers.gitOk repoDir [ "add"; "--"; "tracked.txt" ]
            GitTestHelpers.gitOk repoDir [ "commit"; "-m"; "dev base" ]
            GitTestHelpers.gitOk repoDir [ "update-ref"; "refs/remotes/origin/dev"; "HEAD" ]
            GitTestHelpers.gitOk repoDir [ "worktree"; "add"; "-b"; "feature"; linkedDir; "dev" ]

            File.AppendAllText(
                Path.Combine(repoDir, ".git", "info", "exclude"),
                Environment.NewLine + ".treemon.json" + Environment.NewLine
            )

            File.WriteAllText(
                configPath,
                """{ "baseBranch": "dev", "upstreamRemote": "origin" }"""
            )

            File.WriteAllText(Path.Combine(linkedDir, "tracked.txt"), "feature")
            GitTestHelpers.gitOk linkedDir [ "add"; "--"; "tracked.txt" ]
            GitTestHelpers.gitOk linkedDir [ "commit"; "-m"; "feature change" ]

            Assert.Multiple(fun () ->
                Assert.That(File.Exists(configPath), Is.True)
                Assert.That(
                    File.Exists(Path.Combine(linkedDir, ".treemon.json")),
                    Is.False
                ))

            let expectedPatch =
                GitTestHelpers.gitOutput
                    linkedDir
                    [ "-c"
                      "core.quotepath=false"
                      "diff"
                      "--no-ext-diff"
                      "--no-textconv"
                      "--find-renames"
                      "--full-index"
                      "--no-color"
                      GitTestHelpers.gitText linkedDir [ "merge-base"; "HEAD"; "origin/dev" ]
                      "--"
                      "tracked.txt" ]

            withDiffServerConfiguration
                ProcessRunner.argumentListResponseDeadlineMs
                [ repoDir; linkedDir ]
                "origin"
                "dev"
                WorktreeDiffApi.liveService
                (fun _ -> "linked-id")
                (fun agent client baseUrl ->
                    let previousCwd = Environment.CurrentDirectory

                    let dashboard =
                        try
                            Directory.SetCurrentDirectory(tempDir)

                            WorktreeApi.getWorktrees
                                agent
                                (SessionManager.createAgent ())
                                None
                                (Map.ofList
                                    [ RepoId "diff-endpoint-tests",
                                      repoDir ])
                                "test"
                                None
                            |> TestUtils.runAsync
                        finally
                            Directory.SetCurrentDirectory(previousCwd)

                    let dashboardBase =
                        dashboard.Repos
                        |> List.exactlyOne
                        |> _.BaseBranch

                    let rootSummaryUrl =
                        worktreeUrl
                            baseUrl
                            repoDir
                            ("diff-summary"
                             + layerQuery true true false)

                    use rootResponse = get client rootSummaryUrl
                    let rootBody = getResponseBody rootResponse

                    assertJson
                        ("""{"status":"clean","baseRef":"origin/dev","fileCount":0,"files":[]}"""
                         |> withLayerCounts
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 0))
                        rootBody

                    let linkedSummaryUrl =
                        worktreeUrl baseUrl linkedDir "diff-summary"

                    use linkedResponse = get client linkedSummaryUrl
                    let linkedBody = getResponseBody linkedResponse
                    use linkedDocument = JsonDocument.Parse(linkedBody)
                    let diffBase =
                        linkedDocument.RootElement
                            .GetProperty("baseRef")
                            .GetString()

                    assertJson
                        ("""{"status":"ready","baseRef":"origin/dev","fileCount":1,"files":[{"identity":"linked-id","displayPath":"tracked.txt","oldDisplayPath":null,"linesAdded":1,"linesRemoved":1,"change":"modified"}]}"""
                         |> withLayerCounts
                             (ExpectedLayerCount.Available 1)
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 0))
                        linkedBody

                    use fileResponse =
                        get
                            client
                            (worktreeUrl
                                baseUrl
                                linkedDir
                                "diff-file?identity=linked-id")

                    fileResponse
                    |> getResponseBody
                    |> assertJson (
                        DiffFileResult.Text(
                            { fileSummary
                                  "linked-id"
                                  "tracked.txt"
                                  None
                                  DiffChangeKind.Modified with
                                LinesAdded = Some 1
                                LinesRemoved = Some 1 },
                            expectedPatch
                        )
                        |> WorktreeDiffApi.serializeFileResult
                    )

                    [ "?baseRef=main"; $"?root={Uri.EscapeDataString(repoDir)}" ]
                    |> List.iter (fun query ->
                        use response = get client (linkedSummaryUrl + query)
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
                        Assert.That(
                            getResponseBody response,
                            Is.EqualTo("Invalid diff-summary query")
                        ))

                    use unknownResponse =
                        get
                            client
                            (worktreeUrl
                                baseUrl
                                (Path.Combine(tempDir, "unknown"))
                                "diff-summary")

                    Assert.Multiple(fun () ->
                        Assert.That(dashboardBase, Is.EqualTo("dev"))
                        Assert.That(
                            diffBase,
                            Is.EqualTo(GitWorktree.mainRef "origin" dashboardBase)
                        )
                        Assert.That(rootResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(linkedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(fileResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        Assert.That(unknownResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                        Assert.That(
                            getResponseBody unknownResponse,
                            Is.EqualTo("Unknown worktree")
                        )))
        finally
            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, recursive = true)
                with _ ->
                    ()

    [<Test>]
    member _.``live routes suppress external diff and textconv commands``() =
        let tempDir =
            Path.Combine(
                Path.GetTempPath(),
                $"treemon-diff-suppression-{Guid.NewGuid():N}"
            )

        let repoDir = Path.Combine(tempDir, "repo")

        try
            GitTestHelpers.initRepoOnMain repoDir
            File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "base")

            File.WriteAllText(
                Path.Combine(repoDir, ".gitattributes"),
                "tracked.txt diff=leak"
            )

            GitTestHelpers.gitAssert repoDir "add -- ."
            GitTestHelpers.gitAssert repoDir "commit -m base"
            GitTestHelpers.gitAssert repoDir "checkout -b feature"
            File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "changed")

            GitTestHelpers.gitAssert
                repoDir
                "config diff.external \"printf external-leak\""

            GitTestHelpers.gitAssert
                repoDir
                "config diff.leak.textconv \"printf textconv-leak\""

            let mergeBase =
                GitTestHelpers.gitOut
                    repoDir
                    "merge-base HEAD main"

            let expectedPatch =
                GitTestHelpers.gitOutput
                    repoDir
                    [ "-c"
                      "core.quotepath=false"
                      "diff"
                      "--no-ext-diff"
                      "--no-textconv"
                      "--find-renames"
                      "--full-index"
                      "--no-color"
                      mergeBase
                      "--"
                      "tracked.txt" ]

            let liveService: WorktreeDiffApi.Service =
                { GetSummary =
                    WorktreeDiff.getWorktreeDiffSummaryWithinDeadline
                  GetLayerCounts =
                    WorktreeDiff.getWorktreeDiffLayerCountsWithinDeadline
                  GetFile =
                    WorktreeDiff.getWorktreeDiffFileWithinDeadline }

            withDiffServer
                [ repoDir ]
                liveService
                (fun _ -> "tracked-id")
                (fun client baseUrl ->
                    let fileUrl =
                        worktreeUrl baseUrl repoDir "diff-file"

                    use summaryResponse =
                        get
                            client
                            (worktreeUrl
                                baseUrl
                                repoDir
                                "diff-summary")

                    let summaryBody = getResponseBody summaryResponse
                    Assert.That(summaryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    assertJson
                        ("""{"status":"ready","baseRef":"main","fileCount":1,"files":[{"identity":"tracked-id","displayPath":"tracked.txt","oldDisplayPath":null,"linesAdded":1,"linesRemoved":1,"change":"modified"}]}"""
                         |> withLayerCounts
                             (ExpectedLayerCount.Available 0)
                             (ExpectedLayerCount.Available 1)
                             (ExpectedLayerCount.Available 0))
                        summaryBody

                    use fileResponse =
                        get
                            client
                            $"{fileUrl}?identity=tracked-id"

                    Assert.That(fileResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

                    fileResponse
                    |> getResponseBody
                    |> assertJson (
                        DiffFileResult.Text(
                            { fileSummary
                                  "tracked-id"
                                  "tracked.txt"
                                  None
                                  DiffChangeKind.Modified with
                                LinesAdded = Some 1
                                LinesRemoved = Some 1 },
                            expectedPatch
                        )
                        |> WorktreeDiffApi.serializeFileResult
                    ))
        finally
            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, recursive = true)
                with _ ->
                    ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type DiffIdentityLifecycleHttpTests() =

    let fakePath name =
        Path.Combine(
            Path.GetTempPath(),
            $"treemon-diff-lifecycle-{name}-{Guid.NewGuid():N}"
        )

    let changed =
        entry "changed.txt" None WorktreeDiff.Modified

    let service repositorySecret =
        fakeService
            (Ok(summary [ changed ]))
            (fun _ -> Ok(WorktreeDiff.Text repositorySecret))

    let newViewerClient () =
        let client = new HttpClient()

        client.DefaultRequestHeaders.Add(
            WorktreeDiffApi.viewerHeaderName,
            Guid.NewGuid().ToString("D")
        )

        client

    let issueIdentity client summaryUrl =
        use response = get client summaryUrl
        let body = getResponseBody response
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        summaryIdentity changed.Path body

    let assertFileResponse repositorySecret identity response =
        let body = getResponseBody response
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))

        body
        |> assertJson (
            DiffFileResult.Text(
                fileSummary
                    identity
                    changed.Path
                    None
                    DiffChangeKind.Modified,
                repositorySecret
            )
            |> WorktreeDiffApi.serializeFileResult
        )

    let assertGenericIdentityNotFound
        (scenario: string)
        (worktree: string)
        (repositorySecret: string)
        (response: HttpResponseMessage)
        =
        let body = getResponseBody response

        Assert.Multiple(fun () ->
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            Assert.That(body, Is.EqualTo("Unknown diff identity")))

        let repositoryContentLeaked =
            body.Contains(worktree, StringComparison.OrdinalIgnoreCase)
            || body.Contains(repositorySecret, StringComparison.Ordinal)

        TestContext.Out.WriteLine(
            JsonSerializer.Serialize(
                {| scenario = scenario
                   httpStatus = int response.StatusCode
                   body = body
                   repositoryContentLeaked = repositoryContentLeaked |}
            )
        )

    [<Test>]
    member _.``HTTP lifecycle retains eight snapshots and evicts the least recently used ninth``() =
        let worktree = fakePath "lru"
        let repositorySecret = "lru repository-only patch"

        withDiffServer
            [ worktree ]
            (service repositorySecret)
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun firstClient baseUrl ->
                let additionalClients =
                    [ 2..WorktreeDiffApi.maxViewerSnapshotsPerWorktree ]
                    |> List.map (fun _ -> newViewerClient ())

                use newestClient = newViewerClient ()

                try
                    let initialClients =
                        firstClient :: additionalClients

                    let summaryUrl =
                        worktreeUrl baseUrl worktree "diff-summary"

                    let fileUrl =
                        worktreeUrl baseUrl worktree "diff-file"

                    let initialIdentities =
                        initialClients
                        |> List.map (fun client ->
                            issueIdentity client summaryUrl)

                    Assert.That(
                        initialIdentities.Length,
                        Is.EqualTo(
                            WorktreeDiffApi.maxViewerSnapshotsPerWorktree
                        )
                    )

                    List.zip initialClients initialIdentities
                    |> List.iter (fun (client, identity) ->
                        use response =
                            get client $"{fileUrl}?identity={identity}"

                        assertFileResponse repositorySecret identity response)

                    let firstIdentity = initialIdentities[0]
                    let evictedIdentity = initialIdentities[1]

                    use touchedResponse =
                        get
                            initialClients[0]
                            $"{fileUrl}?identity={firstIdentity}"

                    assertFileResponse
                        repositorySecret
                        firstIdentity
                        touchedResponse

                    let newestIdentity =
                        issueIdentity newestClient summaryUrl

                    use evictedResponse =
                        get
                            initialClients[1]
                            $"{fileUrl}?identity={evictedIdentity}"

                    assertGenericIdentityNotFound
                        "lru-eviction-at-nine-viewers"
                        worktree
                        repositorySecret
                        evictedResponse

                    let retained =
                        List.zip initialClients initialIdentities
                        |> List.indexed
                        |> List.choose (fun (index, pair) ->
                            if index = 1 then None else Some pair)
                        |> fun existing ->
                            existing
                            @ [ newestClient, newestIdentity ]

                    Assert.That(
                        retained.Length,
                        Is.EqualTo(
                            WorktreeDiffApi.maxViewerSnapshotsPerWorktree
                        )
                    )

                    retained
                    |> List.iter (fun (client, identity) ->
                        use response =
                            get client $"{fileUrl}?identity={identity}"

                        assertFileResponse repositorySecret identity response)
                finally
                    additionalClients |> List.iter _.Dispose())

    [<Test>]
    member _.``successful worktree deletion makes the issued identity a generic HTTP 404``() =
        let repoRoot = fakePath "delete-root"
        let worktree = Path.Combine(repoRoot, "worktree")
        let repositorySecret = "deleted worktree repository-only patch"

        withDiffServer
            [ worktree ]
            (service repositorySecret)
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let summaryUrl =
                    worktreeUrl baseUrl worktree "diff-summary"

                let fileUrl =
                    worktreeUrl baseUrl worktree "diff-file"

                let identity =
                    issueIdentity client summaryUrl

                let deleteAgent = SchedulerState.createAgent ()
                let repoId = PathUtils.toRepoId repoRoot

                let worktreeInfo: GitWorktree.WorktreeInfo =
                    { Path = PathUtils.normalizePath worktree
                      Head = "abc123"
                      Branch = Some "feature" }

                deleteAgent.Post(
                    SchedulerState.UpdateWorktreeList(
                        repoId,
                        [ worktreeInfo ]
                    )
                )

                let result =
                    WorktreeApi.deleteWorktreeWith
                        (fun _ _ _ -> async.Return(Ok()))
                        WorktreeDiffApi.removeWorktree
                        deleteAgent
                        (RefreshScheduler.buildRootPaths [ repoRoot ])
                        (PathUtils.toWorktreePath worktree)
                    |> TestUtils.runAsync

                match result with
                | Ok () -> ()
                | Error error ->
                    Assert.Fail(
                        $"Expected successful worktree deletion, got {error}"
                    )

                use removedResponse =
                    get client $"{fileUrl}?identity={identity}"

                assertGenericIdentityNotFound
                    "successful-worktree-deletion"
                    worktree
                    repositorySecret
                    removedResponse)

    [<Test>]
    member _.``all-ready scheduler reconciliation prunes identities absent from known worktrees``() =
        let retainedWorktree = fakePath "reconcile-retained"
        let removedWorktree = fakePath "reconcile-removed"
        let repositorySecret = "reconciled worktree repository-only patch"

        withDiffServer
            [ retainedWorktree; removedWorktree ]
            (service repositorySecret)
            (fun _ -> Guid.NewGuid().ToString("N"))
            (fun client baseUrl ->
                let retainedSummaryUrl =
                    worktreeUrl
                        baseUrl
                        retainedWorktree
                        "diff-summary"

                let removedSummaryUrl =
                    worktreeUrl
                        baseUrl
                        removedWorktree
                        "diff-summary"

                let retainedFileUrl =
                    worktreeUrl
                        baseUrl
                        retainedWorktree
                        "diff-file"

                let removedFileUrl =
                    worktreeUrl
                        baseUrl
                        removedWorktree
                        "diff-file"

                let retainedIdentity =
                    issueIdentity client retainedSummaryUrl

                let removedIdentity =
                    issueIdentity client removedSummaryUrl

                let reconcileAgent =
                    SchedulerState.createAgent ()

                let retainedInfo: GitWorktree.WorktreeInfo =
                    { Path =
                        PathUtils.normalizePath
                            retainedWorktree
                      Head = "abc123"
                      Branch = Some "retained" }

                let retainedRepo =
                    { SchedulerState.PerRepoState.empty with
                        WorktreeList = [ retainedInfo ]
                        KnownPaths = Set.singleton retainedInfo.Path
                        IsReady = true }

                let pendingRepo =
                    { SchedulerState.PerRepoState.empty with
                        IsReady = false }

                let pendingRepoId = RepoId "pending"

                let reposBeforeAllReady =
                    Map.ofList
                        [ RepoId "retained", retainedRepo
                          pendingRepoId, pendingRepo ]

                let watchersBeforeAllReady =
                    RefreshScheduler.CanvasWatchers.reconcile
                        reconcileAgent
                        reposBeforeAllReady
                        Map.empty
                    |> TestUtils.runAsync

                use beforeReadyResponse =
                    get
                        client
                        $"{removedFileUrl}?identity={removedIdentity}"

                assertFileResponse
                    repositorySecret
                    removedIdentity
                    beforeReadyResponse

                let allReadyRepos =
                    reposBeforeAllReady
                    |> Map.add
                        pendingRepoId
                        { pendingRepo with IsReady = true }

                let watchersAfterAllReady =
                    RefreshScheduler.CanvasWatchers.reconcile
                        reconcileAgent
                        allReadyRepos
                        watchersBeforeAllReady
                    |> TestUtils.runAsync

                try
                    use prunedResponse =
                        get
                            client
                            $"{removedFileUrl}?identity={removedIdentity}"

                    assertGenericIdentityNotFound
                        "all-ready-scheduler-reconciliation"
                        removedWorktree
                        repositorySecret
                        prunedResponse

                    use retainedResponse =
                        get
                            client
                            $"{retainedFileUrl}?identity={retainedIdentity}"

                    assertFileResponse
                        repositorySecret
                        retainedIdentity
                        retainedResponse
                finally
                    RefreshScheduler.CanvasWatchers.disposeAll
                        watchersAfterAllReady)
