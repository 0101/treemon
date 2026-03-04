module Server.TreemonConfig

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let private configLock = obj ()

let readArchivedBranches (repoRoot: string) : string list =
    let configPath = Path.Combine(repoRoot, ".treemon.json")

    lock configLock (fun () ->
        if not (File.Exists(configPath)) then
            []
        else
            try
                let json = File.ReadAllText(configPath)
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
                Log.log "TreemonConfig" $"Failed to read archivedBranches from {configPath}: {ex.Message}"
                [])

let setArchivedBranches (repoRoot: string) (branches: string list) : unit =
    let configPath = Path.Combine(repoRoot, ".treemon.json")

    lock configLock (fun () ->
        let root =
            if File.Exists(configPath) then
                try
                    File.ReadAllText(configPath) |> JsonNode.Parse :?> JsonObject
                with ex ->
                    Log.log "TreemonConfig" $"Failed to parse existing {configPath}, overwriting: {ex.Message}"
                    JsonObject()
            else
                JsonObject()

        let branchArray = JsonArray(branches |> List.map (fun s -> JsonValue.Create(s) :> JsonNode) |> List.toArray)
        root["archivedBranches"] <- branchArray

        let options = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(configPath, root.ToJsonString(options)))

let modifyArchivedBranches (repoRoot: string) (modify: string list -> string list) : unit =
    let configPath = Path.Combine(repoRoot, ".treemon.json")

    lock configLock (fun () ->
        let root, existing =
            if File.Exists(configPath) then
                try
                    let json = File.ReadAllText(configPath)
                    let root = json |> JsonNode.Parse :?> JsonObject

                    let branches =
                        match root.TryGetPropertyValue("archivedBranches") with
                        | true, node when node <> null && node.GetValueKind() = JsonValueKind.Array ->
                            node.AsArray()
                            |> Seq.choose (fun v ->
                                if v <> null && v.GetValueKind() = JsonValueKind.String then Some(v.GetValue<string>())
                                else None)
                            |> Seq.toList
                        | _ -> []

                    root, branches
                with ex ->
                    Log.log "TreemonConfig" $"Failed to parse existing {configPath}, overwriting: {ex.Message}"
                    JsonObject(), []
            else
                JsonObject(), []

        let updated = modify existing
        let branchArray = JsonArray(updated |> List.map (fun s -> JsonValue.Create(s) :> JsonNode) |> List.toArray)
        root["archivedBranches"] <- branchArray

        let options = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(configPath, root.ToJsonString(options)))
