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
            // below 2 GB the single whole-range buffer is a Large-Object-Heap spike. Each 64 KB chunk
            // buffer stays off the LOH, and the int64 offset arithmetic below never narrows to int32.
            //
            // Complete (newline-terminated) lines are decoded as we go; the bytes after the final '\n'
            // stay unconsumed (partial trailing line, carried across the chunk boundary) so a later
            // append can complete them. A '\n' byte (0x0A) — like '\r' (0x0D) — never occurs as a UTF-8
            // continuation byte, so cutting each chunk at its last '\n' and carrying the remainder is
            // UTF-8-safe and yields the same lines/offset as decoding the whole consumed range at once.
            let chunkSize = 64 * 1024
            let rangeLength = endOffset - startOffset

            // Complete lines within a byte region (carry ++ chunk up to its last '\n'): decoded,
            // trimmed, blanks dropped, oldest→newest.
            let linesFrom (region: byte[]) : string list =
                System.Text.Encoding.UTF8
                    .GetString(region)
                    .Split([| '\r'; '\n' |], StringSplitOptions.None)
                |> Array.choose (fun s ->
                    let trimmed = s.Trim()
                    if trimmed.Length > 0 then Some trimmed else None)
                |> List.ofArray

            // Tail-recursive fold over the chunks (no mutation / loop): carries the partial-line bytes,
            // the collected lines (reversed), the bytes processed from the range, and the offset just
            // past the last consumed '\n' (relative to startOffset).
            let rec readChunks (carry: byte[]) (linesRev: string list) (processed: int64) (lastNewlineEnd: int64) =
                if processed >= rangeLength then (linesRev, lastNewlineEnd)
                else
                    let toRead = int (min (int64 chunkSize) (rangeLength - processed))
                    let buffer = Array.zeroCreate toRead
                    let read = stream.Read(buffer, 0, toRead)
                    if read <= 0 then
                        (linesRev, lastNewlineEnd) // range claimed more bytes than the file now holds
                    else
                        match System.Array.LastIndexOf(buffer, 0x0Auy, read - 1, read) with
                        | lastNl when lastNl >= 0 ->
                            // carried partial line ++ this chunk up to and including its last '\n'
                            let region = Array.append carry buffer[..lastNl]
                            let nextCarry = buffer[lastNl + 1 .. read - 1]
                            readChunks
                                nextCarry
                                (List.rev (linesFrom region) @ linesRev)
                                (processed + int64 read)
                                (processed + int64 (lastNl + 1))
                        | _ ->
                            // no '\n' in this chunk: the whole chunk extends the current partial line
                            readChunks (Array.append carry buffer[..read - 1]) linesRev (processed + int64 read) lastNewlineEnd

            let linesRev, lastNewlineEnd = readChunks [||] [] 0L 0L
            (List.rev linesRev, startOffset + lastNewlineEnd)
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
