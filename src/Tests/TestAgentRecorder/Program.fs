module TestAgentRecorder.Program

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading

[<DllImport("kernel32.dll", EntryPoint = "GetConsoleWindow")>]
extern nativeint private getConsoleWindow()

let rec private freePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    if port = 5000 then freePort () else port

let private startupPrompt (args: string array) =
    let sessionId =
        args
        |> Array.tryFind _.StartsWith("--session-id=")
        |> Option.map _.Substring("--session-id=".Length)

    match Environment.GetEnvironmentVariable("TM_COPILOT_BRIDGE_STARTUP"), sessionId with
    | "1", Some sessionId ->
        let serverPort =
            match Int32.TryParse(Environment.GetEnvironmentVariable("TREEMON_PORT")) with
            | true, port when port > 0 && port <> 5000 -> port
            | _ -> invalidOp "Mock startup requires an isolated non-production TREEMON_PORT"

        use listener = new HttpListener()
        let url = $"http://127.0.0.1:{freePort ()}/"
        listener.Prefixes.Add url
        listener.Start()
        let incoming = listener.GetContextAsync()
        use client = new HttpClient()
        let capability =
            RandomNumberGenerator.GetBytes(32)
            |> Convert.ToBase64String
            |> _.TrimEnd('=').Replace('+', '-').Replace('/', '_')
        let registration =
            {| worktreePath = Environment.CurrentDirectory
               injectUrl = url
               shutdownUrl = url + "shutdown"
               shutdownCapability = capability
               sessionId = sessionId
               parentProcessId = Environment.ProcessId
               terminalSessionId =
                    Environment.GetEnvironmentVariable("TREEMON_TERMINAL_SESSION_ID") |}
        use body = new StringContent(JsonSerializer.Serialize(registration), Encoding.UTF8, "application/json")
        use response =
            client.PostAsync($"http://127.0.0.1:{serverPort}/api/canvas/register", body)
                .GetAwaiter().GetResult()
        response.EnsureSuccessStatusCode() |> ignore

        try
            let context =
                incoming.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use reader = new StreamReader(context.Request.InputStream)
            use payload = JsonDocument.Parse(reader.ReadToEnd())

            if payload.RootElement.GetProperty("kind").GetString() <> "startup-prompt" then
                invalidOp "The first injected message must be the startup prompt"

            let prompt = payload.RootElement.GetProperty("prompt").GetString()
            context.Response.StatusCode <- 200
            context.Response.Close()
            Some prompt
        with :? TimeoutException ->
            None
    | _ -> None

[<EntryPoint>]
let main args =
    try
        let recorderPath =
            Environment.GetEnvironmentVariable("TM_COPILOT_RECORDER")

        let consoleHandleFile =
            Environment.GetEnvironmentVariable("TM_CONSOLE_HANDLE_FILE")

        if not (String.IsNullOrWhiteSpace consoleHandleFile) then
            File.WriteAllText(
                consoleHandleFile,
                string (getConsoleWindow().ToInt64())
            )

            Thread.Sleep Timeout.Infinite
            0
        elif String.IsNullOrWhiteSpace recorderPath then
            eprintfn "TM_COPILOT_RECORDER is required"
            1
        else
            let initialPrompt = startupPrompt args
            let payload =
                {| terminalSessionId =
                    Environment.GetEnvironmentVariable(
                        "TREEMON_TERMINAL_SESSION_ID"
                    )
                   worktreePath = Environment.CurrentDirectory
                   args = args
                   initialPrompt = initialPrompt |> Option.toObj |}

            File.AppendAllText(
                recorderPath,
                JsonSerializer.Serialize(payload) + Environment.NewLine,
                UTF8Encoding(false)
            )

            printfn
                $"RECORDED:{payload.terminalSessionId}"

            0
    with error ->
        eprintfn $"Test helper failed: {error.Message}"
        1
