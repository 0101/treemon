module Server.WorktreeDiffApi

open Microsoft.AspNetCore.Http
open System.Text.Json
open Shared

type internal Service =
    { GetSummary:
        string
            -> Async<
                Result<
                    WorktreeDiff.WorktreeDiffSummary,
                    WorktreeDiff.WorktreeDiffError
                 >
             >
      GetFile:
        string
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
        string
            -> bool
            -> HttpContext
            -> System.Threading.Tasks.Task<unit>
      File:
        string
            -> bool
            -> HttpContext
            -> System.Threading.Tasks.Task<unit> }

type private DiffIdentitySnapshot =
    { MergeBase: string
      Files:
        Map<
            string,
            DiffFileSummary * WorktreeDiff.WorktreeDiffEntry
         > }

type private DiffIdentityMessage =
    | ReplaceSnapshot of
        worktreePath: string
        * snapshot: DiffIdentitySnapshot
        * AsyncReplyChannel<unit>
    | ClearSnapshot of worktreePath: string * AsyncReplyChannel<unit>
    | ResolveIdentity of
        worktreePath: string
        * identity: string
        * AsyncReplyChannel<
            (string
             * DiffFileSummary
             * WorktreeDiff.WorktreeDiffEntry) option
         >

let private createIdentityStore () =
    MailboxProcessor.Start(fun inbox ->
        let rec loop snapshots =
            async {
                let! message = inbox.Receive()

                match message with
                | ReplaceSnapshot (worktreePath, snapshot, reply) ->
                    let next =
                        snapshots |> Map.add worktreePath snapshot

                    reply.Reply()
                    return! loop next
                | ClearSnapshot (worktreePath, reply) ->
                    let next =
                        snapshots |> Map.remove worktreePath

                    reply.Reply()
                    return! loop next
                | ResolveIdentity (worktreePath, identity, reply) ->
                    let resolved =
                        snapshots
                        |> Map.tryFind worktreePath
                        |> Option.bind (fun snapshot ->
                            snapshot.Files
                            |> Map.tryFind identity
                            |> Option.map (fun (file, entry) ->
                                snapshot.MergeBase, file, entry))

                    reply.Reply(resolved)
                    return! loop snapshots
            }

        loop Map.empty)

let internal liveService =
    { GetSummary = WorktreeDiff.getWorktreeDiffSummary
      GetFile = WorktreeDiff.getWorktreeDiffFile }

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

let private replaceSnapshot
    (store: MailboxProcessor<DiffIdentityMessage>)
    worktreePath
    mergeBase
    files
    =
    store.PostAndAsyncReply(fun reply ->
        ReplaceSnapshot(
            worktreePath,
            { MergeBase = mergeBase
              Files =
                files
                |> List.map (fun (file, entry) ->
                    file.Identity, (file, entry))
                |> Map.ofList },
            reply
        ))

let private clearSnapshot
    (store: MailboxProcessor<DiffIdentityMessage>)
    worktreePath
    =
    store.PostAndAsyncReply(fun reply ->
        ClearSnapshot(worktreePath, reply))

let private resolveIdentity
    (store: MailboxProcessor<DiffIdentityMessage>)
    worktreePath
    identity
    =
    store.PostAndAsyncReply(fun reply ->
        ResolveIdentity(worktreePath, identity, reply))

let private summaryErrorResult =
    function
    | WorktreeDiff.BaseNotFound _ -> DiffSummaryResult.BaseError
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
    | Error _ -> DiffFileResult.GitError file

let private writeJson (ctx: HttpContext) json = task {
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    ctx.Response.Headers["Cache-Control"] <- "no-store"
    do! ctx.Response.WriteAsync(json)
}

let private writeError
    (ctx: HttpContext)
    statusCode
    message
    =
    task {
        ctx.Response.StatusCode <- statusCode
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(message)
    }

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

let private handleSummary
    (service: Service)
    (store: MailboxProcessor<DiffIdentityMessage>)
    newIdentity
    worktreePath
    isKnown
    (ctx: HttpContext)
    =
    task {
        if not isKnown then
            do! writeError ctx 404 "Unknown worktree"
        elif ctx.Request.Query.Count <> 0 then
            do! writeError ctx 400 "Unexpected query parameters"
        else
            let! result =
                service.GetSummary worktreePath
                |> Async.StartAsTask

            let! response =
                async {
                    match result with
                    | Ok summary
                        when summary.Files.Length
                             > WorktreeDiff.maxWorktreeDiffFiles ->
                        do! clearSnapshot store worktreePath

                        return
                            DiffSummaryResult.TooManyFiles
                                summary.Files.Length
                    | Ok summary ->
                        let issued =
                            summary.Files
                            |> List.map (fun entry ->
                                issueFile newIdentity entry, entry)

                        do!
                            replaceSnapshot
                                store
                                worktreePath
                                summary.MergeBase
                                issued

                        let files = issued |> List.map fst

                        return
                            if files.IsEmpty then
                                DiffSummaryResult.Clean summary.BaseRef
                            else
                                DiffSummaryResult.Ready
                                    { BaseRef = summary.BaseRef
                                      FileCount = files.Length
                                      Files = files }
                    | Error error ->
                        do! clearSnapshot store worktreePath
                        return summaryErrorResult error
                }
                |> Async.StartAsTask

            do!
                response
                |> serializeSummaryResult
                |> writeJson ctx
    }

let private handleFile
    (service: Service)
    (store: MailboxProcessor<DiffIdentityMessage>)
    worktreePath
    isKnown
    (ctx: HttpContext)
    =
    task {
        if not isKnown then
            do! writeError ctx 404 "Unknown worktree"
        else
            match identityQuery ctx with
            | None ->
                do! writeError ctx 400 "Invalid diff-file query"
            | Some identity ->
                let! resolved =
                    resolveIdentity store worktreePath identity
                    |> Async.StartAsTask

                match resolved with
                | None ->
                    do! writeError ctx 404 "Unknown diff identity"
                | Some (mergeBase, file, entry) ->
                    let! result =
                        service.GetFile worktreePath mergeBase entry
                        |> Async.StartAsTask

                    do!
                        result
                        |> fileResult file
                        |> serializeFileResult
                        |> writeJson ctx
    }

let internal createHandlers
    (service: Service)
    newIdentity
    =
    let store = createIdentityStore ()

    { Summary = handleSummary service store newIdentity
      File = handleFile service store }
