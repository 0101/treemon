module Log

open System
open System.IO

let private logPath =
    Path.Combine(Directory.GetCurrentDirectory(), "logs", "server.log")

let init () =
    logPath |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
    try
        use stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
        ()
    with _ -> ()

let private lockObj = obj ()

let log (context: string) (message: string) =
    let timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
    let line = $"{timestamp} [{context}] {message}{Environment.NewLine}"
    lock lockObj (fun () ->
        try
            use stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
            use writer = new StreamWriter(stream)
            writer.Write(line)
        with _ -> ())

let timed (context: string) (label: string) (work: Async<'T>) =
    async {
        let sw = Diagnostics.Stopwatch.StartNew()
        let! result = work
        sw.Stop()
        log context $"{label} completed in {sw.ElapsedMilliseconds}ms"
        return result
    }
