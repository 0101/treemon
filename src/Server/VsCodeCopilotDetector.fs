module Server.VsCodeCopilotDetector

open System
open System.IO
open System.Text.Json
open Shared

let private vsCodeWorkspaceStorageDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Code",
        "User",
        "workspaceStorage"
    )

type private WorkspaceIndex =
    { PathToChatSessions: Map<string, string>
      BuiltAt: DateTimeOffset }

let private workspaceIndex =
    ref
        { PathToChatSessions = Map.empty
          BuiltAt = DateTimeOffset.MinValue }

let private tryDecodeLocalPath (folderUri: string) =
    try
        let decoded = Uri.UnescapeDataString(folderUri)
        Some(Uri(decoded).LocalPath)
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to decode folder URI '{folderUri}': {ex.Message}"
        None

let private normalizePath (path: string) =
    Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let private tryReadFolderUri (workspaceJson: string) =
    try
        let json = File.ReadAllText(workspaceJson)
        use doc = JsonDocument.Parse(json)
        match doc.RootElement.TryGetProperty("folder") with
        | true, p -> Some(p.GetString())
        | _ -> None
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to parse {workspaceJson}: {ex.Message}"
        None

let private buildWorkspaceIndex () =
    let index =
        try
            if Directory.Exists(vsCodeWorkspaceStorageDir) then
                Directory.GetDirectories(vsCodeWorkspaceStorageDir)
                |> Array.choose (fun hashDir ->
                    let workspaceJson = Path.Combine(hashDir, "workspace.json")
                    let chatSessionsDir = Path.Combine(hashDir, "chatSessions")

                    if File.Exists(workspaceJson) && Directory.Exists(chatSessionsDir) then
                        tryReadFolderUri workspaceJson
                        |> Option.bind tryDecodeLocalPath
                        |> Option.map (fun localPath -> normalizePath localPath, chatSessionsDir)
                    else
                        None)
                |> Map.ofArray
            else
                Map.empty
        with ex ->
            Log.log "VsCodeCopilot" $"Failed to scan workspaceStorage: {ex.Message}"
            Map.empty

    Log.log "VsCodeCopilot" $"Index built: {index.Count} workspace(s) with chatSessions mapped"
    { PathToChatSessions = index; BuiltAt = DateTimeOffset.UtcNow }

let private refreshIndex () =
    FileUtils.refreshIfStale (TimeSpan.FromSeconds(60.0)) workspaceIndex _.BuiltAt buildWorkspaceIndex

let private getChatSessionsDir (worktreePath: string) =
    let index = refreshIndex ()
    let normalized = normalizePath worktreePath
    Map.tryFind normalized index.PathToChatSessions

let private findMostRecentSessionFile (chatSessionsDir: string) =
    try
        if not (Directory.Exists(chatSessionsDir)) then
            Log.log "VsCodeCopilot" $"chatSessions dir does not exist: {chatSessionsDir}"
            None
        else
            Directory.GetFiles(chatSessionsDir, "*.jsonl")
                |> Array.choose (fun path ->
                    try Some(FileInfo(path))
                    with ex ->
                        Log.log "VsCodeCopilot" $"Failed to get FileInfo for {path}: {ex.Message}"
                        None)
                |> Array.sortByDescending _.LastWriteTimeUtc
                |> Array.tryHead
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to scan chatSessions dir: {ex.Message}"
        None

// VS Code JSONL mutation log format:
//   kind=0: Full session snapshot (.v = ISerializableChatData3)
//   kind=1: Set value at path (.k = path, .v = new value)
//   kind=2: Push/splice array (.k = path, .v = items, .i? = truncate index)
//   kind=3: Delete property (.k = path) -- intentionally unhandled

type ModelState = Unknown | InProgress | Complete

type internal ReqState =
    { ModelState: ModelState
      CompletedAt: DateTimeOffset option
      ResponseText: string option
      ResponseKinds: string list
      UserText: string option }

let private emptyReq =
    { ModelState = Unknown; CompletedAt = None; ResponseText = None; ResponseKinds = []; UserText = None }

let internal modelStateFromInt = function
    | 0 -> InProgress
    | _ -> Complete

let private applyResponseParts (parts: JsonElement) (state: ReqState) =
    if parts.ValueKind <> JsonValueKind.Array then state
    else
        parts.EnumerateArray()
        |> Seq.fold (fun acc item ->
            match item.TryGetProperty("kind"), item.TryGetProperty("value") with
            | (true, k), (true, v) when v.ValueKind = JsonValueKind.String ->
                let kv = k.GetString()
                let text = v.GetString()
                let withKind =
                    if List.contains kv acc.ResponseKinds then acc
                    else { acc with ResponseKinds = kv :: acc.ResponseKinds }
                if String.IsNullOrWhiteSpace(text) then withKind
                else { withKind with ResponseText = Some text }
            | (true, k), _ ->
                let kv = k.GetString()
                if List.contains kv acc.ResponseKinds then acc
                else { acc with ResponseKinds = kv :: acc.ResponseKinds }
            | _, (true, v) when v.ValueKind = JsonValueKind.String ->
                let text = v.GetString()
                if String.IsNullOrWhiteSpace(text) then acc
                else { acc with ResponseText = Some text }
            | _ -> acc
        ) state

let private applyModelState (ms: JsonElement) (state: ReqState) =
    let modelState =
        match ms.TryGetProperty("value") with
        | true, v -> modelStateFromInt (v.GetInt32())
        | _ -> state.ModelState

    let completedAt =
        match ms.TryGetProperty("completedAt") with
        | true, ca -> Some(DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64()))
        | _ -> state.CompletedAt

    { state with ModelState = modelState; CompletedAt = completedAt }

let private applyRequestObject (req: JsonElement) (state: ReqState) =
    let withModel =
        match req.TryGetProperty("modelState") with
        | true, ms -> applyModelState ms state
        | _ -> state

    let withResponse =
        match req.TryGetProperty("response") with
        | true, r -> applyResponseParts r withModel
        | _ -> withModel

    match req.TryGetProperty("message") with
    | true, msg ->
        match msg.TryGetProperty("text") with
        | true, t ->
            let text = t.GetString()
            if String.IsNullOrWhiteSpace(text) then withResponse
            else { withResponse with UserText = Some text }
        | _ -> withResponse
    | _ -> withResponse

let private readAllLines (filePath: string) =
    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream)
        reader.ReadToEnd().Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.toList
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to read session file {filePath}: {ex.Message}"
        []

let private getPath (root: JsonElement) =
    match root.TryGetProperty("k") with
    | true, k when k.ValueKind = JsonValueKind.Array ->
        k.EnumerateArray() |> Seq.map (fun e -> e.GetRawText().Trim('"')) |> Seq.toArray |> Some
    | _ -> None

let private applySnapshotLine (root: JsonElement) =
    match root.TryGetProperty("v") with
    | true, v ->
        match v.TryGetProperty("requests") with
        | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
            reqs.EnumerateArray()
            |> Seq.map (fun req -> applyRequestObject req emptyReq)
            |> Seq.toList
            |> Some
        | _ -> None
    | _ -> None

let private ensureIndex (idx: int) (requests: ReqState list) =
    if idx < requests.Length then requests
    else requests @ List.init (idx + 1 - requests.Length) (fun _ -> emptyReq)

let private updateAt (idx: int) (f: ReqState -> ReqState) (requests: ReqState list) =
    requests
    |> List.mapi (fun i req -> if i = idx then f req else req)

let private applySetLine (root: JsonElement) (acc: ReqState list) =
    match getPath root, root.TryGetProperty("v") with
    | Some path, (true, v) when path.Length = 3 && path[0] = "requests" && path[2] = "modelState" ->
        match Int32.TryParse(path[1]) with
        | true, idx ->
            let reqs = ensureIndex idx acc
            updateAt idx (applyModelState v) reqs
        | _ -> acc
    | _ -> acc

let private applyPushLine (root: JsonElement) (acc: ReqState list) =
    match getPath root, root.TryGetProperty("v") with
    | Some path, (true, v) when v.ValueKind = JsonValueKind.Array ->
        if path.Length = 1 && path[0] = "requests" then
            v.EnumerateArray()
            |> Seq.fold (fun reqs req ->
                reqs @ [ applyRequestObject req emptyReq ]
            ) acc
        elif path.Length = 3 && path[0] = "requests" && path[2] = "response" then
            match Int32.TryParse(path[1]) with
            | true, idx ->
                let reqs = ensureIndex idx acc
                updateAt idx (applyResponseParts v) reqs
            | _ -> acc
        else acc
    | _ -> acc

let private applyLine (acc: ReqState list) (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match root.TryGetProperty("kind") with
        | true, kp ->
            match kp.GetInt32() with
            | 0 -> applySnapshotLine root |> Option.defaultValue acc
            | 1 -> applySetLine root acc
            | 2 -> applyPushLine root acc
            | _ -> acc
        | _ -> acc
    with _ -> acc

let internal reconstructLastRequest (lines: string list) : ReqState option =
    lines |> List.fold applyLine [] |> List.tryLast

type private CachedReq =
    { Req: ReqState option
      FileMtime: DateTimeOffset
      CachedAt: DateTimeOffset }

let private reqCache = System.Collections.Concurrent.ConcurrentDictionary<string, CachedReq>()

let private getReconstructed (fi: FileInfo) : ReqState option =
    let mtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
    let key = fi.FullName

    match reqCache.TryGetValue(key) with
    | true, c when c.FileMtime = mtime -> c.Req
    | _ ->
        let lines = readAllLines fi.FullName
        let req = reconstructLastRequest lines
        reqCache[key] <- { Req = req; FileMtime = mtime; CachedAt = DateTimeOffset.UtcNow }
        req

let private twoHours = TimeSpan.FromHours(2.0)

let private tryGetActiveSession (worktreePath: string) =
    getChatSessionsDir worktreePath
    |> Option.bind findMostRecentSessionFile
    |> Option.bind (fun fi ->
        let mtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
        let age = DateTimeOffset.UtcNow - mtime
        if age > twoHours then None
        else Some(fi, mtime))

let private statusFromReqState = function
    | { ModelState = InProgress } | { ModelState = Unknown } -> Working
    | _ -> Done

let private toLastMessageEvent (req: ReqState) (fileMtime: DateTimeOffset) =
    match req.ModelState with
    | InProgress | Unknown ->
        Some { Source = "copilot-vscode"
               Message = "Working..."
               Timestamp = fileMtime
               Status = Some StepStatus.Running
               Duration = None }
    | Complete ->
        req.ResponseText
        |> Option.map (fun text ->
            let ts = req.CompletedAt |> Option.defaultValue fileMtime
            { Source = "copilot-vscode"
              Message = FileUtils.truncateMessage 80 text
              Timestamp = ts
              Status = None
              Duration = None })

let getSessionMtime (worktreePath: string) : DateTimeOffset option =
    getChatSessionsDir worktreePath
    |> Option.bind findMostRecentSessionFile
    |> Option.map (fun fi -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))

let getStatus (worktreePath: string) : CodingToolStatus =
    match tryGetActiveSession worktreePath with
    | None -> Idle
    | Some (fi, _) ->
        match getReconstructed fi with
        | None -> Idle
        | Some req -> statusFromReqState req

let getLastMessage (worktreePath: string) : CardEvent option =
    tryGetActiveSession worktreePath
    |> Option.bind (fun (fi, fileMtime) ->
        getReconstructed fi
        |> Option.bind (fun req -> toLastMessageEvent req fileMtime))

let getLastUserMessage (worktreePath: string) : (string * DateTimeOffset) option =
    match getChatSessionsDir worktreePath |> Option.bind findMostRecentSessionFile with
    | None -> None
    | Some fi ->
        let fileMtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
        getReconstructed fi
        |> Option.bind (fun req ->
            req.UserText
            |> Option.map (fun text ->
                let ts = req.CompletedAt |> Option.defaultValue fileMtime
                FileUtils.truncateMessage 120 text, ts))
