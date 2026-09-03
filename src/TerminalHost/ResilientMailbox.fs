namespace TerminalHost

open System

[<RequireQualifiedAccess>]
module internal ResilientMailbox =
    let private log name scope (error: exn) =
        try
            Console.Error.WriteLine($"{name} {scope} failed ({error.GetType().Name})")
        with _ ->
            ()

    let start name initial recover handle =
        let mailbox =
            MailboxProcessor.Start(fun inbox ->
                let rec loop state =
                    async {
                        let! message = inbox.Receive()

                        let! next =
                            async {
                                try
                                    return! handle inbox state message
                                with error ->
                                    log name "message" error

                                    try
                                        return! recover state message
                                    with _ ->
                                        return state
                            }

                        return! loop next
                    }

                loop initial)

        mailbox.Error.Add(log name "mailbox")
        mailbox

    let ask timeout build (mailbox: MailboxProcessor<_>) =
        mailbox.PostAndAsyncReply(build, timeout = timeout)
