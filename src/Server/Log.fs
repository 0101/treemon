module Log

open System
open System.IO

let private logPath =
    Path.Combine(Directory.GetCurrentDirectory(), ".agents", "server.log")

let init () =
    logPath |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
    File.WriteAllText(logPath, "")

let log (context: string) (message: string) =
    let timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss")
    let line = sprintf "%s [%s] %s%s" timestamp context message Environment.NewLine
    File.AppendAllText(logPath, line)
