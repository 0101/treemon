module CanvasAwareness

open Shared
open Navigation

type CanvasEvent =
    { Filename: string
      Timestamp: System.DateTimeOffset
      IsNew: bool }

let seedLastViewedHashes (repos: RepoModel list) (hashes: Map<string, Map<string, string>>) =
    repos
    |> List.fold (fun acc r ->
        r.Worktrees
        |> List.fold (fun acc2 wt ->
            let scopedKey = WorktreePath.value wt.Path
            let existing = acc2 |> Map.tryFind scopedKey |> Option.defaultValue Map.empty
            let withSeeded =
                wt.CanvasDocs
                |> List.fold (fun inner doc ->
                    if inner |> Map.containsKey doc.Filename then inner
                    else inner |> Map.add doc.Filename doc.ContentHash) existing
            if withSeeded = existing then acc2
            else acc2 |> Map.add scopedKey withSeeded) acc) hashes

let unviewedDocsByScopedKey (repos: RepoModel list) (lastViewedHashes: Map<string, Map<string, string>>) : Map<string, string list> =
    repos
    |> List.collect (fun r ->
        r.Worktrees
        |> List.choose (fun wt ->
            let scopedKey = WorktreePath.value wt.Path
            let viewedHashes =
                lastViewedHashes
                |> Map.tryFind scopedKey
                |> Option.defaultValue Map.empty
            let unviewed =
                wt.CanvasDocs
                |> List.filter (fun doc ->
                    match viewedHashes |> Map.tryFind doc.Filename with
                    | Some hash -> hash <> doc.ContentHash
                    | None -> true)
                |> List.map _.Filename
            if List.isEmpty unviewed then None
            else Some (scopedKey, unviewed)))
    |> Map.ofList

let canvasHashesByScopedKey (repos: RepoModel list) : Map<string, Map<string, string>> =
    repos
    |> List.collect (fun r ->
        r.Worktrees
        |> List.choose (fun wt ->
            let hashes =
                wt.CanvasDocs
                |> List.map (fun d -> d.Filename, d.ContentHash)
                |> Map.ofList
            if Map.isEmpty hashes then None
            else Some (WorktreePath.value wt.Path, hashes)))
    |> Map.ofList

let canvasEventExpiryMs = 5.0 * 60.0 * 1000.0

let detectCanvasEvents (now: System.DateTimeOffset) (previousHashes: Map<string, Map<string, string>>) (currentHashes: Map<string, Map<string, string>>) : Map<string, CanvasEvent list> =
    currentHashes
    |> Map.toList
    |> List.choose (fun (scopedKey, currentDocs) ->
        let prevDocs =
            previousHashes
            |> Map.tryFind scopedKey
            |> Option.defaultValue Map.empty
        let events =
            currentDocs
            |> Map.toList
            |> List.choose (fun (filename, hash) ->
                match prevDocs |> Map.tryFind filename with
                | None -> Some { Filename = filename; Timestamp = now; IsNew = true }
                | Some prevHash when prevHash <> hash -> Some { Filename = filename; Timestamp = now; IsNew = false }
                | _ -> None)
        if List.isEmpty events then None
        else Some (scopedKey, events))
    |> Map.ofList

let mergeCanvasEvents (existing: Map<string, CanvasEvent list>) (newEvents: Map<string, CanvasEvent list>) : Map<string, CanvasEvent list> =
    newEvents
    |> Map.fold (fun acc scopedKey evts ->
        let current = acc |> Map.tryFind scopedKey |> Option.defaultValue []
        let merged = current @ evts
        let deduped =
            merged
            |> List.groupBy _.Filename
            |> List.map (fun (_, group) -> group |> List.maxBy _.Timestamp)
        acc |> Map.add scopedKey deduped) existing

let expireCanvasEvents (now: System.DateTimeOffset) (events: Map<string, CanvasEvent list>) : Map<string, CanvasEvent list> =
    let cutoff = now.AddMilliseconds(-canvasEventExpiryMs)
    events
    |> Map.map (fun _ evts -> evts |> List.filter (fun e -> e.Timestamp > cutoff))
    |> Map.filter (fun _ evts -> not (List.isEmpty evts))

let detectChangedCanvasDocs (now: System.DateTimeOffset) (previous: Map<string, Map<string, string>>) (current: Map<string, Map<string, string>>) : (string * string) list =
    detectCanvasEvents now previous current
    |> Map.toList
    |> List.collect (fun (scopedKey, events) ->
        events |> List.map (fun e -> scopedKey, e.Filename))

let findMostRecentChangedDoc (repos: RepoModel list) (changedDocs: (string * string) list) =
    changedDocs
    |> List.choose (fun (scopedKey, filename) ->
        repos
        |> List.tryPick (fun r ->
            r.Worktrees
            |> List.tryPick (fun wt ->
                if WorktreePath.value wt.Path = scopedKey
                then wt.CanvasDocs |> List.tryFind (fun d -> d.Filename = filename)
                     |> Option.map (fun doc -> scopedKey, filename, doc.LastModified)
                else None)))
    |> List.sortByDescending (fun (_, _, lastModified) -> lastModified)
    |> List.tryHead
    |> Option.map (fun (scopedKey, filename, _) -> scopedKey, filename)
