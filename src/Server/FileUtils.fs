module Server.FileUtils

open System
open System.IO

let readLastLines (logTag: string) (filePath: string) (maxLines: int) =
    try
        use stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        if stream.Length = 0L then []
        else
            let bufferSize = 64 * 1024
            let length = stream.Length
            let start = Math.Max(0L, length - int64 bufferSize)
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
            |> Array.rev
            |> Array.truncate maxLines
            |> Array.toList
    with ex ->
        Log.log logTag $"Failed to read JSONL {filePath}: {ex.Message}"
        []

let refreshIfStale (maxAge: TimeSpan) (cache: 'T ref) (getAge: 'T -> DateTimeOffset) (rebuild: unit -> 'T) =
    let current = cache.Value
    let age = DateTimeOffset.UtcNow - getAge current

    if age > maxAge then
        let newValue = rebuild ()
        cache.Value <- newValue
        newValue
    else
        current

let findInLines (tryParse: string -> 'a option) (lines: string array) : 'a option =
    lines
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.tryPick tryParse

let readChunk (stream: FileStream) (position: int64) (length: int) : string =
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
                        let content = readChunk stream chunkStart readLength
                        let lines = content.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

                        let trimmedLines =
                            if isAtFileStart || lines.Length = 0 then lines
                            else lines[1..]

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
