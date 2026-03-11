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

let truncateMessage (maxLen: int) (text: string) =
    let singleLine = text.Replace("\r", "").Replace("\n", " ").Trim()
    if singleLine.Length <= maxLen then singleLine
    else singleLine[..maxLen-1].TrimEnd() + "..."
