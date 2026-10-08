module Tests.LaunchLogFixture

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Shared
open Server
open Server.SessionActivity

let internal recordedAttempt logPath sessionId path =
    PromptedLaunchDiagnostics.Attempt(
        SessionId sessionId,
        path,
        (fun () -> 0L),
        Log.writeAtPath logPath "Launch")

let readEvents logPath sessionId =
    use stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use reader = new StreamReader(stream)
    let marker = "[Launch] "
    let text = reader.ReadToEnd()
    let completeLength = text.LastIndexOf('\n') + 1
    text[..completeLength - 1].Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter (fun line -> line.Contains($"\"launchId\":\"{sessionId}\"", StringComparison.Ordinal))
    |> Array.map (fun line ->
        let start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length
        use document = JsonDocument.Parse(line[start..])
        document.RootElement.Clone())
    |> Array.toList

let eventName (entry: JsonElement) = entry.GetProperty("event").GetString()

let waitForEvent logPath sessionId expected =
    let deadline = Stopwatch.StartNew()
    let rec poll () =
        async {
            let events = readEvents logPath sessionId
            if events |> List.exists (fun entry -> eventName entry = expected) then
                return events
            elif deadline.Elapsed >= TimeSpan.FromSeconds 5.0 then
                return raise (NUnit.Framework.AssertionException $"Launch log did not contain {expected}")
            else
                do! Async.Sleep 10
                return! poll ()
        }
    poll () |> Async.RunSynchronously

let withCurrentLog path action =
    let key = "Treemon.Server.LogPath"
    let previous = AppContext.GetData key |> Option.ofObj
    try
        AppContext.SetData(key, path)
        action ()
    finally
        AppContext.SetData(key, Option.toObj previous)

let withUnavailableStderr (error: exn) action =
    let previous = Console.Error
    use writer =
        { new TextWriter() with
            override _.Encoding = System.Text.Encoding.UTF8
            override _.WriteLine(_: string) : unit = raise error }
    try
        Console.SetError writer
        action ()
    finally
        Console.SetError previous
