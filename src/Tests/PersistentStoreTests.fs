module Tests.PersistentStoreTests

open System
open System.Threading.Tasks
open NUnit.Framework
open Server.PersistentStore
open Tests.TestUtils

type private PersistMsg =
    | Persist of Map<int, string> * AsyncReplyChannel<Result<unit, string>>

let private awaitTask (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

let private createPersist handler =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop attempt =
                async {
                    let! Persist(state, reply) = inbox.Receive()
                    reply.Reply(handler attempt state)
                    return! loop (attempt + 1)
                }

            loop 0)

    fun state -> agent.PostAndAsyncReply(fun reply -> Persist(state, reply))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PersistentStoreTests() =

    [<Test>]
    member _.``failed persistence keeps the desired state readable``() =
        let firstFailure = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let persist =
            createPersist (fun attempt _ ->
                if attempt = 0 then
                    firstFailure.TrySetResult() |> ignore

                Error "disk unavailable")

        let store = create "PersistentStoreTests" persist (fun () -> Map.empty)
        store.Update 1 (fun _ -> Some "updated")

        awaitTask firstFailure.Task

        Assert.That(runAsync (store.Get 1), Is.EqualTo(Some "updated"))

        match runAsync (store.Flush()) with
        | Error error -> Assert.That(error, Is.EqualTo("disk unavailable"))
        | Ok() -> Assert.Fail("flush should surface the persistence failure")

    [<Test>]
    member _.``failed persistence is retried internally until it succeeds``() =
        let retrySucceeded =
            TaskCompletionSource<Map<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

        let persist =
            createPersist (fun attempt state ->
                if attempt = 0 then
                    Error "first attempt failed"
                else
                    retrySucceeded.TrySetResult state |> ignore
                    Ok())

        let store = create "PersistentStoreTests" persist (fun () -> Map.empty)
        let expected = Map.ofList [ 1, "updated" ]
        store.Update 1 (fun _ -> Some "updated")

        Assert.That(awaitTask retrySucceeded.Task, Is.EqualTo(expected))

        match runAsync (store.Flush()) with
        | Ok() -> ()
        | Error error -> Assert.Fail($"flush should be clean after retry: {error}")
