module Server.BackgroundLoop

open System
open System.Threading
open System.Threading.Tasks

type internal Running =
    private
        { Cancellation: CancellationTokenSource
          Completion: Task }

let internal start (workflow: CancellationToken -> Async<unit>) =
    let cancellation = new CancellationTokenSource()

    try
        { Cancellation = cancellation
          Completion =
            Async.StartAsTask(
                workflow cancellation.Token,
                cancellationToken = cancellation.Token
            )
            :> Task }
    with _ ->
        cancellation.Dispose()
        reraise ()

let internal cancel loop =
    loop.Cancellation.Cancel()

let internal stop name loop =
    try
        cancel loop

        try
            loop.Completion.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException -> ()
        | ex -> Log.log "Shutdown" $"{name} stopped with an error: {ex.Message}"
    finally
        loop.Cancellation.Dispose()
