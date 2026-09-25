module Server.DeletedWorktreeStore

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let filePathForPort port =
    Path.Combine(GlobalConfig.globalConfigDir (), $"deleted-worktrees-{port}.json")

let private storeLock = obj ()

let private canonicalPath path =
    if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
        Error "Deleted-worktree path must be absolute"
    else
        try Ok(PathUtils.normalizePath path)
        with :? ArgumentException -> Error "Deleted-worktree path is invalid"

let private readUnlocked path : Result<Set<string>, string> =
    if not (File.Exists path) then Ok Set.empty
    else
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement

            match root.ValueKind with
            | JsonValueKind.Object ->
                match root.TryGetProperty "paths" with
                | true, entries when entries.ValueKind = JsonValueKind.Array ->
                    let values = entries.EnumerateArray() |> Seq.toList

                    if values |> List.exists (fun value -> value.ValueKind <> JsonValueKind.String) then
                        Error "Deleted-worktree state contains a non-string path"
                    else
                        values
                        |> List.map _.GetString()
                        |> List.fold
                            (fun state value ->
                                state
                                |> Result.bind (fun paths ->
                                    canonicalPath value
                                    |> Result.map (fun canonical -> Set.add canonical paths)))
                            (Ok Set.empty)
                | _ -> Error "Deleted-worktree state has no paths array"
            | _ -> Error "Deleted-worktree state must be a JSON object"
        with ex ->
            Log.log "DeletedWorktreeStore" $"Failed to read state: {ex}"
            Error "Deleted-worktree state could not be read; check the server log"

let readAtPath path =
    lock storeLock (fun () -> readUnlocked path)

let private persist (path: string) (paths: Set<string>) =
    if Set.isEmpty paths then
        try
            if File.Exists path then File.Delete path
            Ok ()
        with ex -> Error $"Failed to remove deleted-worktree state: {ex.Message}"
    else
        let values =
            paths
            |> Set.toArray
            |> Array.map (fun value -> JsonValue.Create(value) :> JsonNode)

        GlobalConfig.updateConfigAtPath path [ "paths", JsonArray(values) :> JsonNode ]

let private modifyAtPath path change =
    lock storeLock (fun () ->
        readUnlocked path
        |> Result.bind (fun existing ->
            change existing
            |> Result.bind (fun updated ->
                if updated = existing then Ok ()
                else persist path updated)))

let recordAtPath path worktreePath =
    canonicalPath worktreePath
    |> Result.bind (fun canonical ->
        modifyAtPath path (Set.add canonical >> Ok))

let forgetAtPath path worktreePath =
    canonicalPath worktreePath
    |> Result.bind (fun canonical ->
        if Directory.Exists canonical || File.Exists canonical then
            Error "Worktree path still exists on disk"
        else
            modifyAtPath path (fun paths ->
                if Set.contains canonical paths then
                    Ok(Set.remove canonical paths)
                else
                    Error "Worktree path is not tombstoned"))
