module Server.VsCodeCopilotDetector

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Shared

// VS Code stores per-workspace chat sessions under:
//   %APPDATA%\Code\User\workspaceStorage\{hash}\chatSessions\{session-guid}.jsonl
// The workspace.json in that same hash dir maps the hash to the workspace folder URI.

let private vsCodeWorkspaceStorageDir =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Code",
        "User",
        "workspaceStorage"
    )

// Index: normalized local path → chatSessions directory
type private WorkspaceIndex =
    { PathToChatSessions: Dictionary<string, string>
      BuiltAt: DateTimeOffset }

let private workspaceIndex =
    ref
        { PathToChatSessions = Dictionary(StringComparer.OrdinalIgnoreCase)
          BuiltAt = DateTimeOffset.MinValue }

let private tryDecodeLocalPath (folderUri: string) =
    try
        // e.g. "file:///q%3A/src/AITestAgent" — the %3A-encoded colon prevents .NET from
        // recognising the Windows drive root, so decode first to get "file:///q:/src/AITestAgent"
        // and then Uri.LocalPath returns the correct "q:\src\AITestAgent".
        let decoded = Uri.UnescapeDataString(folderUri)
        Some(Uri(decoded).LocalPath)
    with _ ->
        None

let private buildWorkspaceIndex () =
    let index = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

    try
        if Directory.Exists(vsCodeWorkspaceStorageDir) then
            Directory.GetDirectories(vsCodeWorkspaceStorageDir)
            |> Array.iter (fun hashDir ->
                let workspaceJson = Path.Combine(hashDir, "workspace.json")
                let chatSessionsDir = Path.Combine(hashDir, "chatSessions")

                if File.Exists(workspaceJson) && Directory.Exists(chatSessionsDir) then
                    try
                        let json = File.ReadAllText(workspaceJson)
                        use doc = JsonDocument.Parse(json)

                        let folderUri =
                            match doc.RootElement.TryGetProperty("folder") with
                            | true, p -> Some(p.GetString())
                            | _ -> None

                        folderUri
                        |> Option.bind tryDecodeLocalPath
                        |> Option.iter (fun localPath ->
                            let normalized =
                                Path.GetFullPath(localPath)
                                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                            index[normalized] <- chatSessionsDir)
                    with _ ->
                        ())
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to scan workspaceStorage: {ex.Message}"

    Log.log "VsCodeCopilot" $"Index built: {index.Count} workspace(s) with chatSessions mapped"
    { PathToChatSessions = index; BuiltAt = DateTimeOffset.UtcNow }

let private refreshIndex () =
    let current = workspaceIndex.Value
    let age = DateTimeOffset.UtcNow - current.BuiltAt

    if age > TimeSpan.FromSeconds(60.0) then
        let newIndex = buildWorkspaceIndex ()
        workspaceIndex.Value <- newIndex
        newIndex
    else
        current

let private getChatSessionsDir (worktreePath: string) =
    let index = refreshIndex ()

    let normalized =
        Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    match index.PathToChatSessions.TryGetValue(normalized) with
    | true, dir -> Some dir
    | false, _ ->
        None

let private findMostRecentSessionFile (chatSessionsDir: string) =
    try
        if not (Directory.Exists(chatSessionsDir)) then
            Log.log "VsCodeCopilot" $"chatSessions dir does not exist: {chatSessionsDir}"
            None
        else
            Directory.GetFiles(chatSessionsDir, "*.jsonl")
                |> Array.choose (fun path ->
                    try Some(FileInfo(path))
                    with _ -> None)
                |> Array.sortByDescending _.LastWriteTimeUtc
                |> Array.tryHead
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to scan chatSessions dir: {ex.Message}"
        None

/// Reads the last N lines of a file. Opens with FileAccess.Read + FileShare.ReadWrite
/// so VS Code's exclusive append handle is not blocked.
let private readLastLines (filePath: string) (maxLines: int) =
    try
        use stream =
            new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        if stream.Length = 0L then
            []
        else
            let bufferSize = 64 * 1024
            let length = stream.Length
            let start = Math.Max(0L, length - int64 bufferSize)
            stream.Seek(start, SeekOrigin.Begin) |> ignore

            use reader = new StreamReader(stream)
            let content = reader.ReadToEnd()
            let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.None)

            let linesToProcess =
                if start > 0L && lines.Length > 0 then lines[1..] else lines

            linesToProcess
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.rev
            |> Array.truncate maxLines
            |> Array.toList
    with ex ->
        Log.log "VsCodeCopilot" $"Failed to read session file {filePath}: {ex.Message}"
        []

// ── JSONL operation-log format (objectMutationLog.ts) ────────────────────────
//
//  kind=0  Initial: full session snapshot (.v = ISerializableChatData3)
//  kind=1  Set:     replace value at path  (.k = path array, .v = new value)
//  kind=2  Push:    append/splice array    (.k = path array, .v = items, .i? = truncate index)
//  kind=3  Delete:  remove property        (.k = path array)
//
//  Text and modelState always arrive on separate lines:
//    kind=2, k=["requests",N,"response"], v=[...parts]
//      → response parts pushed incrementally; plain text = {value:"..."} (no "kind" field)
//    kind=1, k=["requests",N,"modelState"], v={value:0|1|4, completedAt?:<ms>}
//      → 0=in-progress, 1/4=complete
//    kind=2, k=["requests"], v=[{...new request object...}]
//      → new request appended
//
//  To get last-request state we must reconstruct by replaying all mutations.

type private ReqState =
    { mutable ModelStateValue: int option
      mutable CompletedAt: DateTimeOffset option
      mutable ResponseText: string option
      mutable ResponseKinds: string list
      mutable UserText: string option }

let private emptyReq () =
    { ModelStateValue = None; CompletedAt = None; ResponseText = None; ResponseKinds = []; UserText = None }

let private applyResponseParts (parts: JsonElement) (s: ReqState) =
    if parts.ValueKind = JsonValueKind.Array then
        for item in parts.EnumerateArray() do
            match item.TryGetProperty("kind") with
            | true, k ->
                let kv = k.GetString()
                if not (List.contains kv s.ResponseKinds) then
                    s.ResponseKinds <- kv :: s.ResponseKinds
            | false, _ ->
                // No "kind" field = IMarkdownString plain text response
                match item.TryGetProperty("value") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    let text = v.GetString()
                    if not (String.IsNullOrWhiteSpace(text)) then
                        s.ResponseText <- Some text
                | _ -> ()

let private applyModelState (ms: JsonElement) (s: ReqState) =
    match ms.TryGetProperty("value") with
    | true, v -> s.ModelStateValue <- Some(v.GetInt32())
    | _ -> ()
    match ms.TryGetProperty("completedAt") with
    | true, ca -> s.CompletedAt <- Some(DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64()))
    | _ -> ()

let private applyRequestObject (req: JsonElement) (s: ReqState) =
    match req.TryGetProperty("modelState") with
    | true, ms -> applyModelState ms s
    | _ -> ()
    match req.TryGetProperty("response") with
    | true, r -> applyResponseParts r s
    | _ -> ()
    match req.TryGetProperty("message") with
    | true, msg ->
        match msg.TryGetProperty("text") with
        | true, t ->
            let text = t.GetString()
            if not (String.IsNullOrWhiteSpace(text)) then s.UserText <- Some text
        | _ -> ()
    | _ -> ()

/// Reads all lines of a file forward in order (for state reconstruction).
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

/// Replay all JSONL mutations and return the final state of the last request.
let private reconstructLastRequest (lines: string list) : ReqState option =
    let requests = System.Collections.Generic.List<ReqState>()

    let getOrCreate i =
        while requests.Count <= i do
            requests.Add(emptyReq ())
        requests[i]

    for line in lines do
        try
            use doc = JsonDocument.Parse(line)
            let root = doc.RootElement

            match root.TryGetProperty("kind") with
            | true, kp ->
                match kp.GetInt32() with
                | 0 ->
                    // Full snapshot — rebuild requests list
                    match root.TryGetProperty("v") with
                    | true, v ->
                        match v.TryGetProperty("requests") with
                        | true, reqs when reqs.ValueKind = JsonValueKind.Array ->
                            requests.Clear()
                            let arr = reqs.EnumerateArray() |> Seq.toArray
                            for i in 0 .. arr.Length - 1 do
                                applyRequestObject arr[i] (getOrCreate i)
                        | _ -> ()
                    | _ -> ()

                | 1 ->
                    // Set value at path
                    match root.TryGetProperty("k"), root.TryGetProperty("v") with
                    | (true, k), (true, v) when k.ValueKind = JsonValueKind.Array ->
                        let path = k.EnumerateArray() |> Seq.map (fun e -> e.GetRawText().Trim('"')) |> Seq.toArray

                        if path.Length = 3 && path[0] = "requests" && path[2] = "modelState" then
                            match Int32.TryParse(path[1]) with
                            | true, idx -> applyModelState v (getOrCreate idx)
                            | _ -> ()
                    | _ -> ()

                | 2 ->
                    // Push/splice
                    match root.TryGetProperty("k"), root.TryGetProperty("v") with
                    | (true, k), (true, v) when k.ValueKind = JsonValueKind.Array && v.ValueKind = JsonValueKind.Array ->
                        let path = k.EnumerateArray() |> Seq.map (fun e -> e.GetRawText().Trim('"')) |> Seq.toArray

                        if path.Length = 1 && path[0] = "requests" then
                            // Append new full request objects
                            for req in v.EnumerateArray() do
                                applyRequestObject req (getOrCreate requests.Count)
                        elif path.Length = 3 && path[0] = "requests" && path[2] = "response" then
                            // Push response parts to specific request
                            match Int32.TryParse(path[1]) with
                            | true, idx -> applyResponseParts v (getOrCreate idx)
                            | _ -> ()
                    | _ -> ()

                | _ -> ()
            | _ -> ()
        with _ ->
            ()

    if requests.Count = 0 then None
    else Some(requests[requests.Count - 1])

// ── Per-file reconstruction cache ────────────────────────────────────────────
// Reconstruction is shared between getStatus / getLastMessage / getLastUserMessage
// so the file is only read and parsed once per mtime change.

type private CachedReq =
    { Req: ReqState option
      FileMtime: DateTimeOffset
      CachedAt: DateTimeOffset }

let private reqCache = System.Collections.Concurrent.ConcurrentDictionary<string, CachedReq>()

let private getReconstructed (fi: FileInfo) : ReqState option =
    let mtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
    let key = fi.FullName

    let cached = reqCache.TryGetValue(key)
    match cached with
    | true, c when c.FileMtime = mtime -> c.Req
    | _ ->
        let lines = readAllLines fi.FullName
        let req = reconstructLastRequest lines
        reqCache[key] <- { Req = req; FileMtime = mtime; CachedAt = DateTimeOffset.UtcNow }
        req

// ── Public API ────────────────────────────────────────────────────────────────

let getSessionMtime (worktreePath: string) : DateTimeOffset option =
    getChatSessionsDir worktreePath
    |> Option.bind findMostRecentSessionFile
    |> Option.map (fun fi -> DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero))

let getStatus (worktreePath: string) : CodingToolStatus =
    match getChatSessionsDir worktreePath |> Option.bind findMostRecentSessionFile with
    | None -> Idle
    | Some fi ->
        let age = DateTimeOffset.UtcNow - DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
        if age > TimeSpan.FromHours(2.0) then Idle
        else
            match getReconstructed fi with
            | None -> Idle
            | Some req ->
                match req.ModelStateValue with
                | Some 0 -> Working        // explicit in-progress
                | None   -> Working        // no completion patch yet = active turn
                | Some _ -> Done           // 1=complete, 4=complete+tools, etc.

let private truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()
    if singleLine.Length <= maxLen then singleLine
    else singleLine[..maxLen - 1].TrimEnd() + "..."

let getLastMessage (worktreePath: string) : CardEvent option =
    match getChatSessionsDir worktreePath |> Option.bind findMostRecentSessionFile with
    | None -> None
    | Some fi ->
        let fileMtime = DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)
        let age = DateTimeOffset.UtcNow - fileMtime
        if age > TimeSpan.FromHours(2.0) then None
        else
            match getReconstructed fi with
            | None -> None
            | Some req ->
                match req.ModelStateValue with
                | Some 0 | None ->
                    // Active turn in progress (explicit state=0 or no completion patch yet)
                    Some { Source = "copilot-vscode"
                           Message = "Working..."
                           Timestamp = fileMtime
                           Status = Some StepStatus.Running
                           Duration = None }
                | Some _ ->
                    // Completed — return last response text if present
                    match req.ResponseText with
                    | None -> None
                    | Some text ->
                        let ts = req.CompletedAt |> Option.defaultValue fileMtime
                        Some { Source = "copilot-vscode"
                               Message = truncateMessage 80 text
                               Timestamp = ts
                               Status = None
                               Duration = None }

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
                truncateMessage 120 text, ts))
