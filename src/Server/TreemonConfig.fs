module Server.TreemonConfig

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let private configLock = obj ()

let private configPath repoRoot = Path.Combine(repoRoot, ".treemon.json")

let private readBranchesCore (path: string) : string list =
    if not (File.Exists(path)) then
        []
    else
        try
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty("archivedBranches") with
            | true, elem when elem.ValueKind = JsonValueKind.Array ->
                elem.EnumerateArray()
                |> Seq.choose (fun v ->
                    if v.ValueKind = JsonValueKind.String then Some(v.GetString())
                    else None)
                |> Seq.toList
            | _ -> []
        with ex ->
            Log.log "TreemonConfig" $"Failed to read archivedBranches from {path}: {ex.Message}"
            []

let private writeBranchesCore (path: string) (branches: string list) : unit =
    let root =
        if File.Exists(path) then
            try
                File.ReadAllText(path) |> JsonNode.Parse :?> JsonObject
            with ex ->
                Log.log "TreemonConfig" $"Failed to parse existing {path}, overwriting: {ex.Message}"
                JsonObject()
        else
            JsonObject()

    let branchArray = JsonArray(branches |> List.map (fun s -> JsonValue.Create(s) :> JsonNode) |> List.toArray)
    root["archivedBranches"] <- branchArray

    let options = JsonSerializerOptions(WriteIndented = true)
    File.WriteAllText(path, root.ToJsonString(options))

let readArchivedBranches (repoRoot: string) : string list =
    lock configLock (fun () -> configPath repoRoot |> readBranchesCore)

let setArchivedBranches (repoRoot: string) (branches: string list) : unit =
    lock configLock (fun () -> configPath repoRoot |> writeBranchesCore <| branches)

let readArchivedBranchSet (repoRoot: string option) : Set<string> =
    repoRoot
    |> Option.map readArchivedBranches
    |> Option.defaultValue []
    |> Set.ofList

let modifyArchivedBranches (repoRoot: string) (modify: string list -> string list) : unit =
    let path = configPath repoRoot
    lock configLock (fun () ->
        readBranchesCore path
        |> modify
        |> writeBranchesCore path)
