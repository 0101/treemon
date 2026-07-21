module Server.GlobalConfig

open System
open System.IO
open System.Text.RegularExpressions
open Shared
open Shared.PathUtils

/// Directory holding the machine-level Treemon config (`config.json`), normally `~/.treemon`.
/// The `TREEMON_CONFIG_DIR` override exists for test isolation: on Windows
/// `Environment.GetFolderPath(UserProfile)` ignores the USERPROFILE/HOME env vars, so an
/// in-process test can only redirect the config dir via this explicit override.
let internal globalConfigDir () =
    Environment.GetEnvironmentVariable("TREEMON_CONFIG_DIR")
    |> Option.ofObj
    |> Option.filter (fun d -> d <> "")
    |> Option.defaultWith (fun () ->
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".treemon"))

let private globalConfigPath () =
    Path.Combine(globalConfigDir (), "config.json")

let private withConfigDocument (defaultValue: 'a) (f: System.Text.Json.JsonElement -> 'a) : 'a =
    let path = globalConfigPath ()
    if not (File.Exists path) then defaultValue
    else
        try
            let json = File.ReadAllText path
            use doc = System.Text.Json.JsonDocument.Parse json
            f doc.RootElement
        with ex ->
            Log.log "Config" $"Failed to read config: {ex.Message}"
            defaultValue

let private readGlobalConfig () =
    withConfigDocument Map.empty (fun root ->
        root.EnumerateObject()
        |> Seq.choose (fun prop ->
            if prop.Value.ValueKind = System.Text.Json.JsonValueKind.String
            then Some (prop.Name, prop.Value.GetString())
            else None)
        |> Map.ofSeq)

/// Reads a JSON string array from the machine-level config by key, returning `[]` when the key is
/// absent or not an array. Shared by the config readers that each pull a list of strings and then
/// apply their own post-processing (element wrapping, trimming, filtering, container choice).
let private readStringArray (key: string) : string list =
    withConfigDocument [] (fun root ->
        match root.TryGetProperty(key) with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Array ->
            prop.EnumerateArray()
            |> Seq.choose (fun el ->
                if el.ValueKind = System.Text.Json.JsonValueKind.String then Some (el.GetString())
                else None)
            |> Seq.toList
        | _ -> [])

let internal readCollapsedRepos () : Set<RepoId> =
    readStringArray "collapsedRepos" |> List.map RepoId |> Set.ofList

/// Reads `ignoreWorktreePatterns` from the machine-level config — the regexes for worktrees the
/// dashboard should hide. Lives here so all global-config reads share the one
/// `TREEMON_CONFIG_DIR`-aware path.
let readIgnoreWorktreePatterns () : string list =
    readStringArray "ignoreWorktreePatterns"

/// Reads the machine-level `worktreeSkills` — the skills offered in the create-worktree modal's
/// radio group. Entries are trimmed and blanks dropped. Absent or empty yields `[]`, which the modal renders
/// as "None only" plus an onboarding hint pointing here. Order is preserved; the first entry is
/// the modal's default selection.
let readWorktreeSkills () : string list =
    readStringArray "worktreeSkills"
    |> List.map _.Trim()
    |> List.filter (fun s -> s <> "")

let buildIgnorePredicate (patterns: string list) : string -> bool =
    let regexes =
        patterns
        |> List.filter (not << String.IsNullOrWhiteSpace)
        |> List.choose (fun pattern ->
            try Some (Regex($"^(?:{pattern})$", RegexOptions.Compiled))
            with :? ArgumentException ->
                Log.log "Config" $"Invalid ignore worktree pattern: '{pattern}'"
                None)
    match regexes with
    | [] -> fun _ -> false
    | _ -> fun value -> regexes |> List.exists _.IsMatch(value)

/// Serializes every write to the machine-level `config.json`. All global-config writers
/// (collapsedRepos, canvas state, lastViewedHashes, worktreeRoots) funnel through
/// `updateConfigAtPath`, so this lock makes the server the single serialized writer and stops
/// concurrent read-modify-write cycles from clobbering each other's keys.
let private globalConfigLock = obj ()

let private tryParseJsonObject (text: string) : System.Text.Json.Nodes.JsonObject option =
    try
        match System.Text.Json.Nodes.JsonNode.Parse(text) with
        | :? System.Text.Json.Nodes.JsonObject as root -> Some root
        | _ -> None
    with _ -> None

/// Read-modify-write a JSON config file safely. Serialized by an in-process lock,
/// written atomically (temp file in the same directory, then replace), and never
/// discarding existing data: an unparseable file is backed up to a timestamped sibling
/// rather than overwritten. Updates are described as (key, node) pairs, applied as the only
/// mutation of the JSON tree. Takes the path so it can be unit-tested against a temp file.
let internal updateConfigAtPath (configPath: string) (updates: (string * System.Text.Json.Nodes.JsonNode) list) : Result<unit, string> =
    lock globalConfigLock (fun () ->
        try
            let dir = Path.GetDirectoryName(configPath)
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

            let root =
                if not (File.Exists(configPath)) then
                    System.Text.Json.Nodes.JsonObject()
                else
                    match File.ReadAllText(configPath) |> tryParseJsonObject with
                    | Some existing -> existing
                    | None ->
                        let timestamp = DateTime.Now.ToString("yyyyMMddHHmmss")
                        let backupPath = $"{configPath}.corrupt-{timestamp}"
                        Log.log "Config" $"Config at {configPath} is unparseable; backing up to {backupPath} and starting fresh"
                        File.Copy(configPath, backupPath, overwrite = true)
                        System.Text.Json.Nodes.JsonObject()

            updates |> List.iter (fun (key, value) -> root[key] <- value)

            let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
            let tempPath = configPath + ".tmp"
            File.WriteAllText(tempPath, root.ToJsonString(options))
            File.Move(tempPath, configPath, overwrite = true)
            Ok()
        with ex ->
            Error ex.Message)

let private updateGlobalConfig (description: string) (updates: (string * System.Text.Json.Nodes.JsonNode) list) =
    match updateConfigAtPath (globalConfigPath ()) updates with
    | Ok() -> ()
    | Error msg -> Log.log "Config" $"Failed to save {description}: {msg}"

let internal writeCollapsedRepos (repos: RepoId list) =
    let repoArray =
        System.Text.Json.Nodes.JsonArray(repos |> List.map (fun (RepoId s) -> System.Text.Json.Nodes.JsonValue.Create(s) :> System.Text.Json.Nodes.JsonNode) |> List.toArray)
    updateGlobalConfig "collapsed repos" [ "collapsedRepos", repoArray :> System.Text.Json.Nodes.JsonNode ]

/// Reads the machine-level set of watched worktree roots (`worktreeRoots` in `config.json`),
/// distinguishing a MISSING key (`None`) from a present-but-empty list (`Some []`). The startup
/// resolver depends on that distinction: an explicit `worktreeRoots:[]` means the user curated
/// every root away, so it must NOT be treated like a fresh install and repopulated from CLI args
/// or a stale orphan `roots.json`. A malformed (non-array) value is reported as `None` — absent —
/// matching the original lenient behavior. `internal` so the resolver (`Program.fs`) shares it.
let internal tryReadWorktreeRootsConfig () : string list option =
    withConfigDocument None (fun root ->
        match root.TryGetProperty("worktreeRoots") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Array ->
            prop.EnumerateArray()
            |> Seq.choose (fun el ->
                if el.ValueKind = System.Text.Json.JsonValueKind.String then Some(el.GetString())
                else None)
            |> List.ofSeq
            |> Some
        | _ -> None)

/// Flattens `tryReadWorktreeRootsConfig` to a plain list (missing key -> `[]`) for callers that
/// don't need the missing-vs-empty distinction: the `getRoots` endpoint and the add/remove
/// read-modify-write. `internal` so the startup resolver and the endpoint can share the reader.
let internal readWorktreeRootsConfig () : string list =
    tryReadWorktreeRootsConfig () |> Option.defaultValue []

/// Persists the watched worktree roots through the locked, atomic single-writer path
/// (`updateConfigAtPath`), leaving every other global-config key untouched. Returns the write
/// outcome so the `addRoot`/`removeRoot` endpoints can surface a persistence failure to the CLI
/// instead of reporting a false success. `internal` so the startup resolver can also write
/// through this one helper.
let internal writeWorktreeRoots (roots: string list) : Result<unit, string> =
    let rootArray =
        System.Text.Json.Nodes.JsonArray(
            roots
            |> List.map (fun r -> System.Text.Json.Nodes.JsonValue.Create(r) :> System.Text.Json.Nodes.JsonNode)
            |> List.toArray)
    updateConfigAtPath (globalConfigPath ()) [ "worktreeRoots", rootArray :> System.Text.Json.Nodes.JsonNode ]

/// Canonical comparison form for a worktree root: absolute path with trailing separators
/// trimmed. Total (never throws) so it is safe to fold over already-stored roots — a malformed
/// stored entry falls back to its raw value rather than aborting the whole add/remove.
let private canonicalRoot (path: string) =
    try Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    with _ -> path

/// Normalizes a caller-supplied root path (absolute, trailing separators trimmed), surfacing a
/// readable error for blank or malformed input so the CLI can report it.
let private tryNormalizeRoot (path: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace path then Error "Path is empty."
    else
        try Ok(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        with ex -> Error $"Invalid path '{path}': {ex.Message}"

/// Adds a worktree root to the global config (restart-to-apply). Normalizes and verifies the
/// path is an existing directory, then read-modify-writes the roots list via the locked
/// single-writer helpers, surfacing a persistence failure rather than a false success. Adding an
/// already-watched path is a no-op success. add/remove are driven by the (serialized)
/// `tm add`/`tm remove` CLI, so the read-then-write is not contended in practice; the write
/// itself is serialized by `globalConfigLock`.
let internal addRootToConfig (path: string) : Result<unit, string> =
    tryNormalizeRoot path
    |> Result.bind (fun normalized ->
        if not (Directory.Exists normalized) then
            Error $"Path does not exist or is not a directory: {normalized}"
        else
            let existing = readWorktreeRootsConfig ()
            if existing |> List.exists (fun r -> pathEquals (canonicalRoot r) normalized) then
                Ok()
            else
                writeWorktreeRoots (existing @ [ normalized ]))

/// Removes a worktree root from the global config (restart-to-apply). Does not require the path
/// to still exist on disk (a deleted root is removable); reports an error when the path is not
/// currently watched, and surfaces a persistence failure instead of a false success.
let internal removeRootFromConfig (path: string) : Result<unit, string> =
    tryNormalizeRoot path
    |> Result.bind (fun normalized ->
        let existing = readWorktreeRootsConfig ()
        let remaining = existing |> List.filter (fun r -> not (pathEquals (canonicalRoot r) normalized))
        if List.length remaining = List.length existing then
            Error $"Not a watched root: {normalized}"
        else
            writeWorktreeRoots remaining)

let private readBoolProperty (name: string) : bool =
    withConfigDocument false (fun root ->
        match root.TryGetProperty(name) with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.True -> true
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.False -> false
        | _ -> false)

let internal readCanvasPaneOpen () : bool = readBoolProperty "canvasPaneOpen"

let internal writeCanvasPaneOpen (isOpen: bool) =
    updateGlobalConfig "canvas pane open state" [ "canvasPaneOpen", System.Text.Json.Nodes.JsonValue.Create(isOpen) :> System.Text.Json.Nodes.JsonNode ]

let internal readOverviewPanelOpen () : bool = readBoolProperty "overviewPanelOpen"

let internal writeOverviewPanelOpen (isOpen: bool) =
    updateGlobalConfig "overview panel open state" [ "overviewPanelOpen", System.Text.Json.Nodes.JsonValue.Create(isOpen) :> System.Text.Json.Nodes.JsonNode ]

let internal readCanvasPosition () : CanvasPosition =
    withConfigDocument CanvasPosition.Right (fun root ->
        let found, prop = root.TryGetProperty("canvasPosition")
        let s = if found && prop.ValueKind = System.Text.Json.JsonValueKind.String then prop.GetString() else ""
        match found, s with
        | true, "left" -> CanvasPosition.Left
        | true, "right" -> CanvasPosition.Right
        | true, "top" -> CanvasPosition.Top
        | true, "bottom" -> CanvasPosition.Bottom
        | _ -> CanvasPosition.Right)

let internal writeCanvasPosition (position: CanvasPosition) =
    let value =
        match position with
        | CanvasPosition.Left -> "left"
        | CanvasPosition.Right -> "right"
        | CanvasPosition.Top -> "top"
        | CanvasPosition.Bottom -> "bottom"
    updateGlobalConfig "canvas position" [ "canvasPosition", System.Text.Json.Nodes.JsonValue.Create(value) :> System.Text.Json.Nodes.JsonNode ]

let internal readCanvasSize () : CanvasSize =
    withConfigDocument CanvasSize.Ratio1To1 (fun root ->
        let found, prop = root.TryGetProperty("canvasSize")
        let s = if found && prop.ValueKind = System.Text.Json.JsonValueKind.String then prop.GetString() else ""
        match found, s with
        | true, "2to1" -> CanvasSize.Ratio2To1
        | _ -> CanvasSize.Ratio1To1)

let internal writeCanvasSize (size: CanvasSize) =
    let value =
        match size with
        | CanvasSize.Ratio1To1 -> "1to1"
        | CanvasSize.Ratio2To1 -> "2to1"
    updateGlobalConfig "canvas size" [ "canvasSize", System.Text.Json.Nodes.JsonValue.Create(value) :> System.Text.Json.Nodes.JsonNode ]

/// Machine-level config for the canvas Share backend (the `canvasShare` section of `config.json`):
/// which PRIVATE blob container published docs land in, and the default per-doc SAS expiry. The
/// Azure credential is deliberately NOT here — it is the `AZURE_STORAGE_CONNECTION_STRING` secret,
/// read by `CanvasShare` straight from the environment, so no account key is ever written to the
/// JSON file (spec docs/spec/canvas-sharing.md, Configuration).
type CanvasShareConfig =
    { Container: string
      DefaultExpiryDays: int }

/// Defaults for a missing `canvasShare` section or field: a conventional private-container name and
/// the spec's 90-day SAS lifetime. With these, setting only `AZURE_STORAGE_CONNECTION_STRING` is
/// enough to share — matching the spec's framing that the connection string is the one thing an
/// operator must supply.
let defaultCanvasShareConfig = { Container = "canvas-shared"; DefaultExpiryDays = 90 }

/// Upper bound (10 years) on a configured `defaultExpiryDays`. A larger — or non-positive — value is
/// treated as absent and falls back to the default, keeping the SAS expiry *bounded* (spec Decision
/// #3) and guaranteeing `DateTimeOffset.UtcNow.AddDays(DefaultExpiryDays)` at publish can never
/// overflow `DateTimeOffset` (year 9999 is ~2.9M days out) and orphan an already-uploaded blob.
let internal maxCanvasShareExpiryDays = 3650

/// Reads the `canvasShare` config section, falling back to `defaultCanvasShareConfig` for a missing
/// section or field. A blank `container`, or a `defaultExpiryDays` outside `1 .. maxCanvasShareExpiryDays`,
/// is treated as absent (a non-positive expiry would mint an already-dead link; an unbounded one would
/// overflow `AddDays` at publish and orphan the blob), so a partial or typo'd section still yields a
/// working config rather than a broken one.
let internal readCanvasShareConfig () : CanvasShareConfig =
    withConfigDocument defaultCanvasShareConfig (fun root ->
        match root.TryGetProperty("canvasShare") with
        | true, section when section.ValueKind = System.Text.Json.JsonValueKind.Object ->
            let container =
                match section.TryGetProperty("container") with
                | true, c when c.ValueKind = System.Text.Json.JsonValueKind.String && c.GetString().Trim() <> "" ->
                    c.GetString().Trim()
                | _ -> defaultCanvasShareConfig.Container
            let expiryDays =
                match section.TryGetProperty("defaultExpiryDays") with
                | true, e when e.ValueKind = System.Text.Json.JsonValueKind.Number ->
                    match e.TryGetInt32() with
                    | true, n when n > 0 && n <= maxCanvasShareExpiryDays -> n
                    | _ -> defaultCanvasShareConfig.DefaultExpiryDays
                | _ -> defaultCanvasShareConfig.DefaultExpiryDays
            { Container = container; DefaultExpiryDays = expiryDays }
        | _ -> defaultCanvasShareConfig)

let internal readLastViewedHashes () : Map<string, Map<string, string>> =
    withConfigDocument Map.empty (fun root ->
        match root.TryGetProperty("lastViewedHashes") with
        | true, prop when prop.ValueKind = System.Text.Json.JsonValueKind.Object ->
            prop.EnumerateObject()
            |> Seq.choose (fun worktreeProp ->
                if worktreeProp.Value.ValueKind = System.Text.Json.JsonValueKind.Object then
                    let fileHashes =
                        worktreeProp.Value.EnumerateObject()
                        |> Seq.choose (fun fileProp ->
                            if fileProp.Value.ValueKind = System.Text.Json.JsonValueKind.String
                            then Some (fileProp.Name, fileProp.Value.GetString())
                            else None)
                        |> Map.ofSeq
                    Some (worktreeProp.Name, fileHashes)
                else None)
            |> Map.ofSeq
        | _ -> Map.empty)

let internal writeLastViewedHashes (hashes: Map<string, Map<string, string>>) =
    let outerObj = System.Text.Json.Nodes.JsonObject()
    hashes |> Map.iter (fun worktreePath fileHashes ->
        let innerObj = System.Text.Json.Nodes.JsonObject()
        fileHashes |> Map.iter (fun filename hash ->
            innerObj[filename] <- System.Text.Json.Nodes.JsonValue.Create(hash))
        outerObj[worktreePath] <- innerObj)
    updateGlobalConfig "last viewed hashes" [ "lastViewedHashes", outerObj :> System.Text.Json.Nodes.JsonNode ]

let internal getEditorConfig () =
    let config = readGlobalConfig ()
    let command = config |> Map.tryFind "editor" |> Option.defaultValue "code"
    let name =
        match config |> Map.tryFind "editorName", command with
        | Some n, _ -> n
        | None, "code" -> "VS Code"
        | None, cmd -> cmd
    command, name
