module Tests.NativeSessionFocusTests

open System.Collections.Concurrent
open NUnit.Framework
open Server
open Server.SessionManager

[<RequireQualifiedAccess>]
type private ActivationCall =
    | Restore of nativeint
    | Attach of sourceThread: uint32 * targetThread: uint32 * attached: bool
    | SetForeground of nativeint
    | SwitchTo of nativeint

let private activationApi
    foregroundWindows
    isIconic
    attachResult
    setForegroundResult
    (calls: ConcurrentQueue<ActivationCall>)
    =
    let foregrounds = ConcurrentQueue<nativeint>(foregroundWindows)

    let api: Win32.WindowActivationApi =
        { IsWindow = fun _ -> true
          IsIconic = fun _ -> isIconic
          RestoreWindow = fun hwnd -> calls.Enqueue(ActivationCall.Restore hwnd)
          GetForegroundWindow =
            fun () ->
                match foregrounds.TryDequeue() with
                | true, hwnd -> hwnd
                | false, _ -> 0n
          GetCurrentThreadId = fun () -> 11u
          GetWindowThreadId = fun _ -> 22u
          AttachThreadInput =
            fun source target attached ->
                calls.Enqueue(ActivationCall.Attach(source, target, attached))
                attachResult
          SetForegroundWindow =
            fun hwnd ->
                calls.Enqueue(ActivationCall.SetForeground hwnd)
                setForegroundResult
          SwitchToThisWindow = fun hwnd -> calls.Enqueue(ActivationCall.SwitchTo hwnd) }

    api

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WindowActivationTests() =

    [<Test>]
    member _.``fallback activation restores and switches the tracked window``() =
        let target = 101n
        let foreground = 202n
        let calls = ConcurrentQueue<ActivationCall>()

        let api =
            activationApi
                [ foreground; target ]
                true
                true
                false
                calls

        let result = Win32.focusWindowWith api target

        Assert.Multiple(fun () ->
            Assert.That(result, Is.True)
            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [| ActivationCall.Restore target
                       ActivationCall.Attach(22u, 11u, true)
                       ActivationCall.SetForeground target
                       ActivationCall.Attach(22u, 11u, false)
                       ActivationCall.SwitchTo target |]
                )
            ))

    [<Test>]
    member _.``activation failure is reported when another window remains foreground``() =
        let target = 303n
        let foreground = 404n
        let calls = ConcurrentQueue<ActivationCall>()
        let api = activationApi [ foreground; foreground ] false false false calls

        let result = Win32.focusWindowWith api target

        Assert.Multiple(fun () ->
            Assert.That(result, Is.False)
            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [| ActivationCall.Attach(22u, 11u, true)
                       ActivationCall.SetForeground target
                       ActivationCall.SwitchTo target |]
                )
            ))

    [<Test>]
    member _.``valid activation request succeeds when no foreground owner is observable``() =
        let target = 505n
        let calls = ConcurrentQueue<ActivationCall>()
        let api = activationApi [ 0n; 0n ] false false false calls

        let result = Win32.focusWindowWith api target

        Assert.Multiple(fun () ->
            Assert.That(result, Is.True)
            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [| ActivationCall.SetForeground target
                       ActivationCall.SwitchTo target |]
                )
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TrackedSessionFocusTests() =

    let path = @"Q:\code\tracked"
    let otherPath = @"Q:\code\other"
    let hwnd = 606n

    let sessions =
        Map.ofList
            [ path, hwnd
              otherPath, 707n ]

    [<Test>]
    member _.``successful focus preserves the exact native session mapping``() =
        let focused = ConcurrentQueue<nativeint>()

        let result, remaining =
            focusTrackedSession
                (fun candidate ->
                    focused.Enqueue(candidate)
                    true)
                path
                sessions

        Assert.Multiple(fun () ->
            Assert.That(result, Is.EqualTo(Ok() : Result<unit, string>))
            Assert.That(focused.ToArray(), Is.EqualTo([| hwnd |]))
            Assert.That(remaining, Is.EqualTo(sessions)))

    [<Test>]
    member _.``failed focus preserves the exact native session mapping``() =
        let focused = ConcurrentQueue<nativeint>()

        let result, remaining =
            focusTrackedSession
                (fun candidate ->
                    focused.Enqueue(candidate)
                    false)
                path
                sessions

        Assert.Multiple(fun () ->
            Assert.That(
                result,
                Is.EqualTo(
                    Error "Failed to activate tracked terminal window"
                    : Result<unit, string>
                )
            )
            Assert.That(focused.ToArray(), Is.EqualTo([| hwnd |]))
            Assert.That(remaining, Is.EqualTo(sessions)))
