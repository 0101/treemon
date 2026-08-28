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

let private runWithin (timeout: TimeSpan) workflow =
    workflow
    |> Async.StartAsTask
    |> _.WaitAsync(timeout)
    |> getTask

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
    |> Seq.map _.GetProperty("sessionId").GetString()
    |> Seq.choose Option.ofObj
    |> Seq.toList

let private terminalEndpoints (document: JsonDocument) =
    document.RootElement.GetProperty("terminals").EnumerateArray()
    |> Seq.map _.GetProperty("attachmentEndpoint").GetString()
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

let private assertRegistryResponseV2Shape (document: JsonDocument) =
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

let private inertDataPlane endpoint =
    { AttachmentEndpoint = endpoint
      AttachSocket = fun _ -> async.Return None
      AcceptBrowserFrame = fun _ _ -> async.Return(Ok())
      DetachSocket = fun _ -> async.Return()
      AcceptUpstreamFrame = fun _ -> async.Return()
      UpstreamEnded = fun () -> async.Return()
      Stop = fun () -> async.Return() }

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

    let dataPlaneStarter sessionId _ _ =
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
type TerminalRuntimeBudgetTests() =
    let rec findRepositoryRoot directory =
        let candidate = DirectoryInfo directory

        if File.Exists(Path.Combine(candidate.FullName, "treemon.slnx")) then
            candidate.FullName
        elif isNull candidate.Parent then
            failwith "Could not locate the Treemon repository root"
        else
            findRepositoryRoot candidate.Parent.FullName

    let sourceFiles root relativePath pattern =
        Directory.EnumerateFiles(
            Path.Combine(root, relativePath),
            pattern,
            SearchOption.AllDirectories
        )
        |> Seq.filter (fun path ->
            let segments =
                Path.GetRelativePath(root, path)
                    .Split(
                        [| Path.DirectorySeparatorChar
                           Path.AltDirectorySeparatorChar |],
                        StringSplitOptions.RemoveEmptyEntries
                    )

            segments |> Array.exists (fun segment -> segment = "bin" || segment = "obj") |> not)

    let runtimeScripts root =
        Directory.EnumerateFiles(Path.Combine(root, "scripts"), "*", SearchOption.TopDirectoryOnly)
        |> Seq.filter (fun path ->
            let name = Path.GetFileName path
            name.StartsWith("durable-terminal-", StringComparison.Ordinal)
            || name.StartsWith("terminal-", StringComparison.Ordinal))

    [<Test>]
    member _.``complete terminal runtime stays within its simplicity budget``() =
        let root = findRepositoryRoot AppContext.BaseDirectory

        let files =
            seq {
                yield! sourceFiles root "src/TerminalHost" "*.fs"
                yield! sourceFiles root "src/TerminalHostLayout" "*.fs"
                yield! sourceFiles root "src/Server" "TerminalHost*.fs"
                yield Path.Combine(root, "src/Server/TerminalSessionActivity.fs")
                yield Path.Combine(root, "src/Server/EmbeddedTerminal.fs")
                yield! runtimeScripts root
            }
            |> Seq.distinct
            |> Seq.map (fun path ->
                Path.GetRelativePath(root, path),
                File.ReadLines path |> Seq.filter (String.IsNullOrWhiteSpace >> not) |> Seq.length)
            |> Seq.sortBy fst
            |> Seq.toList

        let total = files |> List.sumBy snd
        let detail =
            files
            |> List.map (fun (path, lines) -> $"{path}: {lines}")
            |> String.concat Environment.NewLine

        Assert.That(total, Is.LessThanOrEqualTo(4_000), $"Terminal runtime has {total} nonblank lines:{Environment.NewLine}{detail}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalRegistryResilienceTests() =
    let timeout = TimeSpan.FromSeconds 2.0

    let worktree name =
        let path =
            Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}")
            )

        CanonicalWorktree.create path

    [<Test>]
    member _.``upstream exit racing prune has one cleanup owner and ignores stale notices``() =
        use pruneArmed = new ManualResetEventSlim()
        use pruneEntered = new ManualResetEventSlim()
        use releasePrune = new ManualResetEventSlim()
        let closes = ConcurrentQueue<string>()
        let dataPlanes = ConcurrentQueue<TerminalDataPlane>()
        let upstreamExitNotices = ConcurrentQueue<(unit -> unit)>()

        let starter sessionId _ =
            async {
                return
                    Ok
                        { ProcessId = 23_000 + closes.Count
                          ProcessStartTimeUtcTicks = 33_000L + int64 closes.Count
                          TtydPort = 43_000 + closes.Count
                          HasExited =
                            fun () ->
                                if pruneArmed.IsSet then
                                    pruneEntered.Set()

                                    if not (releasePrune.Wait timeout) then
                                        failwith "Timed out coordinating registry prune"

                                    true
                                else
                                    false
                          Close = fun () -> closes.Enqueue sessionId }
            }

        let dataPlaneStarter sessionId _ notifyUpstreamExited =
            async {
                let core =
                    TerminalDataPlane.createCore
                        Protocol.MaximumReplayBytes
                        (new TestWebSocket())
                        notifyUpstreamExited

                let running =
                    { core with
                        AttachmentEndpoint =
                            $"http://127.0.0.1:43000/_treemon/{sessionId}/test-token/" }

                dataPlanes.Enqueue running
                upstreamExitNotices.Enqueue notifyUpstreamExited
                return Ok running
            }

        let registry = TerminalRegistry.create starter dataPlaneStarter
        let canonicalWorktree = worktree "terminal-registry-prune-race"

        try
            let first =
                TerminalRegistry.start registry canonicalWorktree
                |> runWithin timeout
                |> requireOk

            let firstSessionId =
                first.Terminals
                |> List.exactlyOne
                |> _.SessionId

            let firstUpstreamExit =
                upstreamExitNotices.ToArray()
                |> Array.exactlyOne

            let firstDataPlane =
                dataPlanes.ToArray()
                |> Array.exactlyOne

            pruneArmed.Set()
            let pendingList =
                TerminalRegistry.list registry
                |> Async.StartAsTask

            Assert.That(
                pruneEntered.Wait timeout,
                Is.True,
                "registry did not enter the deterministic prune window"
            )

            firstDataPlane.UpstreamEnded()
            |> runWithin timeout

            releasePrune.Set()

            let pruned =
                pendingList.WaitAsync(timeout)
                |> getTask

            let afterExitNotice =
                TerminalRegistry.list registry
                |> runWithin timeout

            Assert.Multiple(fun () ->
                Assert.That(pruned.Terminals, Is.Empty)
                Assert.That(afterExitNotice.Terminals, Is.Empty)
                Assert.That(closes.ToArray(), Is.EqualTo([| firstSessionId |])))

            pruneArmed.Reset()

            let restarted =
                TerminalRegistry.start registry canonicalWorktree
                |> runWithin timeout
                |> requireOk

            let restartedSessionId =
                restarted.Terminals
                |> List.exactlyOne
                |> _.SessionId

            firstUpstreamExit ()

            let afterStaleNotice =
                TerminalRegistry.list registry
                |> runWithin timeout

            Assert.Multiple(fun () ->
                Assert.That(restartedSessionId, Is.Not.EqualTo(firstSessionId))

                Assert.That(
                    afterStaleNotice.Terminals |> List.map _.SessionId,
                    Is.EqualTo([ restartedSessionId ])
                )

                Assert.That(closes.Count, Is.EqualTo(1)))

            let closed =
                TerminalRegistry.close registry restartedSessionId
                |> runWithin timeout

            Assert.Multiple(fun () ->
                Assert.That(closed.Terminals, Is.Empty)
                Assert.That(closes.Count, Is.EqualTo(2)))

            TerminalRegistry.shutdown registry
            |> runWithin timeout
        finally
            releasePrune.Set()

            try
                TerminalRegistry.shutdown registry
                |> runWithin timeout
            with _ ->
                ()

    [<Test>]
    member _.``cleanup failures do not wedge list close or shutdown``() =
        use exited = new ManualResetEventSlim()
        use cleanupFails = new ManualResetEventSlim(true)
        let closes = ConcurrentQueue<unit>()

        let starter _ _ =
            async {
                return
                    Ok
                        { ProcessId = 23_100
                          ProcessStartTimeUtcTicks = 33_100L
                          TtydPort = 43_100
                          HasExited = fun () -> exited.IsSet
                          Close =
                            fun () ->
                                closes.Enqueue()

                                if cleanupFails.IsSet then
                                    raise (
                                        InvalidOperationException(
                                            "deterministic cleanup failure"
                                        )
                                    ) }
            }

        let dataPlaneStarter sessionId _ _ =
            async {
                return
                    Ok(
                        inertDataPlane
                            $"http://127.0.0.1:43100/_treemon/{sessionId}/test-token/"
                    )
            }

        let registry =
            TerminalRegistry.create
                starter
                dataPlaneStarter

        try
            let started =
                TerminalRegistry.start
                    registry
                    (worktree "terminal-registry-cleanup-failure")
                |> runWithin timeout
                |> requireOk

            let sessionId =
                started.Terminals
                |> List.exactlyOne
                |> _.SessionId

            exited.Set()

            let listed =
                TerminalRegistry.list registry
                |> runWithin timeout

            let closed =
                TerminalRegistry.close registry sessionId
                |> runWithin timeout

            TerminalRegistry.shutdown registry
            |> runWithin timeout

            Assert.Multiple(fun () ->
                Assert.That(
                    listed.Terminals |> List.map _.SessionId,
                    Is.EqualTo([ sessionId ])
                )

                Assert.That(
                    closed.Terminals |> List.map _.SessionId,
                    Is.EqualTo([ sessionId ])
                )

                Assert.That(
                    closes.Count,
                    Is.EqualTo(3),
                    "each message should reach cleanup and recover"
                ))
        finally
            cleanupFails.Reset()

            try
                TerminalRegistry.shutdown registry
                |> runWithin timeout
            with _ ->
                ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostControlApiTests() =
    [<Test>]
    member _.``each start creates a distinct session and close targets one terminal``() =
        task {
            use fixture = new ApiFixture()

            use! health = fixture.Client.GetAsync("/api/v2/health")
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
                    "/api/v2/terminals",
                    {| worktreePath = fixture.Worktree |}
                )

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            use firstDocument = responseDocument first
            assertRegistryResponseV2Shape firstDocument
            let firstIds = terminalIds firstDocument
            Assert.That(List.length firstIds, Is.EqualTo(1))
            let sessionId = firstIds.Head

            use! second =
                fixture.Client.PostAsJsonAsync(
                    "/api/v2/terminals",
                    {| worktreePath = Path.Combine(fixture.Worktree, ".") |}
                )

            use secondDocument = responseDocument second
            assertRegistryResponseV2Shape secondDocument
            let secondIds = terminalIds secondDocument
            let secondSessionId = secondIds[1]

            Assert.Multiple(fun () ->
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(secondIds.Length, Is.EqualTo(2))
                Assert.That(secondIds[0], Is.EqualTo(sessionId))
                Assert.That(secondSessionId, Is.Not.EqualTo(sessionId))
                Assert.That(
                    terminalEndpoints secondDocument,
                    Is.EqualTo(
                        [ $"http://127.0.0.1:41000/_treemon/{sessionId}/{fixture.Token}/"
                          $"http://127.0.0.1:41000/_treemon/{secondSessionId}/{fixture.Token}/" ]
                    )
                )
                Assert.That(fixture.StartCount, Is.EqualTo(2)))

            use! listed = fixture.Client.GetAsync("/api/v2/terminals")
            use listDocument = responseDocument listed
            assertRegistryResponseV2Shape listDocument

            Assert.Multiple(fun () ->
                Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds listDocument, Is.EqualTo(secondIds)))

            use! unchanged =
                fixture.Client.DeleteAsync(
                    "/api/v2/terminals/00000000000000000000000000000000"
                )

            use unchangedDocument = responseDocument unchanged
            assertRegistryResponseV2Shape unchangedDocument
            Assert.That(terminalIds unchangedDocument, Is.EqualTo(secondIds))

            use! closed =
                fixture.Client.DeleteAsync($"/api/v2/terminals/{sessionId}")

            use closeDocument = responseDocument closed
            assertRegistryResponseV2Shape closeDocument

            Assert.Multiple(fun () ->
                Assert.That(closed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(
                    terminalIds closeDocument,
                    Is.EqualTo([ secondSessionId ])
                )
                Assert.That(fixture.CloseCount, Is.EqualTo(1))
                Assert.That(
                    closeDocument.RootElement.GetProperty("revision").GetInt64(),
                    Is.EqualTo(3L)
                ))

            use! finalClose =
                fixture.Client.DeleteAsync(
                    $"/api/v2/terminals/{secondSessionId}"
                )

            use finalDocument = responseDocument finalClose
            Assert.Multiple(fun () ->
                Assert.That(terminalIds finalDocument, Is.Empty)
                Assert.That(fixture.CloseCount, Is.EqualTo(2))
                Assert.That(
                    finalDocument.RootElement.GetProperty("revision").GetInt64(),
                    Is.EqualTo(4L)
                ))
        }
        :> Task

    [<Test>]
    member _.``control validation rejects untrusted malformed and unknown requests before lifecycle``() =
        task {
            use fixture = new ApiFixture()
            use unauthenticated = new HttpClient(BaseAddress = Uri fixture.Endpoint)
            use! missingToken = unauthenticated.GetAsync("/api/v2/health")
            Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            use wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/v2/health")
            wrongHost.Headers.Host <- $"localhost:{Uri(fixture.Endpoint).Port}"
            use! hostRejected = fixture.Client.SendAsync wrongHost
            Assert.That(hostRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use wrongOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v2/health")
            wrongOrigin.Headers.Add("Origin", "http://attacker.example")
            use! originRejected = fixture.Client.SendAsync wrongOrigin
            Assert.That(originRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use allowedOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v2/health")
            allowedOrigin.Headers.Add("Origin", "http://localhost:5174")
            use! originAccepted = fixture.Client.SendAsync allowedOrigin

            Assert.Multiple(fun () ->
                Assert.That(originAccepted.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(originAccepted.Headers.Server, Is.Empty))

            use malformed =
                new StringContent("{\"worktreePath\":", Encoding.UTF8, "application/json")

            use! malformedRejected =
                fixture.Client.PostAsync("/api/v2/terminals", malformed)

            Assert.That(malformedRejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            use! unknownRejected =
                fixture.Client.PostAsJsonAsync(
                    "/api/v2/terminals",
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
                fixture.Client.PostAsync("/api/v2/terminals", oversized)

            Assert.That(
                oversizedRejected.StatusCode,
                Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)
            )

            use! extraEndpoint = fixture.Client.GetAsync("/api/v2/version")
            Assert.That(extraEndpoint.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            Assert.That(fixture.StartCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``shutdown is authenticated and stops the control host``() =
        task {
            use fixture = new ApiFixture()
            use emptyBody = new ByteArrayContent(Array.empty)
            use! response = fixture.Client.PostAsync("/api/v2/shutdown", emptyBody)
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
    member _.``message failure does not wedge the data plane``() =
        let upstream = new TestWebSocket()

        let plane =
            TerminalDataPlane.createCore
                0
                upstream
                ignore

        try
            plane.AcceptUpstreamFrame(frame "0deterministic failure")
            |> runWithin (TimeSpan.FromSeconds 2.0)

            plane.DetachSocket(Guid.NewGuid())
            |> runWithin (TimeSpan.FromSeconds 2.0)

            plane.Stop()
            |> runWithin (TimeSpan.FromSeconds 2.0)

            Assert.That(
                upstream.CloseDescription,
                Is.EqualTo(Some "Terminal session closed")
            )
        finally
            try
                plane.Stop()
                |> runWithin (TimeSpan.FromSeconds 2.0)
            with _ ->
                ()

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

        let dataPlaneStarter sessionId _ _ =
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

        try
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
    member _.``resume resets and reports output evicted beyond the one MiB replay boundary``() =
        let upstream = new TestWebSocket()

        let plane =
            TerminalDataPlane.createCore
                Protocol.MaximumReplayBytes
                upstream
                ignore

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

            plane.AcceptBrowserFrame attachmentId (frame "2")
            |> Async.RunSynchronously
            |> requireOk

            let retained =
                Array.append
                    [| byte '0' |]
                    (Array.create
                        (Protocol.MaximumReplayBytes - 1)
                        (byte 'x'))

            plane.AcceptUpstreamFrame(frame "0evicted")
            |> Async.RunSynchronously

            plane.AcceptUpstreamFrame retained
            |> Async.RunSynchronously

            Assert.That(browser.Sent, Is.Empty, "paused output was sent live")

            plane.AcceptBrowserFrame attachmentId (frame "3")
            |> Async.RunSynchronously
            |> requireOk

            let browserFrames = browser.Sent
            Assert.That(browserFrames |> List.length, Is.EqualTo(2))

            let gapNotice = browserFrames |> List.head |> Encoding.UTF8.GetString
            let survivingFrame = browserFrames |> List.last

            Assert.Multiple(fun () ->
                Assert.That(
                    gapNotice,
                    Does.StartWith("0\u001bc\u001b[2J\u001b[H")
                )

                Assert.That(gapNotice, Does.Contain("output was omitted"))

                Assert.That(
                    survivingFrame.Length = retained.Length
                    && Array.forall2 (=) survivingFrame retained,
                    Is.True,
                    "surviving replay bytes changed"
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

        match ReplayBuffer.framesFrom 0L replay with
        | ReplaySlice.Gap frames ->
            Assert.That(
                frames |> List.map (fun replayFrame -> Encoding.UTF8.GetString replayFrame.Data),
                Is.EqualTo([ "0second"; "0third" ])
            )
        | ReplaySlice.Complete _ ->
            Assert.Fail("an evicted replay prefix was reported as complete")

        match ReplayBuffer.framesFrom 1L replay with
        | ReplaySlice.Complete _ -> ()
        | ReplaySlice.Gap _ ->
            Assert.Fail("the oldest retained sequence was reported as a gap")

        match ReplayBuffer.framesFrom 3L replay with
        | ReplaySlice.Complete [] -> ()
        | ReplaySlice.Complete _ ->
            Assert.Fail("a replay tail with no new frames was not empty")
        | ReplaySlice.Gap _ ->
            Assert.Fail("a replay tail with no new frames was reported as a gap")

        match ReplayBuffer.framesFrom 0L ReplayBuffer.empty with
        | ReplaySlice.Complete [] -> ()
        | ReplaySlice.Complete _ ->
            Assert.Fail("a never-written replay buffer was not empty")
        | ReplaySlice.Gap _ ->
            Assert.Fail("a never-written replay buffer was reported as a gap")

let private terminalInputFrames (upstream: TestWebSocket) =
    upstream.Sent
    |> List.filter (fun frame ->
        frame.Length > 0
        && frame[0] = byte '0')

let private withCommandProxy action =
    let upstream = new TestWebSocket()
    let connector _ =
        async.Return(Ok(upstream :> System.Net.WebSockets.WebSocket))

    let plane =
        TerminalProxy.startWithConnector
            connector
            []
            "command-boundary-token"
            "command-boundary-session"
            1
            ignore
        |> Async.RunSynchronously
        |> requireOk

    try
        action upstream plane
    finally
        plane.Stop() |> Async.RunSynchronously

let private submitTerminalCommand (plane: TerminalDataPlane) command =
    Server.TerminalHostClient.sendTerminalCommandDefault
        plane.AttachmentEndpoint
        command
    |> Async.RunSynchronously

let private requireTerminalInputFrame (upstream: TestWebSocket) =
    let delivered =
        waitUntil
            (TimeSpan.FromSeconds 2.0)
            (fun () ->
                terminalInputFrames upstream
                |> List.isEmpty
                |> not)

    Assert.That(delivered, Is.True, "Command was not forwarded to the terminal upstream")

    match terminalInputFrames upstream with
    | [ frame ] -> frame
    | frames ->
        Assert.Fail($"Expected one terminal input frame, got {frames.Length}")
        Array.empty

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostProxyTests() =
    [<Test>]
    member _.``multiline launch prompts cross the real attachment as one control-free frame``() =
        let cases =
            [ ("AgentDoc",
               CanvasSessionPrompt.forAgentDoc
                   "Q:/code/demo"
                   "report.html")
              ("SystemView",
               Shared.CanvasPrompt.continueWorking
                   "Q:/code/demo"
                   "diff.html")
              ("create-worktree",
               Server.CodingToolStatus.skillInvocation
                   None
                   "bd-execute"
                   "Implement the first line.\r\nPreserve the second line.") ]

        cases
        |> List.iter (fun (name, prompt) ->
            withCommandProxy (fun upstream plane ->
                let command =
                    Server.CodingToolCli.build
                        None
                        (Server.CodingToolCli.Interactive prompt)
                    |> _.AsShellString

                let result = submitTerminalCommand plane command
                assertOk result $"{name} command submission failed"
                let frame = requireTerminalInputFrame upstream

                Assert.Multiple(fun () ->
                    Assert.That(
                        command |> Seq.exists Char.IsControl,
                        Is.False,
                        $"{name} launch command must be one control-free line"
                    )

                    Assert.That(
                        frame,
                        Is.EqualTo(Encoding.UTF8.GetBytes($"0{command}\r")),
                        $"{name} prompt command changed while crossing the attachment"
                    ))))

    [<Test>]
    member _.``command sender delivers a frame exactly at the attachment byte limit``() =
        withCommandProxy (fun upstream plane ->
            let command =
                String('x', Protocol.MaximumAttachmentMessageBytes - 2)

            let result = submitTerminalCommand plane command
            assertOk result "Exact-limit command submission failed"
            let frame = requireTerminalInputFrame upstream

            Assert.Multiple(fun () ->
                Assert.That(
                    frame.Length,
                    Is.EqualTo Protocol.MaximumAttachmentMessageBytes
                )

                Assert.That(
                    frame,
                    Is.EqualTo(Encoding.UTF8.GetBytes($"0{command}\r"))
                )))

    [<Test>]
    member _.``command sender rejects one-byte-over ASCII and multibyte frames``() =
        [ ("ASCII",
           String('x', Protocol.MaximumAttachmentMessageBytes - 1),
           Protocol.MaximumAttachmentMessageBytes - 1)
          ("multibyte",
           String('x', Protocol.MaximumAttachmentMessageBytes - 3) + "é",
           Protocol.MaximumAttachmentMessageBytes - 2) ]
        |> List.iter (fun (name, command, expectedCharacterCount) ->
            withCommandProxy (fun upstream plane ->
                let result = submitTerminalCommand plane command

                Assert.Multiple(fun () ->
                    Assert.That(
                        command.Length,
                        Is.EqualTo expectedCharacterCount,
                        $"{name} character-count fixture changed"
                    )

                    Assert.That(
                        Encoding.UTF8.GetByteCount($"0{command}\r"),
                        Is.EqualTo(Protocol.MaximumAttachmentMessageBytes + 1),
                        $"{name} frame must be exactly one byte over"
                    )

                    Assert.That(
                        result,
                        Is.EqualTo(
                            Error "The terminal command is invalid"
                            : Result<unit, string>
                        )
                    )

                    Assert.That(
                        terminalInputFrames upstream,
                        Is.Empty,
                        $"{name} oversized command must not reach the attachment"
                    )

                    Assert.That(
                        upstream.Sent.Length,
                        Is.EqualTo 1,
                        $"{name} oversized command must be rejected before browser attachment"
                    ))))

    [<Test>]
    member _.``terminal page hides viewport scrollbar without disabling scrolling``() =
        let html =
            "<html><head><style>.xterm-viewport{overflow-y:scroll}</style></head><body></body></html>"

        let styled = TerminalProxy.hideViewportScrollbar html

        Assert.Multiple(fun () ->
            Assert.That(
                styled,
                Does.Contain(".xterm-viewport{scrollbar-width:none}")
            )

            Assert.That(
                styled,
                Does.Contain(".xterm-viewport::-webkit-scrollbar{display:none}")
            )

            Assert.That(styled, Does.Contain("overflow-y:scroll"))

            Assert.That(
                styled.IndexOf("scrollbar-width:none", StringComparison.Ordinal),
                Is.LessThan(styled.IndexOf("</head>", StringComparison.Ordinal))
            ))

    [<Test>]
    member _.``attachment endpoint rejects invalid bearer origin and oversized requests``() =
        let upstream = new TestWebSocket()
        let connectorCalls = ConcurrentQueue<int>()
        let token = "shared-control-bearer"
        let dashboardOrigin = "http://localhost:5174"
        let allowedOrigins =
            [ dashboardOrigin
              "http://127.0.0.1:5174" ]

        let expectedFrameAncestors =
            "frame-ancestors " + String.concat " " allowedOrigins

        let connector port =
            connectorCalls.Enqueue port
            async.Return(Ok(upstream :> System.Net.WebSockets.WebSocket))

        let plane =
            TerminalProxy.startWithConnector
                connector
                allowedOrigins
                token
                "security-session"
                1
                ignore
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

                Assert.That(connectorCalls.Count, Is.EqualTo(1)))
        finally
            plane.Stop() |> Async.RunSynchronously

    [<Test>]
    member _.``attachment response denies framing when no dashboard origin is configured``() =
        let upstream = new TestWebSocket()

        let connector _ =
            async.Return(Ok(upstream :> System.Net.WebSockets.WebSocket))

        let plane =
            TerminalProxy.startWithConnector
                connector
                []
                "shared-control-bearer"
                "no-origin-session"
                1
                ignore
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
            CanonicalWorktree.create worktreePath

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
                  ControlApiVersion = Protocol.ControlApiVersion }

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
                  ControlApiVersion = Protocol.ControlApiVersion }

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

            let nonDirectVersions = [ "."; ".." ]

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
                    ))

                nonDirectVersions
                |> List.iter (fun version ->
                    match TerminalHostLayout.validateStagedVersion layout version with
                    | Error error ->
                        Assert.That(
                            error,
                            Is.EqualTo(
                                "The staged TerminalHost version is not a direct version directory"
                            ),
                            version
                        )
                    | Ok path -> Assert.Fail($"{version} escaped staging at {path}"))))

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
