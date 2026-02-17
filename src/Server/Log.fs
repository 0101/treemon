module Log

open System
open System.IO

let private logPath =
    Path.Combine(Directory.GetCurrentDirectory(), "logs", "server.log")

let init () =
    logPath |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
    File.WriteAllText(logPath, "")

let private lockObj = obj ()

let log (context: string) (message: string) =
    let timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
    let line = sprintf "%s [%s] %s%s" timestamp context message Environment.NewLine
    lock lockObj (fun () -> File.AppendAllText(logPath, line))

let timed (context: string) (label: string) (work: Async<'T>) =
    async {
        let sw = Diagnostics.Stopwatch.StartNew()
        let! result = work
        sw.Stop()
        log context (sprintf "%s completed in %dms" label sw.ElapsedMilliseconds)
        return result
    }
