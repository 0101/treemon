module Server.CanvasInteractionOwnership

open System
open System.IO

let private normalizePath = Server.PathUtils.normalizePath
let private normalizeFilename (filename: string) = filename.ToLowerInvariant()

type private PendingClaim =
    | Initial of token: Guid
    | Reassignment of token: Guid * previousOwner: string option

type internal InitialClaim =
    | ExistingOwner of sessionId: string
    | ClaimStarted of token: Guid

type internal Reassignment =
    { Token: Guid
      PreviousOwner: string option }

type FollowResult =
    | Assigned
    | Unchanged
    | Deferred

type internal TargetStore =
    { Assign: string -> string -> string -> Async<unit>
      GetOwner: string -> string -> Async<string option>
      RemoveView: string -> string -> Async<unit>
      RemoveWorktree: string -> Async<unit>
      Prune: Set<string> -> Async<unit> }

type private Msg =
    | Assign of worktreeKey: string * filename: string * sessionId: string * AsyncReplyChannel<unit>
    | FollowLastActive of worktreeKey: string * filename: string * sessionId: string * AsyncReplyChannel<FollowResult>
    | BeginClaim of worktreeKey: string * filename: string * AsyncReplyChannel<InitialClaim>
    | CancelClaim of worktreeKey: string * filename: string * token: Guid * AsyncReplyChannel<unit>
    | BeginReassignment of worktreeKey: string * filename: string * AsyncReplyChannel<Result<Reassignment, string>>
    | CancelReassignment of worktreeKey: string * filename: string * token: Guid * AsyncReplyChannel<unit>
    | ClaimPending of worktreeKey: string * token: Guid * sessionId: string * AsyncReplyChannel<string option>
    | GetOwner of worktreeKey: string * filename: string * AsyncReplyChannel<string option>
    | GetDeliveryOwner of worktreeKey: string * filename: string * AsyncReplyChannel<string option>
    | RemoveView of worktreeKey: string * filename: string * AsyncReplyChannel<unit>
    | RemoveWorktree of worktreeKey: string * AsyncReplyChannel<unit>
    | Prune of knownWorktrees: Set<string> * AsyncReplyChannel<unit>

let private removePendingView worktreeKey filename pending =
    match pending |> Map.tryFind worktreeKey with
    | None -> pending
    | Some views ->
        let remaining = views |> Map.remove filename
        if Map.isEmpty remaining then pending |> Map.remove worktreeKey
        else pending |> Map.add worktreeKey remaining

let private trackedPendingKeys knownWorktrees pending =
    pending
    |> Map.toSeq
    |> Seq.filter (fun (worktreeKey, _) -> knownWorktrees |> Set.contains worktreeKey)
    |> Seq.collect (fun (worktreeKey, views) ->
        views
        |> Map.keys
        |> Seq.map (fun filename -> worktreeKey, filename))
    |> Set.ofSeq

let private keepPending existingViewKeys pending =
    pending
    |> Map.toSeq
    |> Seq.choose (fun (worktreeKey, views) ->
        let existing =
            views
            |> Map.filter (fun filename _ ->
                existingViewKeys |> Set.contains (worktreeKey, filename))

        if Map.isEmpty existing then None else Some(worktreeKey, existing))
    |> Map.ofSeq

let private createAgent targets =
    MailboxProcessor.Start(fun inbox ->
        let rec loop pending =
            async {
                let! msg = inbox.Receive()

                match msg with
                | Assign(worktreeKey, filename, sessionId, reply) ->
                    do! targets.Assign worktreeKey filename sessionId
                    reply.Reply()
                    return! loop (pending |> removePendingView worktreeKey filename)

                | FollowLastActive(worktreeKey, filename, sessionId, reply) ->
                    let hasPending =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.bind (Map.tryFind filename)
                        |> Option.isSome

                    if hasPending then
                        reply.Reply(Deferred)
                        return! loop pending
                    else
                        let! owner = targets.GetOwner worktreeKey filename
                        if owner = Some sessionId then
                            reply.Reply(Unchanged)
                        else
                            do! targets.Assign worktreeKey filename sessionId
                            reply.Reply(Assigned)

                        return! loop pending

                | BeginClaim(worktreeKey, filename, reply) ->
                    let! owner = targets.GetOwner worktreeKey filename
                    match owner with
                    | Some sessionId ->
                        reply.Reply(ExistingOwner sessionId)
                        return! loop pending
                    | None ->
                        let token = Guid.NewGuid()
                        let views =
                            pending
                            |> Map.tryFind worktreeKey
                            |> Option.defaultValue Map.empty
                            |> Map.add filename (Initial token)

                        reply.Reply(ClaimStarted token)
                        return! loop (pending |> Map.add worktreeKey views)

                | CancelClaim(worktreeKey, filename, token, reply) ->
                    let pending' =
                        match pending |> Map.tryFind worktreeKey |> Option.bind (Map.tryFind filename) with
                        | Some (Initial existingToken) when existingToken = token ->
                            pending |> removePendingView worktreeKey filename
                        | _ -> pending

                    reply.Reply()
                    return! loop pending'

                | BeginReassignment(worktreeKey, filename, reply) ->
                    let views =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Map.empty

                    match views |> Map.tryFind filename with
                    | Some _ ->
                        reply.Reply(Error "An interaction-session start is already in progress")
                        return! loop pending
                    | None ->
                        let! owner = targets.GetOwner worktreeKey filename
                        let reassignment =
                            { Token = Guid.NewGuid()
                              PreviousOwner = owner }

                        let pending' =
                            pending
                            |> Map.add worktreeKey (views |> Map.add filename (Reassignment(reassignment.Token, owner)))

                        reply.Reply(Ok reassignment)
                        return! loop pending'

                | CancelReassignment(worktreeKey, filename, token, reply) ->
                    let pending' =
                        match pending |> Map.tryFind worktreeKey |> Option.bind (Map.tryFind filename) with
                        | Some (Reassignment(existingToken, _)) when existingToken = token ->
                            pending |> removePendingView worktreeKey filename
                        | _ -> pending

                    reply.Reply()
                    return! loop pending'

                | ClaimPending(worktreeKey, token, sessionId, reply) ->
                    let pendingClaim =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Map.empty
                        |> Map.toList
                        |> List.tryPick (fun (filename, claim) ->
                            match claim with
                            | Initial existingToken when existingToken = token -> Some(filename, claim)
                            | Reassignment(existingToken, _) when existingToken = token -> Some(filename, claim)
                            | _ -> None)

                    match pendingClaim with
                    | None ->
                        reply.Reply(None)
                        return! loop pending
                    | Some(filename, claim) ->
                        let! owner = targets.GetOwner worktreeKey filename
                        let canClaim =
                            match claim with
                            | Initial _ -> owner.IsNone
                            | Reassignment(_, previousOwner) ->
                                owner = previousOwner && Some sessionId <> previousOwner

                        if canClaim then
                            do! targets.Assign worktreeKey filename sessionId
                            reply.Reply(Some filename)
                            return! loop (pending |> removePendingView worktreeKey filename)
                        else
                            reply.Reply(None)
                            return! loop pending

                | GetOwner(worktreeKey, filename, reply) ->
                    let! owner = targets.GetOwner worktreeKey filename
                    reply.Reply(owner)
                    return! loop pending

                | GetDeliveryOwner(worktreeKey, filename, reply) ->
                    let reassignmentPending =
                        pending
                        |> Map.tryFind worktreeKey
                        |> Option.bind (Map.tryFind filename)
                        |> Option.exists (function Reassignment _ -> true | Initial _ -> false)

                    if reassignmentPending then
                        reply.Reply(None)
                    else
                        let! owner = targets.GetOwner worktreeKey filename
                        reply.Reply(owner)

                    return! loop pending

                | RemoveView(worktreeKey, filename, reply) ->
                    do! targets.RemoveView worktreeKey filename
                    reply.Reply()
                    return! loop (pending |> removePendingView worktreeKey filename)

                | RemoveWorktree(worktreeKey, reply) ->
                    do! targets.RemoveWorktree worktreeKey
                    reply.Reply()
                    return! loop (pending |> Map.remove worktreeKey)

                | Prune(knownWorktrees, reply) ->
                    do! targets.Prune knownWorktrees

                    let! existingKeys =
                        pending
                        |> trackedPendingKeys knownWorktrees
                        |> Set.toList
                        |> List.map (fun ((worktreeKey, filename) as key) ->
                            async {
                                match Server.PathUtils.validateCanvasPath worktreeKey filename with
                                | Ok path when File.Exists path -> return Some key
                                | _ -> return None
                            })
                        |> Async.Parallel

                    reply.Reply()
                    return! loop (pending |> keepPending (existingKeys |> Array.choose id |> Set.ofArray))
            }

        loop Map.empty)

let private targetsFromStore (store: CanvasDocOwnership.OwnershipStore) =
    { Assign = fun worktreePath filename sessionId -> store.Assign(worktreePath, filename, sessionId)
      GetOwner = fun worktreePath filename -> store.GetOwner(worktreePath, filename)
      RemoveView = fun worktreePath filename -> store.RemoveView(worktreePath, filename)
      RemoveWorktree = fun worktreePath -> store.RemoveWorktree(worktreePath)
      Prune = fun knownWorktrees -> store.Prune(knownWorktrees) }

let private defaultTargets =
    { Assign = CanvasDocOwnership.assign
      GetOwner = CanvasDocOwnership.getOwner
      RemoveView = CanvasDocOwnership.removeView
      RemoveWorktree = CanvasDocOwnership.removeWorktree
      Prune = CanvasDocOwnership.prune }

type internal OwnershipStore internal (targets: TargetStore) =
    let agent = createAgent targets

    member _.Assign(worktreePath: string, filename: string, sessionId: string) =
        agent.PostAndAsyncReply(fun reply ->
            Assign(normalizePath worktreePath, normalizeFilename filename, sessionId, reply))

    member _.FollowLastActive(worktreePath: string, filename: string, sessionId: string) =
        agent.PostAndAsyncReply(fun reply ->
            FollowLastActive(normalizePath worktreePath, normalizeFilename filename, sessionId, reply))

    member _.BeginClaim(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            BeginClaim(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.ClaimPending(worktreePath: string, token: Guid, sessionId: string) =
        agent.PostAndReply(fun reply ->
            ClaimPending(normalizePath worktreePath, token, sessionId, reply))

    member _.CancelClaim(worktreePath: string, filename: string, token: Guid) =
        agent.PostAndAsyncReply(fun reply ->
            CancelClaim(normalizePath worktreePath, normalizeFilename filename, token, reply))

    member _.BeginReassignment(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            BeginReassignment(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.CancelReassignment(worktreePath: string, filename: string, token: Guid) =
        agent.PostAndAsyncReply(fun reply ->
            CancelReassignment(normalizePath worktreePath, normalizeFilename filename, token, reply))

    member _.GetOwner(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            GetOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.GetOwnerSync(worktreePath: string, filename: string) =
        agent.PostAndReply(fun reply ->
            GetOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.GetDeliveryOwner(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            GetDeliveryOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.GetDeliveryOwnerSync(worktreePath: string, filename: string) =
        agent.PostAndReply(fun reply ->
            GetDeliveryOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.RemoveView(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveView(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.RemoveWorktree(worktreePath: string) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveWorktree(normalizePath worktreePath, reply))

    member _.Prune(knownWorktrees: Set<string>) =
        let normalized = knownWorktrees |> Set.map normalizePath
        agent.PostAndAsyncReply(fun reply -> Prune(normalized, reply))

let internal createStore filePath =
    CanvasDocOwnership.createStore filePath
    |> targetsFromStore
    |> OwnershipStore

let private defaultStore = lazy (OwnershipStore(defaultTargets))

let load () =
    defaultStore.Force() |> ignore

let assign worktreePath filename sessionId =
    defaultStore.Value.Assign(worktreePath, filename, sessionId)

let followLastActive worktreePath filename sessionId =
    defaultStore.Value.FollowLastActive(worktreePath, filename, sessionId)

let internal beginClaim worktreePath filename =
    defaultStore.Value.BeginClaim(worktreePath, filename)

let claimPending worktreePath token sessionId =
    defaultStore.Value.ClaimPending(worktreePath, token, sessionId)

let cancelClaim worktreePath filename token =
    defaultStore.Value.CancelClaim(worktreePath, filename, token)

let internal beginReassignment worktreePath filename =
    defaultStore.Value.BeginReassignment(worktreePath, filename)

let internal cancelReassignment worktreePath filename token =
    defaultStore.Value.CancelReassignment(worktreePath, filename, token)

let getOwner worktreePath filename =
    defaultStore.Value.GetOwner(worktreePath, filename)

let internal getOwnerSync worktreePath filename =
    defaultStore.Value.GetOwnerSync(worktreePath, filename)

let internal getDeliveryOwner worktreePath filename =
    defaultStore.Value.GetDeliveryOwner(worktreePath, filename)

let internal getDeliveryOwnerSync worktreePath filename =
    defaultStore.Value.GetDeliveryOwnerSync(worktreePath, filename)

let removeView worktreePath filename =
    defaultStore.Value.RemoveView(worktreePath, filename)

let removeWorktree worktreePath =
    defaultStore.Value.RemoveWorktree(worktreePath)

let prune knownWorktrees =
    defaultStore.Value.Prune(knownWorktrees)
