module Server.TreemonConfig

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let private configLock = obj ()

let private configPath repoRoot = Path.Combine(repoRoot, ".treemon.json")

let private validRemoteNamePattern = Regex(@"^[a-zA-Z0-9._-]+$")

let private withJsonProperty (path: string) (propertyName: string) (onFound: JsonElement -> 'a) (defaultValue: 'a) : 'a =
    if not (File.Exists(path)) then
        defaultValue
    else
        try
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty(propertyName) with
            | true, elem -> onFound elem
            | _ -> defaultValue
        with ex ->
            Log.log "TreemonConfig" $"Failed to read {propertyName} from {path}: {ex.Message}"
            defaultValue

let private readBranchesCore (path: string) : string list =
    withJsonProperty path "archivedBranches" (fun elem ->
        if elem.ValueKind = JsonValueKind.Array then
            elem.EnumerateArray()
            |> Seq.choose (fun v ->
                if v.ValueKind = JsonValueKind.String then Some(v.GetString())
                else None)
            |> Seq.toList
        else []) []

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

let private readStringConfig (repoRoot: string) (propertyName: string) : string option =
    lock configLock (fun () ->
        withJsonProperty (configPath repoRoot) propertyName (fun elem ->
            if elem.ValueKind = JsonValueKind.String then
                let value = elem.GetString()
                if System.String.IsNullOrWhiteSpace(value) then None
                else Some value
            else None) None)

let private allowedTestRunners =
    Set.ofList [ "dotnet"; "npm"; "npx"; "yarn"; "pnpm"; "pytest"; "python"; "cargo"; "go"; "make"; "gradle"; "gradlew"; "mvn"; "mvnw" ]

let private extractExecutable (command: string) =
    let trimmed = command.Trim()
    if trimmed.StartsWith('"') then
        match trimmed.IndexOf('"', 1) with
        | -1 -> trimmed
        | i -> trimmed[1..i-1]
    else
        match trimmed.IndexOf(' ') with
        | -1 -> trimmed
        | i -> trimmed[..i-1]

let private isAllowedTestCommand (command: string) =
    let exe = extractExecutable command |> Path.GetFileNameWithoutExtension
    allowedTestRunners.Contains(exe.ToLowerInvariant())

let readUpstreamRemote (repoRoot: string) : string option =
    readStringConfig repoRoot "upstreamRemote"
    |> Option.bind (fun value ->
        if validRemoteNamePattern.IsMatch(value) then Some value
        else
            Log.log "TreemonConfig" $"Rejected invalid upstreamRemote value: '{value}'"
            None)

let readTestCommand (repoRoot: string) : string option =
    readStringConfig repoRoot "testCommand"
    |> Option.bind (fun cmd ->
        if isAllowedTestCommand cmd then Some cmd
        else
            Log.log "TreemonConfig" $"Rejected testCommand '{cmd}': executable not in allowlist"
            None)
