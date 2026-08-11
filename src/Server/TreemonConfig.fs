module Server.TreemonConfig

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let private configLock = obj ()

let private configPath repoRoot = Path.Combine(repoRoot, ".treemon.json")

let private validRemoteNamePattern = Regex(@"^[a-zA-Z0-9._-]+$")
let private validBranchNamePattern = Regex(@"^[a-zA-Z0-9][a-zA-Z0-9._/-]*$")

/// One `.treemon.json` property read as a value the caller owns. `Absent` covers a missing file or a
/// missing key; `Unreadable` covers a file that failed to parse. Readers that must not report a
/// broken file as "not configured" need that distinction.
type internal PropertyRead =
    | Absent
    | Unreadable
    | Present of JsonElement

/// Cloned before the `JsonDocument` is disposed, so the returned element is never borrowed from a
/// disposed document.
let private readProperty (path: string) (propertyName: string) : PropertyRead =
    if not (File.Exists(path)) then
        Absent
    else
        try
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)

            match doc.RootElement.TryGetProperty(propertyName) with
            | true, elem -> Present(elem.Clone())
            | _ -> Absent
        with ex ->
            Log.log "TreemonConfig" $"Failed to read {propertyName} from {path}: {ex.Message}"
            Unreadable

let private withJsonProperty (path: string) (propertyName: string) (onFound: JsonElement -> 'a) (defaultValue: 'a) : 'a =
    match readProperty path propertyName with
    | Present elem -> onFound elem
    | Absent
    | Unreadable -> defaultValue

let private readStringArrayCore (path: string) (propertyName: string) : string list =
    withJsonProperty path propertyName (fun elem ->
        if elem.ValueKind = JsonValueKind.Array then
            elem.EnumerateArray()
            |> Seq.choose (fun v ->
                if v.ValueKind = JsonValueKind.String then Some(v.GetString())
                else None)
            |> Seq.toList
        else []) []

let private writeStringArrayCore (path: string) (propertyName: string) (values: string list) : unit =
    let root =
        if File.Exists(path) then
            try
                File.ReadAllText(path) |> JsonNode.Parse :?> JsonObject
            with ex ->
                Log.log "TreemonConfig" $"Failed to parse existing {path}, overwriting: {ex.Message}"
                JsonObject()
        else
            JsonObject()

    let valuesArray = JsonArray(values |> List.map (fun s -> JsonValue.Create(s) :> JsonNode) |> List.toArray)
    root[propertyName] <- valuesArray

    let options = JsonSerializerOptions(WriteIndented = true)
    File.WriteAllText(path, root.ToJsonString(options))

let private readBranchList propertyName repoRoot =
    lock configLock (fun () -> readStringArrayCore (configPath repoRoot) propertyName)

let private setBranchList propertyName repoRoot branches =
    lock configLock (fun () -> writeStringArrayCore (configPath repoRoot) propertyName branches)

let private readBranchSet propertyName repoRoot =
    repoRoot
    |> Option.map (readBranchList propertyName)
    |> Option.defaultValue []
    |> Set.ofList

let private modifyBranchList propertyName repoRoot modify =
    let path = configPath repoRoot
    lock configLock (fun () ->
        readStringArrayCore path propertyName
        |> modify
        |> writeStringArrayCore path propertyName)

let readArchivedBranches = readBranchList "archivedBranches"

let setArchivedBranches = setBranchList "archivedBranches"

let readArchivedBranchSet = readBranchSet "archivedBranches"

let modifyArchivedBranches = modifyBranchList "archivedBranches"

let readAutoSyncBranches = readBranchList "autoSyncBranches"

let setAutoSyncBranches = setBranchList "autoSyncBranches"

let readAutoSyncBranchSet = readBranchSet "autoSyncBranches"

let modifyAutoSyncBranches = modifyBranchList "autoSyncBranches"

let private readStringConfig (repoRoot: string) (propertyName: string) : string option =
    lock configLock (fun () ->
        withJsonProperty (configPath repoRoot) propertyName (fun elem ->
            if elem.ValueKind = JsonValueKind.String then
                let value = elem.GetString()
                if System.String.IsNullOrWhiteSpace(value) then None
                else Some value
            else None) None)

let readUpstreamRemote (repoRoot: string) : string option =
    readStringConfig repoRoot "upstreamRemote"
    |> Option.bind (fun value ->
        if validRemoteNamePattern.IsMatch(value) then Some value
        else
            Log.log "TreemonConfig" $"Rejected invalid upstreamRemote value: '{value}'"
            None)

let readBaseBranch (repoRoot: string) : string =
    readStringConfig repoRoot "baseBranch"
    |> Option.bind (fun value ->
        if validBranchNamePattern.IsMatch(value) then Some value
        else
            Log.log "TreemonConfig" $"Rejected invalid baseBranch value: '{value}'"
            None)
    |> Option.defaultValue "main"

/// Reads the raw `diffCategories` value. `DiffCategories` owns the schema and validates the shape;
/// this module stays the only reader of `.treemon.json`.
let internal readDiffCategories (repoRoot: string) : PropertyRead =
    lock configLock (fun () -> readProperty (configPath repoRoot) "diffCategories")
