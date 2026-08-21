module Tests.TerminalHostTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open TerminalHost
open Tests.GitTestHelpers
open Tests.TestUtils
open Treemon.TerminalHosting

let private getTask (task: Task<'a>) =
    task.GetAwaiter().GetResult()

let private waitUntil timeout predicate =
    let deadline = DateTimeOffset.UtcNow + timeout

    let rec wait () =
        if predicate () then
            true
        elif DateTimeOffset.UtcNow >= deadline then
            false
        else
            Thread.Sleep 50
            wait ()

    wait ()

let private killExactPidFromFile path =
    if File.Exists path then
        match Int32.TryParse(File.ReadAllText(path).Trim()) with
        | true, pid ->
            try
                use owned = Process.GetProcessById pid

                if not owned.HasExited then
                    owned.Kill(entireProcessTree = true)
                    owned.WaitForExit()
            with :? ArgumentException ->
                ()
        | false, _ -> ()

let private requireOk result =
    match result with
    | Ok value -> value
    | Error error ->
        Assert.Fail(error)
        Unchecked.defaultof<_>

let private requireSome message value =
    match value with
    | Some result -> result
    | None ->
        Assert.Fail(message)
        Unchecked.defaultof<_>

let private executableOnPath name =
    Environment.GetEnvironmentVariable("PATH")
    |> Option.ofObj
    |> Option.map _.Split(Path.PathSeparator)
    |> Option.defaultValue Array.empty
    |> Array.map (fun directory -> Path.Combine(directory, name))
    |> Array.tryFind File.Exists
    |> Option.defaultWith (fun () ->
        Assert.Fail($"Could not find {name} on PATH")
        "")

let private responseDocument (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> getTask
    |> JsonDocument.Parse

let private responseHeader name (response: HttpResponseMessage) =
    response.Headers.GetValues name
    |> Seq.exactlyOne

let private terminalIds (document: JsonDocument) =
    document.RootElement.GetProperty("terminals").EnumerateArray()
    |> Seq.map (fun terminal -> terminal.GetProperty("sessionId").GetString())
    |> Seq.choose Option.ofObj
    |> Seq.toList

let private terminalEndpoints (document: JsonDocument) =
    document.RootElement.GetProperty("terminals").EnumerateArray()
    |> Seq.map (fun terminal ->
        terminal.GetProperty("attachmentEndpoint").GetString())
    |> Seq.choose Option.ofObj
    |> Seq.toList

let private propertyNames (element: JsonElement) =
    element.EnumerateObject()
    |> Seq.map _.Name
    |> Set.ofSeq

let private assertExactProperties expected element =
    Assert.That(
        propertyNames element |> Set.toList,
        Is.EquivalentTo(expected)
    )

let private assertRegistryResponseV1Shape (document: JsonDocument) =
    assertExactProperties
        [ "revision"; "terminals" ]
        document.RootElement

    document.RootElement.GetProperty("terminals").EnumerateArray()
    |> Seq.iter (
        assertExactProperties
            [ "sessionId"
              "worktreePath"
              "attachmentEndpoint" ]
    )

type private TestWebSocket() =
    inherit System.Net.WebSockets.WebSocket()

    let sent = ConcurrentQueue<byte array>()

    let receiveCompletion =
        TaskCompletionSource<System.Net.WebSockets.WebSocketReceiveResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

    // WebSocket is an inherently stateful test boundary; mutation is confined to this fake.
    let mutable state = System.Net.WebSockets.WebSocketState.Open
    let mutable closeStatus = Nullable<System.Net.WebSockets.WebSocketCloseStatus>()
    let mutable closeDescription: string option = None

    let completeReceive status description =
        receiveCompletion.TrySetResult(
            System.Net.WebSockets.WebSocketReceiveResult(
                0,
                System.Net.WebSockets.WebSocketMessageType.Close,
                true,
                Nullable status,
                description
            )
        )
        |> ignore

    let close status description =
        closeStatus <- Nullable status
        closeDescription <- Some description
        state <- System.Net.WebSockets.WebSocketState.CloseSent
        completeReceive status description
        Task.CompletedTask

    member _.Sent = sent.ToArray() |> Array.toList
    member _.CloseDescription = closeDescription

    override _.Abort() =
        state <- System.Net.WebSockets.WebSocketState.Aborted

        completeReceive
            System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable
            "aborted"

    override _.CloseAsync(status, description, _) =
        close status description

    override _.CloseOutputAsync(status, description, _) =
        close status description

    override _.CloseStatus = closeStatus
    override _.CloseStatusDescription = closeDescription |> Option.toObj

    override _.Dispose() =
        state <- System.Net.WebSockets.WebSocketState.Closed

        completeReceive
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure
            "disposed"

    override _.ReceiveAsync(
        _: ArraySegment<byte>,
        _: CancellationToken
    ) : Task<System.Net.WebSockets.WebSocketReceiveResult> =
        receiveCompletion.Task

    override _.SendAsync(
        buffer: ArraySegment<byte>,
        _: System.Net.WebSockets.WebSocketMessageType,
        _: bool,
        _: CancellationToken
    ) : Task =
        sent.Enqueue(buffer.ToArray())
        Task.CompletedTask

    override _.State = state
    override _.SubProtocol = "tty"

type private ApiFixture() =
    let root = uniquePath "terminal-host-api"
    let worktree = Path.Combine(root, "repo")
    let starts = ConcurrentQueue<string>()
    let closes = ConcurrentQueue<string>()
    let token = "test-token-with-fixed-value"

    do
        Directory.CreateDirectory root |> ignore
        initRepo worktree

    let starter sessionId canonicalWorktree =
        async {
            starts.Enqueue sessionId

            return
                Ok
                    { ProcessId = 20_001 + starts.Count
                      ProcessStartTimeUtcTicks = int64 (30_001 + starts.Count)
                      TtydPort = 40_001 + starts.Count
                      HasExited = fun () -> false
                      Close = fun () -> closes.Enqueue sessionId }
        }

    let dataPlaneStarter sessionId _ =
        async {
            return
                Ok
                    { AttachmentEndpoint =
                        $"http://127.0.0.1:41000/_treemon/{sessionId}/{token}/"
                      AttachSocket = fun _ -> async.Return None
                      AcceptBrowserFrame = fun _ _ -> async.Return(Ok())
                      DetachSocket = fun _ -> async.Return()
                      AcceptUpstreamFrame = fun _ -> async.Return()
                      UpstreamEnded = fun () -> async.Return()
                      Stop = fun () -> async.Return() }
        }

    let registry = TerminalRegistry.create starter dataPlaneStarter

    let running =
        ControlApi.start
            { Port = 0
              AllowedOrigins = [ "http://localhost:5174" ] }
            token
            12_345
            638_900_000_000_000_000L
            "test-version"
            registry
        |> getTask

    let client = new HttpClient(BaseAddress = Uri running.Endpoint)

    do
        client.DefaultRequestHeaders.Authorization <-
            AuthenticationHeaderValue("Bearer", token)

    member _.Client = client
    member _.Endpoint = running.Endpoint
    member _.Registry = registry
    member _.Running = running
    member _.StartCount = starts.Count
    member _.CloseCount = closes.Count
    member _.Token = token
    member _.Worktree = worktree
    member _.UnknownDirectory = root

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            TerminalRegistry.shutdown registry |> Async.RunSynchronously
            ControlApi.stop running |> getTask

            try
                Directory.Delete(root, recursive = true)
            with _ ->
                ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostControlApiTests() =
    [<Test>]
    member _.``start reuses one stable session and close returns the authoritative list``() =
        task {
            use fixture = new ApiFixture()

            use! health = fixture.Client.GetAsync("/api/v1/health")
            use healthDocument = responseDocument health

            Assert.Multiple(fun () ->
                Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                assertExactProperties
                    [ "pid"
                      "processStartTimeUtcTicks"
                      "hostVersion"
                      "controlApiVersion" ]
                    healthDocument.RootElement

                Assert.That(
                    healthDocument.RootElement.GetProperty("pid").GetInt32(),
                    Is.EqualTo(12_345)
                )

                Assert.That(
                    healthDocument.RootElement.GetProperty("controlApiVersion").GetInt32(),
                    Is.EqualTo(Protocol.ControlApiVersion)
                ))

            use! first =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = fixture.Worktree |}
                )

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            use firstDocument = responseDocument first
            assertRegistryResponseV1Shape firstDocument
            let firstIds = terminalIds firstDocument
            Assert.That(List.length firstIds, Is.EqualTo(1))
            let sessionId = firstIds.Head

            use! reused =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = Path.Combine(fixture.Worktree, ".") |}
                )

            use reusedDocument = responseDocument reused
            assertRegistryResponseV1Shape reusedDocument

            Assert.Multiple(fun () ->
                Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds reusedDocument, Is.EqualTo([ sessionId ]))
                Assert.That(
                    terminalEndpoints reusedDocument,
                    Is.EqualTo(
                        [ $"http://127.0.0.1:41000/_treemon/{sessionId}/{fixture.Token}/" ]
                    )
                )
                Assert.That(fixture.StartCount, Is.EqualTo(1)))

            use! listed = fixture.Client.GetAsync("/api/v1/terminals")
            use listDocument = responseDocument listed
            assertRegistryResponseV1Shape listDocument

            Assert.Multiple(fun () ->
                Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds listDocument, Is.EqualTo([ sessionId ])))

            use! unchanged =
                fixture.Client.DeleteAsync(
                    "/api/v1/terminals/00000000000000000000000000000000"
                )

            use unchangedDocument = responseDocument unchanged
            assertRegistryResponseV1Shape unchangedDocument
            Assert.That(terminalIds unchangedDocument, Is.EqualTo([ sessionId ]))

            use! closed =
                fixture.Client.DeleteAsync($"/api/v1/terminals/{sessionId}")

            use closeDocument = responseDocument closed
            assertRegistryResponseV1Shape closeDocument

            Assert.Multiple(fun () ->
                Assert.That(closed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds closeDocument, Is.Empty)
                Assert.That(fixture.CloseCount, Is.EqualTo(1))
                Assert.That(
                    closeDocument.RootElement.GetProperty("revision").GetInt64(),
                    Is.EqualTo(2L)
                ))
        }
        :> Task

    [<Test>]
    member _.``control validation rejects untrusted malformed and unknown requests before lifecycle``() =
        task {
            use fixture = new ApiFixture()
            use unauthenticated = new HttpClient(BaseAddress = Uri fixture.Endpoint)
            use! missingToken = unauthenticated.GetAsync("/api/v1/health")
            Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            use wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            wrongHost.Headers.Host <- $"localhost:{Uri(fixture.Endpoint).Port}"
            use! hostRejected = fixture.Client.SendAsync wrongHost
            Assert.That(hostRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use wrongOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            wrongOrigin.Headers.Add("Origin", "http://attacker.example")
            use! originRejected = fixture.Client.SendAsync wrongOrigin
            Assert.That(originRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use allowedOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            allowedOrigin.Headers.Add("Origin", "http://localhost:5174")
            use! originAccepted = fixture.Client.SendAsync allowedOrigin

            Assert.Multiple(fun () ->
                Assert.That(originAccepted.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(originAccepted.Headers.Server, Is.Empty))

            use malformed =
                new StringContent("{\"worktreePath\":", Encoding.UTF8, "application/json")

            use! malformedRejected =
                fixture.Client.PostAsync("/api/v1/terminals", malformed)

            Assert.That(malformedRejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            use! unknownRejected =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = fixture.UnknownDirectory |}
                )

            Assert.That(unknownRejected.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))

            use oversized =
                new StringContent(
                    String('x', int Protocol.MaximumRequestBodyBytes + 1),
                    Encoding.UTF8,
                    "application/json"
                )

            use! oversizedRejected =
                fixture.Client.PostAsync("/api/v1/terminals", oversized)

            Assert.That(
                oversizedRejected.StatusCode,
                Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)
            )

            use! extraEndpoint = fixture.Client.GetAsync("/api/v1/version")
            Assert.That(extraEndpoint.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            Assert.That(fixture.StartCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``shutdown is authenticated and stops the control host``() =
        task {
            use fixture = new ApiFixture()
            use emptyBody = new ByteArrayContent(Array.empty)
            use! response = fixture.Client.PostAsync("/api/v1/shutdown", emptyBody)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))

            let shutdown = ControlApi.waitForShutdown fixture.Running
            let! completed = Task.WhenAny(shutdown, Task.Delay 5_000)
            Assert.That(completed, Is.SameAs(shutdown))
        }
        :> Task

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostDataPlaneTests() =
    let frame (value: string) = Encoding.UTF8.GetBytes value

    [<Test>]
    member _.``one upstream survives browser replacement and attachment loss``() =
        let starts = ConcurrentQueue<string>()
        let closes = ConcurrentQueue<string>()
        let planes = ConcurrentQueue<TerminalDataPlane>()

        let starter sessionId _ =
            async {
                starts.Enqueue sessionId

                return
                    Ok
                        { ProcessId = 21_000
                          ProcessStartTimeUtcTicks = 31_000L
                          TtydPort = 41_000
                          HasExited = fun () -> false
                          Close = fun () -> closes.Enqueue sessionId }
            }

        let dataPlaneStarter sessionId _ =
            async {
                let upstream = new TestWebSocket()

                let plane =
                    TerminalDataPlane.createCore
                        Protocol.MaximumReplayBytes
                        upstream
                        ignore

                let running =
                    { plane with
                        AttachmentEndpoint =
                            $"http://127.0.0.1:42000/_treemon/{sessionId}/test-token/" }

                planes.Enqueue running
                return Ok running
            }

        let registry = TerminalRegistry.create starter dataPlaneStarter

        let worktree =
            CanonicalWorktree.create
                (Path.GetFullPath(Path.Combine(Path.GetTempPath(), "data-plane-worktree")))
                "data-plane-worktree"

        try
            TerminalRegistry.start registry worktree
            |> Async.RunSynchronously
            |> requireOk
            |> ignore

            TerminalRegistry.start registry worktree
            |> Async.RunSynchronously
            |> requireOk
            |> ignore

            let plane = planes.ToArray() |> Array.exactlyOne
            let first = new TestWebSocket()
            let firstId =
                plane.AttachSocket first
                |> Async.RunSynchronously
                |> requireSome "first browser was not attached"

            plane.AcceptBrowserFrame
                firstId
                (frame """{"AuthToken":"","columns":100,"rows":40}""")
            |> Async.RunSynchronously
            |> requireOk

            let second = new TestWebSocket()
            let secondId =
                plane.AttachSocket second
                |> Async.RunSynchronously
                |> requireSome "second browser was not attached"
            plane.DetachSocket firstId |> Async.RunSynchronously
            plane.DetachSocket secondId |> Async.RunSynchronously

            let snapshot =
                TerminalRegistry.list registry
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(starts.Count, Is.EqualTo(1), "ttyd was started more than once")
                Assert.That(planes.Count, Is.EqualTo(1), "more than one upstream was created")
                Assert.That(
                    first.CloseDescription,
                    Is.EqualTo(Some "Replaced by a new attachment")
                )

                Assert.That(List.length snapshot.Terminals, Is.EqualTo(1))
                Assert.That(closes.Count, Is.Zero, "attachment loss closed ttyd"))
        finally
            TerminalRegistry.shutdown registry
            |> Async.RunSynchronously

    [<Test>]
    member _.``new attachment receives bounded replay and ttyd receives its resize``() =
        let upstream = new TestWebSocket()

        let plane =
            TerminalDataPlane.createCore 14 upstream ignore

        try
            [ "0first"; "0second"; "0third" ]
            |> List.iter (fun value ->
                plane.AcceptUpstreamFrame(frame value)
                |> Async.RunSynchronously)

            let browser = new TestWebSocket()
            let attachmentId =
                plane.AttachSocket browser
                |> Async.RunSynchronously
                |> requireSome "browser was not attached"

            plane.AcceptBrowserFrame
                attachmentId
                (frame """{"AuthToken":"","columns":220,"rows":70}""")
            |> Async.RunSynchronously
            |> requireOk

            let browserFrames =
                browser.Sent
                |> List.map Encoding.UTF8.GetString

            let resize =
                upstream.Sent
                |> List.exactlyOne
                |> TerminalProtocol.parseResizeFrame
                |> requireOk

            Assert.Multiple(fun () ->
                Assert.That(browserFrames, Is.EqualTo([ "0second"; "0third" ]))
                Assert.That(resize.Columns, Is.EqualTo(220))
                Assert.That(resize.Rows, Is.EqualTo(70)))
        finally
            plane.Stop() |> Async.RunSynchronously

    [<Test>]
    member _.``resume replays paused output and restores the latest browser resize``() =
        let upstream = new TestWebSocket()
        let plane = TerminalDataPlane.createCore 1_024 upstream ignore

        try
            let browser = new TestWebSocket()

            let attachmentId =
                plane.AttachSocket browser
                |> Async.RunSynchronously
                |> requireSome "browser was not attached"

            plane.AcceptBrowserFrame
                attachmentId
                (frame """{"AuthToken":"","columns":100,"rows":40}""")
            |> Async.RunSynchronously
            |> requireOk

            plane.AcceptBrowserFrame
                attachmentId
                (frame """1{"columns":160,"rows":55}""")
            |> Async.RunSynchronously
            |> requireOk

            plane.AcceptBrowserFrame attachmentId (frame "2")
            |> Async.RunSynchronously
            |> requireOk

            [ "0paused-first"; "0paused-second" ]
            |> List.iter (fun value ->
                plane.AcceptUpstreamFrame(frame value)
                |> Async.RunSynchronously)

            Assert.That(browser.Sent, Is.Empty, "paused output was sent live")

            plane.AcceptBrowserFrame attachmentId (frame "3")
            |> Async.RunSynchronously
            |> requireOk

            let browserFrames =
                browser.Sent
                |> List.map Encoding.UTF8.GetString

            let resizeFrames =
                upstream.Sent
                |> List.map (TerminalProtocol.parseResizeFrame >> requireOk)
                |> List.map (fun size -> size.Columns, size.Rows)

            Assert.Multiple(fun () ->
                Assert.That(
                    browserFrames,
                    Is.EqualTo([ "0paused-first"; "0paused-second" ])
                )

                Assert.That(
                    resizeFrames,
                    Is.EqualTo([ (100, 40); (160, 55); (160, 55) ])
                ))
        finally
            plane.Stop() |> Async.RunSynchronously

    [<Test>]
    member _.``replay drops output older than its byte bound``() =
        let replay =
            [ "0first"; "0second"; "0third" ]
            |> List.map frame
            |> List.fold (fun state data -> ReplayBuffer.append 14 data state) ReplayBuffer.empty

        let retained =
            replay
            |> ReplayBuffer.frames
            |> List.map (fun replayFrame ->
                Encoding.UTF8.GetString replayFrame.Data)

        Assert.Multiple(fun () ->
            Assert.That(retained, Is.EqualTo([ "0second"; "0third" ]))
            Assert.That(
                replay
                |> ReplayBuffer.frames
                |> List.sumBy _.Data.Length,
                Is.EqualTo(13)
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostProxyTests() =
    [<Test>]
    member _.``attachment endpoint rejects invalid bearer origin and oversized requests``() =
        let upstream = new TestWebSocket()
        let processCloses = ConcurrentQueue<unit>()
        let connectorCalls = ConcurrentQueue<int>()
        let token = "shared-control-bearer"
        let dashboardOrigin = "http://localhost:5174"
        let allowedOrigins =
            [ dashboardOrigin
              "http://127.0.0.1:5174" ]

        let expectedFrameAncestors =
            "frame-ancestors " + String.concat " " allowedOrigins

        let terminalProcess =
            { ProcessId = 22_000
              ProcessStartTimeUtcTicks = 32_000L
              TtydPort = 1
              HasExited = fun () -> false
              Close = fun () -> processCloses.Enqueue() }

        let connector port =
            connectorCalls.Enqueue port
            async.Return(Ok(upstream :> System.Net.WebSockets.WebSocket))

        let plane =
            TerminalProxy.startWithConnector
                connector
                allowedOrigins
                token
                "security-session"
                terminalProcess
            |> Async.RunSynchronously
            |> requireOk

        try
            use client = new HttpClient()
            let endpoint = Uri plane.AttachmentEndpoint
            let authority = endpoint.GetLeftPart(UriPartial.Authority)
            let cleanEndpoint = Uri($"{authority}/")
            let wrongToken = Uri($"{authority}/_treemon/security-session/wrong/")

            use missingToken =
                client.GetAsync(cleanEndpoint)
                |> getTask

            use invalidToken =
                client.GetAsync(wrongToken)
                |> getTask

            use missingOrigin =
                client.GetAsync(endpoint)
                |> getTask

            use allowedOriginRequest =
                new HttpRequestMessage(HttpMethod.Get, endpoint)

            allowedOriginRequest.Headers.Add("Origin", dashboardOrigin)

            use allowedOrigin =
                client.SendAsync allowedOriginRequest
                |> getTask

            use wrongOriginRequest =
                new HttpRequestMessage(HttpMethod.Get, endpoint)

            wrongOriginRequest.Headers.Add("Origin", "http://attacker.example")

            use wrongOrigin =
                client.SendAsync wrongOriginRequest
                |> getTask

            use oversizedRequest =
                new HttpRequestMessage(HttpMethod.Get, cleanEndpoint)

            oversizedRequest.Headers.Authorization <-
                AuthenticationHeaderValue("Bearer", token)

            oversizedRequest.Content <-
                new StringContent(
                    String('x', int Protocol.MaximumRequestBodyBytes + 1),
                    Encoding.UTF8,
                    "text/plain"
                )

            use oversized =
                client.SendAsync oversizedRequest
                |> getTask

            Assert.Multiple(fun () ->
                Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                Assert.That(invalidToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
                Assert.That(missingOrigin.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway))
                Assert.That(missingOrigin.Headers.Server, Is.Empty)
                Assert.That(allowedOrigin.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway))
                Assert.That(wrongOrigin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

                Assert.That(
                    responseHeader "Content-Security-Policy" missingOrigin,
                    Is.EqualTo(expectedFrameAncestors)
                )

                Assert.That(
                    oversized.StatusCode,
                    Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)
                )

                Assert.That(
                    responseHeader "Content-Security-Policy" allowedOrigin,
                    Is.EqualTo(expectedFrameAncestors)
                )

                Assert.That(
                    responseHeader "Content-Security-Policy" wrongOrigin,
                    Is.EqualTo(expectedFrameAncestors)
                )

                Assert.That(connectorCalls.Count, Is.EqualTo(1))
                Assert.That(processCloses.Count, Is.Zero))
        finally
            plane.Stop() |> Async.RunSynchronously

    [<Test>]
    member _.``attachment response denies framing when no dashboard origin is configured``() =
        let upstream = new TestWebSocket()

        let terminalProcess =
            { ProcessId = 22_001
              ProcessStartTimeUtcTicks = 32_001L
              TtydPort = 1
              HasExited = fun () -> false
              Close = ignore }

        let connector _ =
            async.Return(Ok(upstream :> System.Net.WebSockets.WebSocket))

        let plane =
            TerminalProxy.startWithConnector
                connector
                []
                "shared-control-bearer"
                "no-origin-session"
                terminalProcess
            |> Async.RunSynchronously
            |> requireOk

        try
            use client = new HttpClient()

            use response =
                client.GetAsync(plane.AttachmentEndpoint)
                |> getTask

            Assert.Multiple(fun () ->
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway))

                Assert.That(
                    responseHeader "Content-Security-Policy" response,
                    Is.EqualTo("frame-ancestors 'none'")
                ))
        finally
            plane.Stop() |> Async.RunSynchronously

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostSecurityTests() =
    [<Test>]
    member _.``non-loopback peer is rejected even with valid host origin and token``() =
        let metadata =
            { RemoteAddress = Some(IPAddress.Parse "192.168.1.10")
              LocalAddress = Some IPAddress.Loopback
              LocalPort = 32_123
              HostHeaders = [ "127.0.0.1:32123" ]
              OriginHeaders = [ "http://127.0.0.1:32123" ]
              AuthorizationHeaders = [ "Bearer expected-token" ]
              ContentLength = None }

        match RequestSecurity.validate [] "expected-token" metadata with
        | Error RequestRejection.Forbidden -> ()
        | result -> Assert.Fail($"Expected non-loopback rejection, got {result}")

    [<Test>]
    member _.``terminal launch specification injects the stable terminal session ID``() =
        let worktreePath =
            Path.Combine(Path.GetTempPath(), "fixture-worktree")
            |> Path.GetFullPath

        let worktree =
            CanonicalWorktree.create
                worktreePath
                "fixture-key"

        let specification =
            TerminalLauncher.startSpecification
                { TtydExecutable = "ttyd.exe"
                  ShellCommand = "pwsh"
                  StartupTimeout = TimeSpan.FromSeconds 1.0 }
                "stable-session-id"
                worktree
                31_234

        Assert.Multiple(fun () ->
            Assert.That(
                specification.Environment,
                Does.Contain(("TREEMON_TERMINAL_SESSION_ID", "stable-session-id"))
            )

            Assert.That(
                specification.Environment,
                Does.Contain(("TREEMON_TERMINAL_WORKTREE", worktreePath))
            )

            Assert.That(specification.Arguments, Does.Contain("127.0.0.1"))
            Assert.That(specification.Arguments, Does.Contain(worktreePath))
            Assert.That(
                specification.Arguments,
                Does.Contain(
                    "Set-Location -LiteralPath $env:TREEMON_TERMINAL_WORKTREE"
                )
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostManifestTests() =
    [<Test>]
    member _.``manifest monitor continues across multiple staged version polls``() =
        withTempDir "terminal-host-manifest-polls" (fun root ->
            let layout = TerminalHostLayout.forStateDirectory root

            let identity =
                { Pid = 12_345
                  ProcessStartTimeUtcTicks = 638_900_000_000_000_000L
                  Endpoint = "http://127.0.0.1:32123"
                  HostVersion = "1.2.3"
                  ControlApiVersion = 1 }

            let stage version =
                let directory =
                    TerminalHostLayout.versionDirectory layout version

                Directory.CreateDirectory directory |> ignore

                layout.RequiredBundleFileNames
                |> List.iter (fun name ->
                    File.WriteAllText(
                        Path.Combine(directory, name),
                        "fixture"
                    ))

                directory

            let observedVersions = ConcurrentQueue<string option>()
            // Mutation models successive timer callbacks at this injected test boundary.
            let mutable poll = 0

            let waitForNextPoll _ =
                async {
                    poll <- poll + 1

                    match poll with
                    | 1 ->
                        stage "2.4.6" |> ignore
                        return true
                    | 2 ->
                        observedVersions.Enqueue(
                            Manifest.readStagedExecutableVersion layout
                        )

                        Directory.Delete(
                            TerminalHostLayout.versionDirectory layout "2.4.6",
                            recursive = true
                        )

                        stage "2.5.0" |> ignore
                        return true
                    | 3 ->
                        observedVersions.Enqueue(
                            Manifest.readStagedExecutableVersion layout
                        )

                        return false
                    | _ ->
                        return
                            failwith
                                $"The manifest monitor performed unexpected poll {poll}"
                }

            Manifest.monitorWithDelay
                waitForNextPoll
                root
                layout
                identity
                "secret-token"
                None
                CancellationToken.None
            |> Async.StartAsTask
            |> getTask

            use document =
                JsonDocument.Parse(File.ReadAllBytes(Manifest.path root))

            Assert.Multiple(fun () ->
                Assert.That(poll, Is.EqualTo(3))

                Assert.That(
                    observedVersions.ToArray(),
                    Is.EqualTo(
                        [| Some "2.4.6"
                           Some "2.5.0" |]
                    )
                )

                Assert.That(
                    document.RootElement
                        .GetProperty("stagedExecutableVersion")
                        .GetString(),
                    Is.EqualTo("2.5.0")
                )))

    [<Test>]
    member _.``manifest contains only discovery identity token versions and staged executable``() =
        withTempDir "terminal-host-manifest" (fun root ->
            let layout = TerminalHostLayout.forStateDirectory root
            let staging = layout.StagingDirectory
            let staged = Path.Combine(staging, "2.4.6")
            Directory.CreateDirectory staged |> ignore

            layout.RequiredBundleFileNames
            |> List.iter (fun name ->
                File.WriteAllText(Path.Combine(staged, name), "fixture"))

            let identity =
                { Pid = 12_345
                  ProcessStartTimeUtcTicks = 638_900_000_000_000_000L
                  Endpoint = "http://127.0.0.1:32123"
                  HostVersion = "1.2.3"
                  ControlApiVersion = 1 }

            let stagedVersion =
                Manifest.readStagedExecutableVersion layout

            Manifest.write
                root
                { Identity = identity
                  BearerToken = "secret-token"
                  StagedExecutableVersion = stagedVersion }
            |> requireOk

            use document = JsonDocument.Parse(File.ReadAllBytes(Manifest.path root))

            let properties =
                document.RootElement.EnumerateObject()
                |> Seq.map _.Name
                |> Set.ofSeq

            Assert.Multiple(fun () ->
                Assert.That(
                    properties,
                    Is.EqualTo(
                        set
                            [ "pid"
                              "processStartTimeUtcTicks"
                              "endpoint"
                              "bearerToken"
                              "hostVersion"
                              "controlApiVersion"
                              "stagedExecutableVersion" ]
                    )
                )

                Assert.That(
                    document.RootElement.GetProperty("stagedExecutableVersion").GetString(),
                    Is.EqualTo("2.4.6")
                )

                Assert.That(
                    document.RootElement.GetProperty("bearerToken").GetString(),
                    Is.EqualTo("secret-token")
                ))

            use cancellation = new CancellationTokenSource()

            let monitor =
                Manifest.monitor
                    root
                    layout
                    identity
                    "secret-token"
                    stagedVersion
                    cancellation.Token

            Directory.Delete(staged, recursive = true)

            let stagedVersionRemoved =
                waitUntil (TimeSpan.FromSeconds 3.0) (fun () ->
                    try
                        use updated =
                            JsonDocument.Parse(File.ReadAllBytes(Manifest.path root))

                        updated.RootElement.EnumerateObject()
                        |> Seq.exists (fun property ->
                            property.Name = "stagedExecutableVersion")
                        |> not
                    with _ ->
                        false)

            cancellation.Cancel()
            monitor |> getTask
            Assert.That(stagedVersionRemoved, Is.True)

            Manifest.removeIfOwned root identity
            Assert.That(File.Exists(Manifest.path root), Is.False))

    [<Test>]
    member _.``layout applies one non-default state root and one version grammar``() =
        withTempDir "terminal-host-layout" (fun root ->
            let stateDirectory = Path.Combine(root, "custom-state")
            let layout = TerminalHostLayout.forStateDirectory stateDirectory

            let validVersions =
                [ "1"; "1.2.3"; "host-build_42"; String.replicate 128 "a" ]

            let invalidVersions =
                [ ""; " "; "1.2.3+metadata"; "../escape"; "nested/version"; "valid\n"
                  String.replicate 129 "a" ]

            Assert.Multiple(fun () ->
                Assert.That(layout.StateDirectory, Is.EqualTo(Path.GetFullPath stateDirectory))
                Assert.That(
                    layout.StagingDirectory,
                    Is.EqualTo(Path.Combine(Path.GetFullPath stateDirectory, "staged"))
                )
                Assert.That(
                    layout.ManifestPath,
                    Is.EqualTo(Path.Combine(Path.GetFullPath stateDirectory, "host.json"))
                )

                validVersions
                |> List.iter (fun version ->
                    Assert.That(
                        TerminalHostLayout.isValidVersionDirectoryName version,
                        Is.True,
                        version
                    ))

                invalidVersions
                |> List.iter (fun version ->
                    Assert.That(
                        TerminalHostLayout.isValidVersionDirectoryName version,
                        Is.False,
                        version
                    ))))

    [<Test>]
    member _.``staged discovery ignores invalid and incomplete bundles``() =
        withTempDir "terminal-host-staged-bundles" (fun root ->
            let layout = TerminalHostLayout.forStateDirectory root

            let createBundle version excludedFiles lastWrite =
                let directory =
                    TerminalHostLayout.versionDirectory layout version

                Directory.CreateDirectory directory |> ignore

                layout.RequiredBundleFileNames
                |> List.filter (fun name ->
                    excludedFiles |> Set.contains name |> not)
                |> List.iter (fun name ->
                    File.WriteAllText(Path.Combine(directory, name), "fixture"))

                Directory.SetLastWriteTimeUtc(directory, lastWrite)

            let baseline = DateTime.UtcNow.AddMinutes(-10.0)

            createBundle "2.0.0-valid" Set.empty baseline

            createBundle
                "3.0.0-missing-ttyd"
                (Set.singleton layout.TtydExecutableName)
                (baseline.AddMinutes 1.0)

            createBundle
                "4.0.0-missing-host"
                (Set.singleton layout.HostExecutableName)
                (baseline.AddMinutes 2.0)

            createBundle
                "5.0.0+invalid"
                Set.empty
                (baseline.AddMinutes 3.0)

            Assert.That(
                Manifest.readStagedExecutableVersion layout,
                Is.EqualTo(Some "2.0.0-valid")
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("TerminalHost")>]
[<Platform("Win")>]
type TerminalHostJobObjectTests() =
    let powershell = executableOnPath "pwsh.exe"

    [<Test>]
    member _.``closing one retained Job Object kills its exact ttyd process tree``() =
        withTempDir "terminal-host-job-close" (fun root ->
            let pidFile = Path.Combine(root, "child.pid")
            let descendantPidFile = Path.Combine(root, "descendant.pid")
            let environmentFile = Path.Combine(root, "session.txt")
            let sessionId = $"terminal-{Guid.NewGuid():N}"

            let owned =
                JobProcess.start
                    { Executable = powershell
                      Arguments =
                        [ "-NoLogo"
                          "-NoProfile"
                          "-NonInteractive"
                          "-Command"
                          "$descendant = Start-Process -FilePath $env:TM_POWERSHELL -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 300') -PassThru; $PID | Set-Content -LiteralPath $env:TM_PID_FILE; $descendant.Id | Set-Content -LiteralPath $env:TM_DESCENDANT_PID_FILE; $env:TREEMON_TERMINAL_SESSION_ID | Set-Content -LiteralPath $env:TM_SESSION_FILE; Start-Sleep -Seconds 300" ]
                      WorkingDirectory = root
                      Environment =
                        [ "TM_POWERSHELL", powershell
                          "TM_PID_FILE", pidFile
                          "TM_DESCENDANT_PID_FILE", descendantPidFile
                          "TM_SESSION_FILE", environmentFile
                          "TREEMON_TERMINAL_SESSION_ID", sessionId ] }
                |> requireOk

            try
                Assert.That(
                    waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                        File.Exists pidFile
                        && File.Exists descendantPidFile
                        && File.Exists environmentFile),
                    Is.True,
                    "owned child did not start"
                )

                let childPid = File.ReadAllText(pidFile).Trim() |> int
                let descendantPid =
                    File.ReadAllText(descendantPidFile).Trim() |> int

                use child = Process.GetProcessById childPid
                use descendant = Process.GetProcessById descendantPid

                Assert.Multiple(fun () ->
                    Assert.That(childPid, Is.EqualTo(JobProcess.processId owned))
                    Assert.That(File.ReadAllText(environmentFile).Trim(), Is.EqualTo(sessionId)))

                JobProcess.close owned

                Assert.Multiple(fun () ->
                    Assert.That(JobProcess.hasExited owned, Is.True)

                    Assert.That(child.WaitForExit 5_000, Is.True, "Job Object close did not kill ttyd")

                    Assert.That(
                        descendant.WaitForExit 5_000,
                        Is.True,
                        "Job Object close did not kill the ttyd process tree"
                    ))
            finally
                JobProcess.close owned
                killExactPidFromFile descendantPidFile)

    [<Test>]
    member _.``terminating the host process closes the Job Object and kills ttyd``() =
        withTempDir "terminal-host-job-exit" (fun root ->
            let readyFile = Path.Combine(root, "ready.pid")
            let errorFile = Path.Combine(root, "error.txt")
            let scriptPath = Path.Combine(root, "owner.fsx")
            let assemblyPath = typeof<JobProcessStart>.Assembly.Location

            let verbatim (value: string) =
                value.Replace("\"", "\"\"")

            let script =
                $"""#r @"{verbatim assemblyPath}"
open System
open System.IO
open System.Threading
open TerminalHost

let specification: JobProcessStart =
    {{ Executable = @"{verbatim powershell}"
      Arguments = [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; "Start-Sleep -Seconds 300" ]
      WorkingDirectory = @"{verbatim root}"
      Environment = [] }}

match JobProcess.start specification with
| Error error ->
    File.WriteAllText(@"{verbatim errorFile}", error)
    Environment.Exit 2
| Ok owned ->
    File.WriteAllText(@"{verbatim readyFile}", string (JobProcess.processId owned))
    let rec keepAlive () =
        Thread.Sleep 250
        GC.KeepAlive owned
        keepAlive ()
    keepAlive ()
"""

            File.WriteAllText(scriptPath, script)

            let startInfo =
                ProcessStartInfo(
                    FileName = "dotnet",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            [ "fsi"; "--nologo"; "--exec"; scriptPath ]
            |> List.iter startInfo.ArgumentList.Add

            use owner = Process.Start startInfo

            try
                Assert.That(
                    waitUntil (TimeSpan.FromSeconds 15.0) (fun () ->
                        File.Exists readyFile || File.Exists errorFile || owner.HasExited),
                    Is.True,
                    "fixture owner did not publish its child PID"
                )

                if File.Exists errorFile then
                    Assert.Fail(File.ReadAllText errorFile)

                if owner.HasExited then
                    Assert.Fail(owner.StandardError.ReadToEnd())

                let childPid = File.ReadAllText(readyFile).Trim() |> int
                use child = Process.GetProcessById childPid

                owner.Kill()
                Assert.That(owner.WaitForExit 5_000, Is.True, "fixture owner did not exit")

                Assert.That(
                    child.WaitForExit 5_000,
                    Is.True,
                    "ttyd survived the process that owned its Job Object"
                )
            finally
                if not owner.HasExited then
                    owner.Kill()
                    owner.WaitForExit()

                killExactPidFromFile readyFile)
