/// Scaffolding shared by the diff endpoint fixtures: fake diff services, in-process handler calls, a
/// real HTTP diff server, and the JSON readers its responses are asserted with. The module is
/// `internal` because the Server types it hands out are.
module internal Tests.DiffEndpointTestHelpers

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open NUnit.Framework
open Shared
open global.Server

/// The categorization every response carries for a repository that declares none: an empty path on
/// each file and a `missing` status on a ready summary. Expectations that are not about
/// categorization omit both, so this fills them in centrally; an expectation that states either one
/// keeps it.
let private withCategorizationDefaults (json: string) =
    let root = JsonNode.Parse(json).AsObject()

    // The defaults are written into `root` in place: `JsonNode` is an interop DOM with no immutable
    // counterpart, and its nodes carry a single parent, so rebuilding would have to `DeepClone` every
    // retained child. The DOM is parsed above and serialized below, so this fixture is owned here and
    // nothing observes the mutation.
    let withEmptyCategoryPath (node: JsonNode) =
        match node with
        | :? JsonObject as file when not (file.ContainsKey("categoryPath")) ->
            file["categoryPath"] <- JsonArray()
        | _ -> ()

    let fileNodes =
        match root["files"] with
        | :? JsonArray as files -> List.ofSeq files
        | _ -> []

    root["file"] :: fileNodes |> List.iter withEmptyCategoryPath

    let isReadySummary =
        match root["status"] with
        | :? JsonValue as status -> status.GetValue<string>() = "ready"
        | _ -> false

    if isReadySummary && not (root.ContainsKey("categorization")) then
        let revision = DiffCategories.revision DiffCategories.Missing

        root["categorization"] <-
            JsonNode.Parse($"""{{"status":"missing","reason":null,"revision":"{revision}"}}""")

    root.ToJsonString()

let assertJson (expected: string) (actual: string) =
    let expectedNode = JsonNode.Parse(expected |> withCategorizationDefaults)
    let actualNode = JsonNode.Parse(actual)

    Assert.That(
        JsonNode.DeepEquals(expectedNode, actualNode),
        Is.True,
        $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}"
    )

let getResponseBody (response: HttpResponseMessage) : string =
    response.Content.ReadAsStringAsync()
    |> Async.AwaitTask
    |> TestUtils.runAsync

let get
    (client: HttpClient)
    (url: string)
    : HttpResponseMessage =
    client.GetAsync(url)
    |> Async.AwaitTask
    |> TestUtils.runAsync

let worktreeUrl
    (baseUrl: string)
    (worktreePath: string)
    (endpoint: string)
    =
    let encoded =
        worktreePath
        |> PathUtils.normalizePath
        |> Uri.EscapeDataString

    $"{baseUrl}/{encoded}/{endpoint}"

/// The scheduler keys a repository by `PathUtils.toRepoId` of its root, and the diff endpoint reads
/// the categorization from that key, so a test that cares about the repository root supplies it.
let private agentKnowingRepository
    (repoId: RepoId)
    (worktreePaths: string list)
    upstreamRemote
    baseBranch
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
        RefreshScheduler.repositoryDiscoveryUpdate
            repoId
            (Some worktrees)
            upstreamRemote
            baseBranch
    )

    agent.PostAndAsyncReply(RefreshScheduler.GetState)
    |> TestUtils.runAsync
    |> ignore

    agent

let withDiffServerRepository
    (repoId: RepoId)
    responseDeadlineMs
    (worktreePaths: string list)
    upstreamRemote
    baseBranch
    (service: WorktreeDiffApi.Service)
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (action: MailboxProcessor<RefreshScheduler.StateMsg> -> HttpClient -> string -> unit)
    =
    let port = TestUtils.getFreeTcpPort ()
    let agent =
        agentKnowingRepository
            repoId
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

let withDiffServerConfiguration
    responseDeadlineMs
    (worktreePaths: string list)
    upstreamRemote
    baseBranch
    (service: WorktreeDiffApi.Service)
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (action: MailboxProcessor<RefreshScheduler.StateMsg> -> HttpClient -> string -> unit)
    =
    withDiffServerRepository
        (RepoId "diff-endpoint-tests")
        responseDeadlineMs
        worktreePaths
        upstreamRemote
        baseBranch
        service
        newIdentity
        action

let withDiffServerDeadline
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

let withDiffServer
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

let fakeService
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

let summaryIdentity
    (displayPath: string)
    (json: string)
    =
    use doc = JsonDocument.Parse(json)

    let file =
        doc.RootElement.GetProperty("files").EnumerateArray()
        |> Seq.find (fun file ->
            file.GetProperty("displayPath").GetString() = displayPath)

    file.GetProperty("identity").GetString()

/// Every file of a summary as the browser reads it: display path with its category path, in
/// response order, so a test states grouping and ordering as one expectation.
let summaryCategoryPaths (json: string) =
    use doc = JsonDocument.Parse(json)

    doc.RootElement.GetProperty("files").EnumerateArray()
    |> Seq.map (fun file ->
        file.GetProperty("displayPath").GetString(),
        file.GetProperty("categoryPath").EnumerateArray()
        |> Seq.map _.GetString()
        |> List.ofSeq)
    |> List.ofSeq

let summaryCategorization (json: string) =
    use doc = JsonDocument.Parse(json)
    let categorization = doc.RootElement.GetProperty("categorization")
    let reason = categorization.GetProperty("reason")

    categorization.GetProperty("status").GetString(),
    (if reason.ValueKind = JsonValueKind.Null then
         None
     else
         Some(reason.GetString()))

let entry
    (path: string)
    (oldPath: string option)
    (status: WorktreeDiff.WorktreeDiffStatus)
    : WorktreeDiff.WorktreeDiffEntry =
    { Path = path
      OldPath = oldPath
      LinesAdded = None
      LinesRemoved = None
      Status = status }

let summary
    (files: WorktreeDiff.WorktreeDiffEntry list)
    : WorktreeDiff.WorktreeDiffSummary =
    { BaseRef = "origin/main"
      MergeBase = "merge-base"
      Files = files }

let availableLayerCounts committed local untracked : WorktreeDiff.WorktreeDiffLayerCounts =
    { CommittedCount = Ok committed
      LocalCount = Ok local
      UntrackedCount = Ok untracked }

let uniformLayerCounts count =
    availableLayerCounts count count count

let fileSummary
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
      Change = change
      CategoryPath = [] }

let private diffContext worktreePath : WorktreeDiff.DiffComparisonContext =
    { WorktreePath = worktreePath
      UpstreamRemote = "origin"
      BaseBranch = "main" }

/// Sends one request through a diff handler in-process, so a summary can be classified against a
/// configuration value directly. The canvas server resolves that value from the repository root per
/// request; `DiffEndpointRepositoryConfigurationTests` covers that resolution over real HTTP.
let private handlerResponse
    (viewer: Guid)
    (query: string)
    (handle: HttpContext -> Task<unit>)
    =
    let ctx = DefaultHttpContext()

    ctx.Request.Headers[WorktreeDiffApi.viewerHeaderName] <-
        StringValues(viewer.ToString("D"))

    ctx.Request.QueryString <- QueryString(query)
    use body = new MemoryStream()
    ctx.Response.Body <- body

    handle ctx |> Async.AwaitTask |> TestUtils.runAsync

    body.ToArray() |> Text.Encoding.UTF8.GetString

let private handlerDeadline () =
    ProcessRunner.createResponseDeadline
        ProcessRunner.argumentListResponseDeadlineMs

let summaryResponse
    (handlers: WorktreeDiffApi.Handlers)
    categorization
    worktree
    viewer
    =
    handlerResponse
        viewer
        ""
        (handlers.Summary
            (handlerDeadline ())
            (Some
                { Comparison = diffContext worktree
                  Categorization = categorization }))

let fileResponse
    (handlers: WorktreeDiffApi.Handlers)
    worktree
    viewer
    identity
    =
    handlerResponse
        viewer
        $"?identity={identity}"
        (handlers.File (handlerDeadline ()) (Some(diffContext worktree)))
