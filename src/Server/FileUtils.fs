module Server.FileUtils

open System
open System.IO

/// Reads the tail of a text file (up to maxBytes back from EOF) as clean, non-empty, trimmed lines
/// in file order (oldest→newest). A leading partial line is dropped unless the read reached the file
/// start. Unlike scanBackward this returns the whole tail so callers can run a *stateful* scan
/// (e.g. threading a small state machine over consecutive events) — scanBackward's overlapping
/// chunks can re-emit a boundary line and so only suit stateless first-match picks.
let readTailLines (logTag: string) (filePath: string) (maxBytes: int) : string list =
    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let length = stream.Length
        if length = 0L then []
        else
            let start = Math.Max(0L, length - int64 maxBytes)
            stream.Seek(start, SeekOrigin.Begin) |> ignore

            use reader = new StreamReader(stream)
            let content = reader.ReadToEnd()
            let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.None)

            let linesToProcess =
                if start > 0L && lines.Length > 0 then
                    lines[1..]
                else
                    lines

            linesToProcess
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.toList
    with ex ->
        Log.log logTag $"Failed to read JSONL {filePath}: {ex.Message}"
        []

let readLastLines (logTag: string) (filePath: string) (maxLines: int) =
    readTailLines logTag filePath (64 * 1024)
    |> List.rev
    |> List.truncate maxLines

let refreshIfStale (maxAge: TimeSpan) (cache: 'T ref) (getAge: 'T -> DateTimeOffset) (rebuild: unit -> 'T) =
    let current = cache.Value
    let age = DateTimeOffset.UtcNow - getAge current

    if age > maxAge then
        let newValue = rebuild ()
        cache.Value <- newValue
        newValue
    else
        current

let private findInLines (tryParse: string -> 'a option) (lines: string array) : 'a option =
    lines
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.tryPick tryParse

let private readChunk (stream: FileStream) (position: int64) (length: int) : string =
    stream.Seek(position, SeekOrigin.Begin) |> ignore
    let buffer = Array.zeroCreate length
    let bytesRead = stream.Read(buffer, 0, length)
    System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead)

let scanBackward (logTag: string) (filePath: string) (tryParse: string -> 'a option) : 'a option =
    let chunkSize = 64L * 1024L
    let overlap = 1024L
    let stepSize = chunkSize - overlap
    let maxChunks = 16

    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        let fileLength = stream.Length
        if fileLength = 0L then None
        else
            let rec scanChunk chunkIndex =
                if chunkIndex >= maxChunks then None
                else
                    let rawStart = fileLength - chunkSize - (int64 chunkIndex) * stepSize
                    let chunkStart = Math.Max(0L, rawStart)
                    let readLength = int (Math.Min(chunkSize, fileLength - chunkStart))
                    if readLength <= 0 then None
                    else
                        let isAtFileStart = chunkStart = 0L
                        let isAtFileEnd = chunkStart + int64 readLength = fileLength
                        let content = readChunk stream chunkStart readLength
                        let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

                        let trimmedLines =
                            let afterLeading =
                                if isAtFileStart || lines.Length = 0 then lines
                                else lines[1..]
                            if isAtFileEnd || afterLeading.Length = 0 then afterLeading
                            else afterLeading[.. afterLeading.Length - 2]

                        match findInLines tryParse trimmedLines with
                        | Some _ as r -> r
                        | None when isAtFileStart -> None
                        | None -> scanChunk (chunkIndex + 1)

            scanChunk 0
    with ex ->
        Log.log logTag $"Failed to scan JSONL {filePath}: {ex.Message}"
        None

let truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()
    if singleLine.Length <= maxLen then singleLine
    else singleLine[..maxLen-1].TrimEnd() + "..."
