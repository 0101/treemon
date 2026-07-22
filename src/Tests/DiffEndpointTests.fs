module Tests.DiffEndpointTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
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

let private agentKnowing
    (worktreePaths: string list)
    : MailboxProcessor<RefreshScheduler.StateMsg> =
    let agent = RefreshScheduler.createAgent ()

    let worktrees =
        worktreePaths
        |> List.map (fun path ->
            let info: GitWorktree.WorktreeInfo =
                { Path = PathUtils.normalizePath path
                  Head = ""
                  Branch = Some "test" }

            info)

    agent.Post(
        RefreshScheduler.UpdateWorktreeList(
            RepoId "diff-endpoint-tests",
            worktrees
        )
    )

    agent.PostAndAsyncReply(RefreshScheduler.GetState)
    |> TestUtils.runAsync
    |> ignore

    agent

let private withDiffServer
    (worktreePaths: string list)
    (service: WorktreeDiffApi.Service)
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (action: HttpClient -> string -> unit)
    =
    let port = TestUtils.getFreeTcpPort ()
    let agent = agentKnowing worktreePaths

    use host =
        CanvasDocServer.createHost
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
        action client $"http://127.0.0.1:{port}"
    finally
        host.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()

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
    { GetSummary = fun _ -> async.Return summary
      GetFile = fun _ _ entry -> async.Return(file entry) }

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

let private entry
    (path: string)
    (oldPath: string option)
    (status: WorktreeDiff.WorktreeDiffStatus)
    : WorktreeDiff.WorktreeDiffEntry =
    { Path = path
      OldPath = oldPath
      Status = status }

let private summary
    (files: WorktreeDiff.WorktreeDiffEntry list)
    : WorktreeDiff.WorktreeDiffSummary =
    { BaseRef = "origin/main"
      MergeBase = "merge-base"
      Files = files }

let private fileSummary
    (identity: string)
    (path: string)
    (oldPath: string option)
    (change: DiffChangeKind)
    : DiffFileSummary =
    { Identity = identity
      DisplayPath = path
      OldDisplayPath = oldPath
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
        fileSummary
            "opaque-1"
            "new.txt"
            (Some "old.txt")
            DiffChangeKind.Renamed

    [<Test>]
    member _.``summary results serialize as a stable tagged renderer-neutral contract``() =
        let cases =
            [ DiffSummaryResult.Ready
                  { BaseRef = "origin/main"
                    FileCount = 1
                    Files = [ file ] },
              """{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}]}"""
              DiffSummaryResult.Clean "main",
              """{"status":"clean","baseRef":"main","fileCount":0,"files":[]}"""
              DiffSummaryResult.BaseError,
              """{"status":"base-error"}"""
              DiffSummaryResult.TimedOut,
              """{"status":"timeout"}"""
              DiffSummaryResult.GitError,
              """{"status":"git-error"}"""
              DiffSummaryResult.TooManyFiles 1001,
              """{"status":"too-many-files","minimumFileCount":1001}""" ]

        cases
        |> List.iter (fun (result, expected) ->
            result
            |> WorktreeDiffApi.serializeSummaryResult
            |> assertJson expected)

    [<Test>]
    member _.``file results serialize every semantic state without renderer concepts``() =
        let cases =
            [ DiffFileResult.Text(file, "patch"),
              """{"status":"text","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},"patch":"patch"}"""
              DiffFileResult.Deleted(file, "deleted patch"),
              """{"status":"deleted","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},"patch":"deleted patch"}"""
              DiffFileResult.Binary file,
              """{"status":"binary","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}"""
              DiffFileResult.Oversized file,
              """{"status":"oversized","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}"""
              DiffFileResult.Truncated file,
              """{"status":"truncated","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}"""
              DiffFileResult.Symlink(file, Some "link patch"),
              """{"status":"symlink","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},"patch":"link patch"}"""
              DiffFileResult.Symlink(file, None),
              """{"status":"symlink","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},"patch":null}"""
              DiffFileResult.Unavailable file,
              """{"status":"unavailable","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}"""
              DiffFileResult.TimedOut file,
              """{"status":"timeout","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}"""
              DiffFileResult.GitError file,
              """{"status":"git-error","file":{"identity":"opaque-1","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"}}""" ]

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
    member _.``timeout summary completes within its response deadline without partial content``() =
        let worktree = fakePath "timeout-deadline"
        let responseDeadlineMs = 2_000

        let fileName, arguments =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                "powershell",
                [ "-NoProfile"
                  "-Command"
                  "Start-Sleep -Seconds 30" ]
            else
                "sh", [ "-c"; "sleep 30" ]

        let service: WorktreeDiffApi.Service =
            { GetSummary =
                fun _ ->
                    async {
                        let! result =
                            ProcessRunner.runArgumentListWithResponseDeadline
                                responseDeadlineMs
                                1024
                                1024
                                "DiffEndpointDeadlineTest"
                                fileName
                                arguments
                                None

                        return
                            match result with
                            | Error ProcessRunner.TimedOut ->
                                Error(
                                    WorktreeDiff.GitTimedOut
                                        WorktreeDiff.ResolveRemote
                                )
                            | other ->
                                failwith $"Expected process timeout, got {other}"
                    }
              GetFile = fun _ _ _ -> failwith "File endpoint was not expected" }

        withDiffServer
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
                body |> assertJson """{"status":"timeout"}"""

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
        let changed = entry "changed.txt" None WorktreeDiff.Modified

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
                            """{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"change":"modified"}]}"""

                        use fileResponse = getWithHost client host $"{fileUrl}?identity=issued-id"
                        Assert.That(fileResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        fileResponse
                        |> getResponseBody
                        |> assertJson
                            """{"status":"text","file":{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"change":"modified"},"patch":"repository patch"}"""

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
            { GetSummary = fun _ -> failwith "Attacker Host reached diff-summary"
              GetFile = fun _ _ _ -> failwith "Attacker Host reached diff-file" }

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

        let service =
            fakeService
                (Ok(summary [ renamed; untracked ]))
                (fun file ->
                    match file.Status with
                    | WorktreeDiff.Renamed ->
                        Ok(WorktreeDiff.Text "rename patch")
                    | WorktreeDiff.Untracked ->
                        Ok(WorktreeDiff.Text "untracked patch")
                    | _ -> failwith "Unexpected file")

        let identities =
            Map.ofList
                [ "new.txt", "rename-id"
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
                    """{"status":"ready","baseRef":"origin/main","fileCount":2,"files":[{"identity":"rename-id","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},{"identity":"untracked-id","displayPath":"untracked.txt","oldDisplayPath":null,"change":"untracked"}]}"""
                    summaryBody

                use renameResponse =
                    get client $"{fileUrl}?identity=rename-id"

                renameResponse
                |> getResponseBody
                |> assertJson
                    """{"status":"text","file":{"identity":"rename-id","displayPath":"new.txt","oldDisplayPath":"old.txt","change":"renamed"},"patch":"rename patch"}"""

                use untrackedResponse =
                    get client $"{fileUrl}?identity=untracked-id"

                untrackedResponse
                |> getResponseBody
                |> assertJson
                    """{"status":"text","file":{"identity":"untracked-id","displayPath":"untracked.txt","oldDisplayPath":null,"change":"untracked"},"patch":"untracked patch"}""")

    [<Test>]
    member _.``clean and summary error states replace the identity map without partial files``() =
        let cases =
            [ Ok(summary []),
              """{"status":"clean","baseRef":"origin/main","fileCount":0,"files":[]}"""
              Error(
                  WorktreeDiff.BaseNotFound(
                      "main",
                      "origin/main"
                  )
              ),
              """{"status":"base-error"}"""
              Error(
                  WorktreeDiff.GitFailed(
                      WorktreeDiff.ResolveMergeBase,
                      1
                  )
              ),
              """{"status":"git-error"}"""
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.ResolveRemote),
              """{"status":"timeout"}"""
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.ResolveBase),
              """{"status":"timeout"}"""
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.ResolveMergeBase),
              """{"status":"timeout"}"""
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.EnumerateTracked),
              """{"status":"timeout"}"""
              Error(WorktreeDiff.GitTimedOut WorktreeDiff.EnumerateUntracked),
              """{"status":"timeout"}"""
              Error(WorktreeDiff.TooManyFiles 1001),
              """{"status":"too-many-files","minimumFileCount":1001}""" ]

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
                    """{"status":"too-many-files","minimumFileCount":1001}""")

    [<Test>]
    member _.``file route maps deleted binary oversized truncated symlink unavailable and Git error states``() =
        let worktree = fakePath "file-states"

        let entries =
            [ entry "deleted.txt" None WorktreeDiff.Deleted
              entry "binary.dat" None WorktreeDiff.Modified
              entry "oversized.txt" None WorktreeDiff.Untracked
              entry "truncated.txt" None WorktreeDiff.Modified
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
                         DiffChangeKind.Untracked)

            match file.Path with
            | "deleted.txt" ->
                DiffFileResult.Deleted(
                    descriptor,
                    "deleted patch"
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
                fun _ ->
                    failwith "Unknown worktree reached diff summary"
              GetFile =
                fun _ _ _ ->
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
                    Assert.That(getResponseBody response, Is.EqualTo("Unexpected query parameters")))

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
                    """{"status":"ready","baseRef":"origin/main","fileCount":1,"files":[{"identity":"issued-id","displayPath":"changed.txt","oldDisplayPath":null,"change":"modified"}]}"""

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
                    WorktreeDiff.getWorktreeDiffSummary
                  GetFile =
                    WorktreeDiff.getWorktreeDiffFile }

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
                        """{"status":"ready","baseRef":"main","fileCount":1,"files":[{"identity":"tracked-id","displayPath":"tracked.txt","oldDisplayPath":null,"change":"modified"}]}"""
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
                            fileSummary
                                "tracked-id"
                                "tracked.txt"
                                None
                                DiffChangeKind.Modified,
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
