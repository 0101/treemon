module Server.WorktreeDiffApi

open Microsoft.AspNetCore.Http
open System.Text.Json
open Shared

type internal Service =
    { GetSummary:
        ProcessRunner.ResponseDeadline
            -> WorktreeDiff.DiffComparisonContext
            -> WorktreeDiff.WorktreeDiffLayers
            -> Async<
                Result<
                    WorktreeDiff.WorktreeDiffSummary,
                    WorktreeDiff.WorktreeDiffError
                 >
             >
      GetLayerCounts:
        ProcessRunner.ResponseDeadline
            -> WorktreeDiff.DiffComparisonContext
            -> Async<WorktreeDiff.WorktreeDiffLayerCounts>
      GetFile:
        ProcessRunner.ResponseDeadline
            -> string
            -> string
            -> WorktreeDiff.WorktreeDiffLayers
            -> WorktreeDiff.WorktreeDiffEntry
            -> Async<
                Result<
                    WorktreeDiff.WorktreeDiffFile,
                    WorktreeDiff.WorktreeDiffError
                 >
             > }

type internal Handlers =
    { Summary:
        ProcessRunner.ResponseDeadline
            -> WorktreeDiff.DiffComparisonContext option
            -> HttpContext
            -> System.Threading.Tasks.Task<unit>
      File:
        ProcessRunner.ResponseDeadline
            -> WorktreeDiff.DiffComparisonContext option
            -> HttpContext
            -> System.Threading.Tasks.Task<unit> }

[<Literal>]
let internal viewerHeaderName = "X-Treemon-Diff-Viewer"

[<Literal>]
let internal maxViewerSnapshotsPerWorktree = 8

type private DiffIdentitySnapshot =
    { MergeBase: string
      Layers: WorktreeDiff.WorktreeDiffLayers
      Files:
        Map<
            string,
            DiffFileSummary * WorktreeDiff.WorktreeDiffEntry
         > }

type private StoredSnapshot =
    { Snapshot: DiffIdentitySnapshot
      LastUsedAt: int64 }

type private DiffIdentityState =
    { Snapshots: Map<string, Map<System.Guid, StoredSnapshot>>
      LatestSummaries: Map<string, Map<System.Guid, int64>>
      NextSequence: int64 }

type private DiffIdentityMessage =
    | BeginSummary of
        worktreePath: string
        * viewerInstance: System.Guid
        * AsyncReplyChannel<int64>
    | ReplaceSnapshot of
        worktreePath: string
        * viewerInstance: System.Guid
        * snapshot: DiffIdentitySnapshot
        * AsyncReplyChannel<unit>
    | ReplaceCurrentSnapshot of
        worktreePath: string
        * viewerInstance: System.Guid
        * generation: int64
        * snapshot: DiffIdentitySnapshot
        * AsyncReplyChannel<bool>
    | ClearCurrentSnapshot of
        worktreePath: string
        * viewerInstance: System.Guid
        * generation: int64
        * AsyncReplyChannel<bool>
    | ResolveIdentity of
        worktreePath: string
        * viewerInstance: System.Guid
        * identity: string
        * AsyncReplyChannel<
            (string
             * WorktreeDiff.WorktreeDiffLayers
             * DiffFileSummary
             * WorktreeDiff.WorktreeDiffEntry) option
         >
    | RemoveWorktree of worktreePath: string * AsyncReplyChannel<unit>
    | Prune of knownWorktrees: Set<string> * AsyncReplyChannel<unit>

let private removeViewer worktreePath viewerInstance snapshots =
    match snapshots |> Map.tryFind worktreePath with
    | None -> snapshots
    | Some viewers ->
        let remaining = viewers |> Map.remove viewerInstance

        if Map.isEmpty remaining then
            snapshots |> Map.remove worktreePath
        else
            snapshots |> Map.add worktreePath remaining

let private boundViewers viewers =
    let excess =
        Map.count viewers - maxViewerSnapshotsPerWorktree

    if excess <= 0 then
        viewers
    else
        viewers
        |> Map.toSeq
        |> Seq.sortBy (fun (_, stored) -> stored.LastUsedAt)
        |> Seq.skip excess
        |> Map.ofSeq

let private createIdentityAgent () =
    MailboxProcessor.Start(fun inbox ->
        let currentGeneration worktreePath viewerInstance state =
            state.LatestSummaries
            |> Map.tryFind worktreePath
            |> Option.bind (Map.tryFind viewerInstance)

        let rec loop state =
            async {
                let! message = inbox.Receive()

                match message with
                | BeginSummary (worktreePath, viewerInstance, reply) ->
                    let generation = state.NextSequence + 1L

                    let summaries =
                        state.LatestSummaries
                        |> Map.tryFind worktreePath
                        |> Option.defaultValue Map.empty
                        |> Map.add viewerInstance generation

                    reply.Reply(generation)

                    return!
                        loop
                            { state with
                                LatestSummaries =
                                    state.LatestSummaries
                                    |> Map.add worktreePath summaries
                                NextSequence = generation }
                | ReplaceSnapshot (worktreePath, viewerInstance, snapshot, reply) ->
                    let sequence = state.NextSequence + 1L

                    let viewers =
                        state.Snapshots
                        |> Map.tryFind worktreePath
                        |> Option.defaultValue Map.empty
                        |> Map.add
                            viewerInstance
                            { Snapshot = snapshot
                              LastUsedAt = sequence }
                        |> boundViewers

                    reply.Reply()
                    return!
                        loop
                            { state with
                                Snapshots =
                                    state.Snapshots
                                    |> Map.add worktreePath viewers
                                NextSequence = sequence }
                | ReplaceCurrentSnapshot (
                    worktreePath,
                    viewerInstance,
                    generation,
                    snapshot,
                    reply
                  ) ->
                    if
                        currentGeneration worktreePath viewerInstance state
                        <> Some generation
                    then
                        reply.Reply(false)
                        return! loop state
                    else
                        let sequence = state.NextSequence + 1L

                        let viewers =
                            state.Snapshots
                            |> Map.tryFind worktreePath
                            |> Option.defaultValue Map.empty
                            |> Map.add
                                viewerInstance
                                { Snapshot = snapshot
                                  LastUsedAt = sequence }
                            |> boundViewers

                        reply.Reply(true)

                        return!
                            loop
                                { state with
                                    Snapshots =
                                        state.Snapshots
                                        |> Map.add worktreePath viewers
                                    LatestSummaries =
                                        state.LatestSummaries
                                        |> removeViewer
                                            worktreePath
                                            viewerInstance
                                    NextSequence = sequence }
                | ClearCurrentSnapshot (
                    worktreePath,
                    viewerInstance,
                    generation,
                    reply
                  ) ->
                    if
                        currentGeneration worktreePath viewerInstance state
                        <> Some generation
                    then
                        reply.Reply(false)
                        return! loop state
                    else
                        reply.Reply(true)

                        return!
                            loop
                                { state with
                                    Snapshots =
                                        state.Snapshots
                                        |> removeViewer
                                            worktreePath
                                            viewerInstance
                                    LatestSummaries =
                                        state.LatestSummaries
                                        |> removeViewer
                                            worktreePath
                                            viewerInstance }
                | ResolveIdentity (worktreePath, viewerInstance, identity, reply) ->
                    match
                        state.Snapshots
                        |> Map.tryFind worktreePath
                        |> Option.bind (fun viewers ->
                            viewers
                            |> Map.tryFind viewerInstance
                            |> Option.map (fun stored ->
                                viewers, stored))
                    with
                    | None ->
                        reply.Reply(None)
                        return! loop state
                    | Some (viewers, stored) ->
                        let resolved =
                            stored.Snapshot.Files
                            |> Map.tryFind identity
                            |> Option.map (fun (file, entry) ->
                                stored.Snapshot.MergeBase,
                                stored.Snapshot.Layers,
                                file,
                                entry)

                        let sequence = state.NextSequence + 1L
                        let touched =
                            { stored with
                                LastUsedAt = sequence }

                        reply.Reply(resolved)

                        return!
                            loop
                                { state with
                                    Snapshots =
                                        state.Snapshots
                                        |> Map.add
                                            worktreePath
                                            (viewers
                                             |> Map.add
                                                 viewerInstance
                                                 touched)
                                    NextSequence = sequence }
                | RemoveWorktree (worktreePath, reply) ->
                    reply.Reply()

                    return!
                        loop
                            { state with
                                Snapshots =
                                    state.Snapshots
                                    |> Map.remove worktreePath
                                LatestSummaries =
                                    state.LatestSummaries
                                    |> Map.remove worktreePath }
                | Prune (knownWorktrees, reply) ->
                    reply.Reply()

                    return!
                        loop
                            { state with
                                Snapshots =
                                    state.Snapshots
                                    |> Map.filter (fun worktreePath _ ->
                                        knownWorktrees
                                        |> Set.contains worktreePath)
                                LatestSummaries =
                                    state.LatestSummaries
                                    |> Map.filter (fun worktreePath _ ->
                                        knownWorktrees
                                        |> Set.contains worktreePath) }
            }

        loop
            { Snapshots = Map.empty
              LatestSummaries = Map.empty
              NextSequence = 0L })

type internal DiffIdentityStore() =
    let agent = createIdentityAgent ()

    member _.BeginSummary(worktreePath, viewerInstance) =
        agent.PostAndAsyncReply(fun reply ->
            BeginSummary(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                reply
            ))

    member _.Replace(worktreePath, viewerInstance, mergeBase, files) =
        agent.PostAndAsyncReply(fun reply ->
            ReplaceSnapshot(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                { MergeBase = mergeBase
                  Layers = WorktreeDiff.allWorktreeDiffLayers
                  Files =
                    files
                    |> List.map (fun (file, entry) ->
                        file.Identity, (file, entry))
                    |> Map.ofList },
                reply
            ))

    member _.ReplaceCurrent(
        worktreePath,
        viewerInstance,
        generation,
        mergeBase,
        layers,
        files
    ) =
        agent.PostAndAsyncReply(fun reply ->
            ReplaceCurrentSnapshot(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                generation,
                { MergeBase = mergeBase
                  Layers = layers
                  Files =
                    files
                    |> List.map (fun (file, entry) ->
                        file.Identity, (file, entry))
                    |> Map.ofList },
                reply
            ))

    member _.ClearCurrent(worktreePath, viewerInstance, generation) =
        agent.PostAndAsyncReply(fun reply ->
            ClearCurrentSnapshot(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                generation,
                reply
            ))

    member _.ResolveFiltered(worktreePath, viewerInstance, identity) =
        agent.PostAndAsyncReply(fun reply ->
            ResolveIdentity(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                identity,
                reply
            ))

    member this.Resolve(worktreePath, viewerInstance, identity) =
        async {
            let! resolved =
                this.ResolveFiltered(
                    worktreePath,
                    viewerInstance,
                    identity
                )

            return
                resolved
                |> Option.map (fun (mergeBase, _, file, entry) ->
                    mergeBase, file, entry)
        }

    member _.RemoveWorktree(worktreePath) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveWorktree(
                PathUtils.normalizePath worktreePath,
                reply
            ))

    member _.Prune(knownWorktrees) =
        let normalized =
            knownWorktrees
            |> Set.map PathUtils.normalizePath

        agent.PostAndAsyncReply(fun reply ->
            Prune(normalized, reply))

let internal createIdentityStore () =
    DiffIdentityStore()

let private defaultIdentityStore =
    lazy (createIdentityStore ())

let removeWorktree worktreePath =
    defaultIdentityStore.Value.RemoveWorktree(worktreePath)

let prune knownWorktrees =
    defaultIdentityStore.Value.Prune(knownWorktrees)

let internal liveService =
    { GetSummary = WorktreeDiff.getWorktreeDiffSummaryWithinDeadline
      GetLayerCounts =
        WorktreeDiff.getWorktreeDiffLayerCountsWithinDeadline
      GetFile = WorktreeDiff.getWorktreeDiffFileWithinDeadline }

let internal newOpaqueIdentity (_: WorktreeDiff.WorktreeDiffEntry) =
    System.Guid.NewGuid().ToString("N")

let private diffChangeKind =
    function
    | WorktreeDiff.Added -> DiffChangeKind.Added
    | WorktreeDiff.Modified -> DiffChangeKind.Modified
    | WorktreeDiff.Deleted -> DiffChangeKind.Deleted
    | WorktreeDiff.Renamed -> DiffChangeKind.Renamed
    | WorktreeDiff.Untracked -> DiffChangeKind.Untracked
    | WorktreeDiff.TrackedAndUntracked _ -> DiffChangeKind.Modified

let private diffChangeName =
    function
    | DiffChangeKind.Added -> "added"
    | DiffChangeKind.Modified -> "modified"
    | DiffChangeKind.Deleted -> "deleted"
    | DiffChangeKind.Renamed -> "renamed"
    | DiffChangeKind.Untracked -> "untracked"

let private diffFileJson (file: DiffFileSummary) =
    {| identity = file.Identity
       displayPath = file.DisplayPath
       oldDisplayPath = file.OldDisplayPath
       change = diffChangeName file.Change |}

let private diffReplacementName =
    function
    | DiffReplacementKind.Binary -> "binary"
    | DiffReplacementKind.Symlink -> "symlink"

let private layerCountResult =
    function
    | Ok count -> DiffLayerCountResult.Available count
    | Error(WorktreeDiff.BaseNotFound _) -> DiffLayerCountResult.BaseError
    | Error(WorktreeDiff.GitTimedOut _) -> DiffLayerCountResult.TimedOut
    | Error _ -> DiffLayerCountResult.GitError

let private layerCounts (counts: WorktreeDiff.WorktreeDiffLayerCounts) =
    { AlreadyCommitted = layerCountResult counts.CommittedCount
      LocalChanges = layerCountResult counts.LocalCount
      Untracked = layerCountResult counts.UntrackedCount }

let private layerCountJson =
    function
    | DiffLayerCountResult.Available count ->
        {| status = "ready"
           fileCount = Some count |}
    | DiffLayerCountResult.BaseError ->
        {| status = "base-error"
           fileCount = None |}
    | DiffLayerCountResult.TimedOut ->
        {| status = "timeout"
           fileCount = None |}
    | DiffLayerCountResult.GitError ->
        {| status = "git-error"
           fileCount = None |}

let private layerCountsJson (counts: DiffLayerCounts) =
    {| committed = layerCountJson counts.AlreadyCommitted
       local = layerCountJson counts.LocalChanges
       untracked = layerCountJson counts.Untracked |}

let internal serializeSummaryResult counts =
    let countsJson = layerCountsJson counts

    function
    | DiffSummaryResult.Ready details ->
        JsonSerializer.Serialize(
            {| status = "ready"
               baseRef = details.BaseRef
               fileCount = details.FileCount
               files = details.Files |> List.map diffFileJson
               layerCounts = countsJson |}
        )
    | DiffSummaryResult.Clean baseRef ->
        JsonSerializer.Serialize(
            {| status = "clean"
               baseRef = baseRef
               fileCount = 0
               files = List.empty
               layerCounts = countsJson |}
        )
    | DiffSummaryResult.FilteredEmpty ->
        JsonSerializer.Serialize(
            {| status = "filtered-empty"
               fileCount = 0
               files = List.empty
               layerCounts = countsJson |}
        )
    | DiffSummaryResult.Stale ->
        JsonSerializer.Serialize
            {| status = "stale"
               layerCounts = countsJson |}
    | DiffSummaryResult.BaseError ->
        JsonSerializer.Serialize
            {| status = "base-error"
               layerCounts = countsJson |}
    | DiffSummaryResult.TimedOut ->
        JsonSerializer.Serialize
            {| status = "timeout"
               layerCounts = countsJson |}
    | DiffSummaryResult.GitError ->
        JsonSerializer.Serialize
            {| status = "git-error"
               layerCounts = countsJson |}
    | DiffSummaryResult.TooManyFiles minimumFileCount ->
        JsonSerializer.Serialize(
            {| status = "too-many-files"
               minimumFileCount = minimumFileCount
               layerCounts = countsJson |}
        )

let internal serializeFileResult =
    function
    | DiffFileResult.Text (file, patch) ->
        JsonSerializer.Serialize(
            {| status = "text"
               file = diffFileJson file
               patch = patch |}
        )
    | DiffFileResult.Deleted (file, patch) ->
        JsonSerializer.Serialize(
            {| status = "deleted"
               file = diffFileJson file
               patch = patch |}
        )
    | DiffFileResult.Replacement (file, patch, replacement) ->
        JsonSerializer.Serialize(
            {| status = "replacement"
               file = diffFileJson file
               patch = patch
               replacement = diffReplacementName replacement |}
        )
    | DiffFileResult.Binary file ->
        JsonSerializer.Serialize(
            {| status = "binary"
               file = diffFileJson file |}
        )
    | DiffFileResult.Oversized file ->
        JsonSerializer.Serialize(
            {| status = "oversized"
               file = diffFileJson file |}
        )
    | DiffFileResult.Truncated file ->
        JsonSerializer.Serialize(
            {| status = "truncated"
               file = diffFileJson file |}
        )
    | DiffFileResult.Symlink (file, patch) ->
        JsonSerializer.Serialize(
            {| status = "symlink"
               file = diffFileJson file
               patch = patch |}
        )
    | DiffFileResult.Unavailable file ->
        JsonSerializer.Serialize(
            {| status = "unavailable"
               file = diffFileJson file |}
        )
    | DiffFileResult.TimedOut file ->
        JsonSerializer.Serialize(
            {| status = "timeout"
               file = diffFileJson file |}
        )
    | DiffFileResult.GitError file ->
        JsonSerializer.Serialize(
            {| status = "git-error"
               file = diffFileJson file |}
        )

let private issueFile
    (newIdentity: WorktreeDiff.WorktreeDiffEntry -> string)
    (entry: WorktreeDiff.WorktreeDiffEntry)
    =
    { Identity = newIdentity entry
      DisplayPath = entry.Path
      OldDisplayPath = entry.OldPath
      Change = diffChangeKind entry.Status }

let private summaryErrorResult =
    function
    | WorktreeDiff.BaseNotFound _ -> DiffSummaryResult.BaseError
    | WorktreeDiff.GitTimedOut _ -> DiffSummaryResult.TimedOut
    | WorktreeDiff.TooManyFiles minimumCount ->
        DiffSummaryResult.TooManyFiles minimumCount
    | _ -> DiffSummaryResult.GitError

let private summaryResultIfCurrent isCurrent result =
    if isCurrent then result else DiffSummaryResult.Stale

let private fileResult file =
    function
    | Ok(WorktreeDiff.Text patch) -> DiffFileResult.Text(file, patch)
    | Ok(WorktreeDiff.DeletedFile patch) ->
        DiffFileResult.Deleted(file, patch)
    | Ok(WorktreeDiff.Replacement (patch, replacement)) ->
        DiffFileResult.Replacement(
            file,
            patch,
            match replacement with
            | WorktreeDiff.WorktreeDiffReplacement.BinaryContent ->
                DiffReplacementKind.Binary
            | WorktreeDiff.WorktreeDiffReplacement.SymbolicLink ->
                DiffReplacementKind.Symlink
        )
    | Ok WorktreeDiff.Binary -> DiffFileResult.Binary file
    | Ok WorktreeDiff.Oversized -> DiffFileResult.Oversized file
    | Ok WorktreeDiff.Truncated -> DiffFileResult.Truncated file
    | Ok(WorktreeDiff.Symlink patch) ->
        DiffFileResult.Symlink(file, patch)
    | Error WorktreeDiff.FileUnavailable ->
        DiffFileResult.Unavailable file
    | Error(WorktreeDiff.GitTimedOut _) ->
        DiffFileResult.TimedOut file
    | Error _ -> DiffFileResult.GitError file

let private completeWithinDeadline
    (deadline: ProcessRunner.ResponseDeadline)
    (ctx: HttpContext)
    =
    task {
        let completion = ctx.Response.CompleteAsync()
        let remainingMs =
            ProcessRunner.responseDeadlineRemainingMs deadline

        if remainingMs <= 0 then
            ctx.Abort()
        else
            let! completed =
                System.Threading.Tasks.Task.WhenAny(
                    completion,
                    System.Threading.Tasks.Task.Delay(remainingMs)
                )

            if obj.ReferenceEquals(completed, completion) then
                do! completion
            else
                ctx.Abort()
    }

let private writeResponse
    (deadline: ProcessRunner.ResponseDeadline)
    (ctx: HttpContext)
    (contentType: string)
    (content: string)
    =
    task {
        let remainingMs =
            ProcessRunner.responseDeadlineRemainingMs deadline

        if remainingMs <= 0 then
            ctx.Abort()
        else
            use cts =
                System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                    ctx.RequestAborted
                )

            cts.CancelAfter(remainingMs)
            ctx.Response.ContentType <- contentType
            ctx.Response.ContentLength <-
                System.Text.Encoding.UTF8.GetByteCount(content)
            ctx.Response.Headers["Cache-Control"] <- "no-store"

            try
                do! ctx.Response.WriteAsync(content, cts.Token)
                do! completeWithinDeadline deadline ctx
            with :? System.OperationCanceledException ->
                ctx.Abort()
    }

let private writeJson deadline ctx json =
    writeResponse
        deadline
        ctx
        "application/json; charset=utf-8"
        json

let private writeError
    (deadline: ProcessRunner.ResponseDeadline)
    (ctx: HttpContext)
    statusCode
    message
    =
    ctx.Response.StatusCode <- statusCode

    writeResponse
        deadline
        ctx
        "text/plain; charset=utf-8"
        message

let private identityQuery (ctx: HttpContext) =
    if
        ctx.Request.Query.Count <> 1
        || not (ctx.Request.Query.ContainsKey("identity"))
    then
        None
    else
        let values = ctx.Request.Query["identity"]

        if
            values.Count <> 1
            || System.String.IsNullOrWhiteSpace(values[0])
        then
            None
        else
            Some(values[0])

let private queryBoolean
    (ctx: HttpContext)
    (name: string)
    =
    let values = ctx.Request.Query[name]

    if values.Count <> 1 then
        None
    else
        match values[0] with
        | "true" -> Some true
        | "false" -> Some false
        | _ -> None

let private summaryLayers (ctx: HttpContext) =
    if ctx.Request.Query.Count = 0 then
        Some WorktreeDiff.allWorktreeDiffLayers
    elif
        ctx.Request.Query.Count <> 3
        || not (ctx.Request.Query.ContainsKey("committed"))
        || not (ctx.Request.Query.ContainsKey("local"))
        || not (ctx.Request.Query.ContainsKey("untracked"))
    then
        None
    else
        match
            queryBoolean ctx "committed",
            queryBoolean ctx "local",
            queryBoolean ctx "untracked"
        with
        | Some committed, Some local, Some untracked ->
            Some
                ({ AlreadyCommitted = committed
                   LocalChanges = local
                   Untracked = untracked }
                 : WorktreeDiff.WorktreeDiffLayers)
        | _ -> None

let private viewerInstance (ctx: HttpContext) =
    let values = ctx.Request.Headers[viewerHeaderName]

    if values.Count <> 1 then
        None
    else
        match System.Guid.TryParseExact(values[0], "D") with
        | true, viewer -> Some viewer
        | false, _ -> None

let private handleSummary
    (service: Service)
    (store: DiffIdentityStore)
    newIdentity
    deadline
    (comparisonContext: WorktreeDiff.DiffComparisonContext option)
    (ctx: HttpContext)
    =
    task {
        match comparisonContext with
        | None ->
            do! writeError deadline ctx 404 "Unknown worktree"
        | Some comparisonContext ->
            let worktreePath = comparisonContext.WorktreePath

            match viewerInstance ctx, summaryLayers ctx with
            | None, _ ->
                do! writeError deadline ctx 400 "Invalid diff viewer"
            | Some _, None ->
                do! writeError deadline ctx 400 "Invalid diff-summary query"
            | Some viewer, Some layers
                when
                    not layers.AlreadyCommitted
                    && not layers.LocalChanges
                    && not layers.Untracked
                ->
                let! generation =
                    store.BeginSummary(worktreePath, viewer)
                    |> Async.StartAsTask

                let! counts =
                    service.GetLayerCounts deadline comparisonContext
                    |> Async.StartAsTask

                let! isCurrent =
                    store.ClearCurrent(
                        worktreePath,
                        viewer,
                        generation
                    )
                    |> Async.StartAsTask

                do!
                    DiffSummaryResult.FilteredEmpty
                    |> summaryResultIfCurrent isCurrent
                    |> serializeSummaryResult (layerCounts counts)
                    |> writeJson deadline ctx
            | Some viewer, Some layers ->
                let! generation =
                    store.BeginSummary(worktreePath, viewer)
                    |> Async.StartAsTask

                let countsTask =
                    service.GetLayerCounts deadline comparisonContext
                    |> Async.StartAsTask

                let summaryTask =
                    service.GetSummary deadline comparisonContext layers
                    |> Async.StartAsTask

                let! result = summaryTask
                let! counts = countsTask

                let! response =
                    async {
                        match result with
                        | Ok summary
                            when summary.Files.Length
                                 > WorktreeDiff.maxWorktreeDiffFiles ->
                            let! isCurrent =
                                store.ClearCurrent(
                                    worktreePath,
                                    viewer,
                                    generation
                                )

                            return
                                DiffSummaryResult.TooManyFiles
                                    summary.Files.Length
                                |> summaryResultIfCurrent isCurrent
                        | Ok summary when summary.Files.IsEmpty ->
                            let! isCurrent =
                                store.ClearCurrent(
                                    worktreePath,
                                    viewer,
                                    generation
                                )

                            return
                                DiffSummaryResult.Clean summary.BaseRef
                                |> summaryResultIfCurrent isCurrent
                        | Ok summary ->
                            let issued =
                                summary.Files
                                |> List.map (fun entry ->
                                    issueFile newIdentity entry, entry)

                            let! isCurrent =
                                store.ReplaceCurrent(
                                    worktreePath,
                                    viewer,
                                    generation,
                                    summary.MergeBase,
                                    layers,
                                    issued
                                )

                            let files = issued |> List.map fst

                            return
                                DiffSummaryResult.Ready
                                    { BaseRef = summary.BaseRef
                                      FileCount = files.Length
                                      Files = files }
                                |> summaryResultIfCurrent isCurrent
                        | Error error ->
                            let! isCurrent =
                                store.ClearCurrent(
                                    worktreePath,
                                    viewer,
                                    generation
                                )

                            return
                                summaryErrorResult error
                                |> summaryResultIfCurrent isCurrent
                    }
                    |> Async.StartAsTask

                do!
                    response
                    |> serializeSummaryResult (layerCounts counts)
                    |> writeJson deadline ctx
    }

let private handleFile
    (service: Service)
    (store: DiffIdentityStore)
    deadline
    (comparisonContext: WorktreeDiff.DiffComparisonContext option)
    (ctx: HttpContext)
    =
    task {
        match comparisonContext with
        | None ->
            do! writeError deadline ctx 404 "Unknown worktree"
        | Some comparisonContext ->
            let worktreePath = comparisonContext.WorktreePath

            match viewerInstance ctx, identityQuery ctx with
            | Some viewer, Some identity ->
                let! resolved =
                    store.ResolveFiltered(
                        worktreePath,
                        viewer,
                        identity
                    )
                    |> Async.StartAsTask

                match resolved with
                | None ->
                    do! writeError deadline ctx 404 "Unknown diff identity"
                | Some (mergeBase, layers, file, entry) ->
                    let! result =
                        service.GetFile
                            deadline
                            worktreePath
                            mergeBase
                            layers
                            entry
                        |> Async.StartAsTask

                    do!
                        result
                        |> fileResult file
                        |> serializeFileResult
                        |> writeJson deadline ctx
            | _ ->
                do! writeError deadline ctx 400 "Invalid diff-file query"
    }

let internal createHandlersWithStore
    (store: DiffIdentityStore)
    (service: Service)
    newIdentity
    =
    { Summary = handleSummary service store newIdentity
      File = handleFile service store }

let internal createHandlers
    (service: Service)
    newIdentity
    =
    createHandlersWithStore
        defaultIdentityStore.Value
        service
        newIdentity
