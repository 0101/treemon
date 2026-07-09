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

/// Reads bytes [startOffset, endOffset) of a file and returns the complete (newline-terminated)
/// lines within that range — cleaned, non-empty, trimmed, oldest→newest — together with the absolute
/// byte offset just past the last consumed newline. A trailing partial line (bytes after the last
/// newline, not yet terminated) is left unconsumed: excluded from both the returned lines and the
/// returned offset, so a later append can complete it. When the range holds no newline at all,
/// returns ([], startOffset). This is what the append-aware incremental session scan folds each cycle.
let readByteRangeLines (logTag: string) (filePath: string) (startOffset: int64) (endOffset: int64) : string list * int64 =
    try
        if endOffset <= startOffset then ([], startOffset)
        else
            use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            stream.Seek(startOffset, SeekOrigin.Begin) |> ignore

            // Read the range in bounded 64 KB chunks rather than a single Array.zeroCreate of the whole
            // range. A whole-file read (first scan / post-prune rescan / truncation reset) of an
            // events.jsonl larger than Int32.MaxValue would otherwise overflow `int (endOffset -
            // startOffset)`: a 2-4 GB range wraps negative (Array.zeroCreate throws), a >4 GB range
            // wraps to a small positive count (ReadExactly silently reads a truncated prefix). Even
            // below 2 GB the single whole-range buffer is a Large-Object-Heap spike. The reused 64 KB
            // buffer stays off the LOH, and the int64 offset arithmetic below never narrows to int32.
            //
            // Complete (newline-terminated) lines are decoded and collected as we go; the bytes after
            // the final '\n' stay unconsumed (partial trailing line) so a later append can complete
            // them. A '\n' byte (0x0A) — like '\r' (0x0D) — never occurs as a UTF-8 continuation byte,
            // so cutting each chunk at its last '\n' and carrying the remainder is UTF-8-safe and yields
            // the same lines/offset as decoding the whole consumed range at once.
            let chunkSize = 64 * 1024
            let readBuffer = Array.zeroCreate chunkSize
            let lines = ResizeArray<string>()
            let carry = ResizeArray<byte>() // partial line bytes carried across a chunk boundary

            let addLinesFrom (region: byte[]) (regionLen: int) =
                System.Text.Encoding.UTF8
                    .GetString(region, 0, regionLen)
                    .Split([| '\r'; '\n' |], StringSplitOptions.None)
                |> Array.iter (fun s ->
                    let trimmed = s.Trim()
                    if trimmed.Length > 0 then lines.Add trimmed)

            let mutable remaining = endOffset - startOffset
            let mutable processed = 0L // bytes read from the range so far
            let mutable lastNewlineEnd = 0L // offset past the last consumed '\n', relative to startOffset
            let mutable stop = false

            while remaining > 0L && not stop do
                let toRead = int (min (int64 chunkSize) remaining)
                let read = stream.Read(readBuffer, 0, toRead)
                if read <= 0 then
                    stop <- true // range claimed more bytes than the file now holds; stop gracefully
                else
                    match System.Array.LastIndexOf(readBuffer, 0x0Auy, read - 1, read) with
                    | lastNl when lastNl >= 0 ->
                        // carried partial line ++ this chunk up to and including its last '\n' = complete lines
                        let regionLen = carry.Count + lastNl + 1
                        let region = Array.zeroCreate regionLen
                        carry.CopyTo(region, 0)
                        Array.blit readBuffer 0 region carry.Count (lastNl + 1)
                        addLinesFrom region regionLen
                        carry.Clear()
                        if lastNl + 1 < read then
                            carry.AddRange(Seq.ofArray (Array.sub readBuffer (lastNl + 1) (read - lastNl - 1)))
                        lastNewlineEnd <- processed + int64 (lastNl + 1)
                    | _ ->
                        // no '\n' in this chunk: the whole chunk extends the current partial line
                        carry.AddRange(Seq.ofArray (Array.sub readBuffer 0 read))

                    processed <- processed + int64 read
                    remaining <- remaining - int64 read

            (List.ofSeq lines, startOffset + lastNewlineEnd)
    with ex ->
        Log.log logTag $"Failed to read byte range of {filePath}: {ex.Message}"
        ([], startOffset)

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
