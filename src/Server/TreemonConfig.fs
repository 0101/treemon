module Server.TreemonConfig

open System
open System.IO
open System.Text.Json

let readArchivedBranches (repoRoot: string) : string list =
    let configPath = Path.Combine(repoRoot, ".treemon.json")

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
            []

let setArchivedBranches (repoRoot: string) (branches: string list) : unit =
    let configPath = Path.Combine(repoRoot, ".treemon.json")

    let existingFields =
        if File.Exists(configPath) then
            try
                let json = File.ReadAllText(configPath)
                use doc = JsonDocument.Parse(json)

                doc.RootElement.EnumerateObject()
                |> Seq.filter (fun prop -> prop.Name <> "archivedBranches")
                |> Seq.map (fun prop -> prop.Name, prop.Value.Clone())
                |> Seq.toList
            with ex ->
                Log.log "TreemonConfig" $"Failed to parse existing {configPath}, overwriting: {ex.Message}"
                []
        else
            []

    let options = JsonWriterOptions(Indented = true)

    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, options)
    writer.WriteStartObject()

    existingFields
    |> List.iter (fun (name, value) ->
        writer.WritePropertyName(name)
        value.WriteTo(writer))

    writer.WritePropertyName("archivedBranches")
    writer.WriteStartArray()
    branches |> List.iter writer.WriteStringValue
    writer.WriteEndArray()

    writer.WriteEndObject()
    writer.Flush()

    let newJson = System.Text.Encoding.UTF8.GetString(stream.ToArray())
    let tempPath = configPath + ".tmp"

    try
        File.WriteAllText(tempPath, newJson)
        File.Move(tempPath, configPath, overwrite = true)
    with ex ->
        Log.log "TreemonConfig" $"Failed to write {configPath}: {ex.Message}"
        if File.Exists(tempPath) then
            try File.Delete(tempPath) with _ -> ()
        reraise ()
