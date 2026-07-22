module Server.WorktreeDiffApi

open Microsoft.AspNetCore.Http
open System.Text.Json
open Shared

type internal Service =
    { GetSummary:
        ProcessRunner.ResponseDeadline
            -> string
            -> Async<
                Result<
                    WorktreeDiff.WorktreeDiffSummary,
                    WorktreeDiff.WorktreeDiffError
                 >
             >
      GetFile:
        ProcessRunner.ResponseDeadline
            -> string
            -> string
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
            -> string
            -> bool
            -> HttpContext
            -> System.Threading.Tasks.Task<unit>
      File:
        ProcessRunner.ResponseDeadline
            -> string
            -> bool
            -> HttpContext
            -> System.Threading.Tasks.Task<unit> }

[<Literal>]
let internal viewerHeaderName = "X-Treemon-Diff-Viewer"

[<Literal>]
let internal maxViewerSnapshotsPerWorktree = 8

type private DiffIdentitySnapshot =
    { MergeBase: string
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
      NextSequence: int64 }

type private DiffIdentityMessage =
    | ReplaceSnapshot of
        worktreePath: string
        * viewerInstance: System.Guid
        * snapshot: DiffIdentitySnapshot
        * AsyncReplyChannel<unit>
    | ClearSnapshot of
        worktreePath: string
        * viewerInstance: System.Guid
        * AsyncReplyChannel<unit>
    | ResolveIdentity of
        worktreePath: string
        * viewerInstance: System.Guid
        * identity: string
        * AsyncReplyChannel<
            (string
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
        let rec loop state =
            async {
                let! message = inbox.Receive()

                match message with
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
                            { Snapshots =
                                state.Snapshots
                                |> Map.add worktreePath viewers
                              NextSequence = sequence }
                | ClearSnapshot (worktreePath, viewerInstance, reply) ->
                    let snapshots =
                        state.Snapshots
                        |> removeViewer worktreePath viewerInstance

                    reply.Reply()
                    return! loop { state with Snapshots = snapshots }
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
                                stored.Snapshot.MergeBase, file, entry)

                        let sequence = state.NextSequence + 1L
                        let touched =
                            { stored with
                                LastUsedAt = sequence }

                        reply.Reply(resolved)

                        return!
                            loop
                                { Snapshots =
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
                                        |> Set.contains worktreePath) }
            }

        loop
            { Snapshots = Map.empty
              NextSequence = 0L })

type internal DiffIdentityStore() =
    let agent = createIdentityAgent ()

    member _.Replace(worktreePath, viewerInstance, mergeBase, files) =
        agent.PostAndAsyncReply(fun reply ->
            ReplaceSnapshot(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                { MergeBase = mergeBase
                  Files =
                    files
                    |> List.map (fun (file, entry) ->
                        file.Identity, (file, entry))
                    |> Map.ofList },
                reply
            ))

    member _.Clear(worktreePath, viewerInstance) =
        agent.PostAndAsyncReply(fun reply ->
            ClearSnapshot(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                reply
            ))

    member _.Resolve(worktreePath, viewerInstance, identity) =
        agent.PostAndAsyncReply(fun reply ->
            ResolveIdentity(
                PathUtils.normalizePath worktreePath,
                viewerInstance,
                identity,
                reply
            ))

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

let internal serializeSummaryResult =
    function
    | DiffSummaryResult.Ready details ->
        JsonSerializer.Serialize(
            {| status = "ready"
               baseRef = details.BaseRef
               fileCount = details.FileCount
               files = details.Files |> List.map diffFileJson |}
        )
    | DiffSummaryResult.Clean baseRef ->
        JsonSerializer.Serialize(
            {| status = "clean"
               baseRef = baseRef
               fileCount = 0
               files = List.empty |}
        )
    | DiffSummaryResult.BaseError ->
        JsonSerializer.Serialize {| status = "base-error" |}
    | DiffSummaryResult.TimedOut ->
        JsonSerializer.Serialize {| status = "timeout" |}
    | DiffSummaryResult.GitError ->
        JsonSerializer.Serialize {| status = "git-error" |}
    | DiffSummaryResult.TooManyFiles minimumFileCount ->
        JsonSerializer.Serialize(
            {| status = "too-many-files"
               minimumFileCount = minimumFileCount |}
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

let private fileResult file =
    function
    | Ok(WorktreeDiff.Text patch) -> DiffFileResult.Text(file, patch)
    | Ok(WorktreeDiff.DeletedFile patch) ->
        DiffFileResult.Deleted(file, patch)
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
    worktreePath
    isKnown
    (ctx: HttpContext)
    =
    task {
        if not isKnown then
            do! writeError deadline ctx 404 "Unknown worktree"
        else
            match viewerInstance ctx with
            | None ->
                do! writeError deadline ctx 400 "Invalid diff viewer"
            | Some _ when ctx.Request.Query.Count <> 0 ->
                do! writeError deadline ctx 400 "Unexpected query parameters"
            | Some viewer ->
                let! result =
                    service.GetSummary deadline worktreePath
                    |> Async.StartAsTask

                let! response =
                    async {
                        match result with
                        | Ok summary
                            when summary.Files.Length
                                 > WorktreeDiff.maxWorktreeDiffFiles ->
                            do! store.Clear(worktreePath, viewer)

                            return
                                DiffSummaryResult.TooManyFiles
                                    summary.Files.Length
                        | Ok summary when summary.Files.IsEmpty ->
                            do! store.Clear(worktreePath, viewer)
                            return DiffSummaryResult.Clean summary.BaseRef
                        | Ok summary ->
                            let issued =
                                summary.Files
                                |> List.map (fun entry ->
                                    issueFile newIdentity entry, entry)

                            do!
                                store.Replace(
                                    worktreePath,
                                    viewer,
                                    summary.MergeBase,
                                    issued
                                )

                            let files = issued |> List.map fst

                            return
                                DiffSummaryResult.Ready
                                    { BaseRef = summary.BaseRef
                                      FileCount = files.Length
                                      Files = files }
                        | Error error ->
                            do! store.Clear(worktreePath, viewer)
                            return summaryErrorResult error
                    }
                    |> Async.StartAsTask

                do!
                    response
                    |> serializeSummaryResult
                    |> writeJson deadline ctx
    }

let private handleFile
    (service: Service)
    (store: DiffIdentityStore)
    deadline
    worktreePath
    isKnown
    (ctx: HttpContext)
    =
    task {
        if not isKnown then
            do! writeError deadline ctx 404 "Unknown worktree"
        else
            match viewerInstance ctx, identityQuery ctx with
            | Some viewer, Some identity ->
                let! resolved =
                    store.Resolve(
                        worktreePath,
                        viewer,
                        identity
                    )
                    |> Async.StartAsTask

                match resolved with
                | None ->
                    do! writeError deadline ctx 404 "Unknown diff identity"
                | Some (mergeBase, file, entry) ->
                    let! result =
                        service.GetFile
                            deadline
                            worktreePath
                            mergeBase
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
