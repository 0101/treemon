namespace TerminalHost

open System
open System.Net.WebSockets
open System.Threading

type private BrowserAttachment =
    { Id: Guid
      Socket: WebSocket
      Initialized: bool
      Paused: bool
      NextSequence: int64 }

type private DataPlaneState =
    { Replay: ReplayBuffer
      Attachment: BrowserAttachment option
      TerminalSize: TerminalSize
      TitleFrame: byte array option
      PreferencesFrame: byte array option
      Stopped: bool }

type private DataPlaneMessage =
    | Attach of WebSocket * AsyncReplyChannel<Guid option>
    | BrowserFrame of
        Guid *
        byte array *
        AsyncReplyChannel<Result<unit, string>>
    | Detach of Guid * AsyncReplyChannel<unit>
    | UpstreamFrame of byte array * AsyncReplyChannel<unit>
    | UpstreamClosed of AsyncReplyChannel<unit>
    | Stop of AsyncReplyChannel<unit>

type TerminalDataPlane =
    internal
        { AttachmentEndpoint: string
          AttachSocket: WebSocket -> Async<Guid option>
          AcceptBrowserFrame: Guid -> byte array -> Async<Result<unit, string>>
          DetachSocket: Guid -> Async<unit>
          AcceptUpstreamFrame: byte array -> Async<unit>
          UpstreamEnded: unit -> Async<unit>
          Stop: unit -> Async<unit> }

[<RequireQualifiedAccess>]
module TerminalDataPlane =
    let private socketOperationTimeout = TimeSpan.FromSeconds 2.0

    let private socketIsOpen (socket: WebSocket) =
        try
            socket.State = WebSocketState.Open
        with _ ->
            false

    let internal sendFrame (socket: WebSocket) (data: byte array) =
        async {
            if not (socketIsOpen socket) then
                return false
            else
                use cancellation =
                    new CancellationTokenSource(socketOperationTimeout)

                try
                    do!
                        socket.SendAsync(
                            ArraySegment<byte>(data),
                            WebSocketMessageType.Binary,
                            true,
                            cancellation.Token
                        )
                        |> Async.AwaitTask

                    return true
                with _ ->
                    return false
        }

    let private sendFrames socket frames =
        let rec send remaining =
            async {
                match remaining with
                | [] -> return true
                | frame :: tail ->
                    match! sendFrame socket frame with
                    | false -> return false
                    | true -> return! send tail
            }

        send frames

    let private sendReplay attachment frames =
        async {
            match! sendFrames attachment.Socket frames with
            | true -> return Ok()
            | false ->
                return Error "Browser attachment closed during replay"
        }

    let private sendResize upstream terminalSize =
        async {
            match!
                terminalSize
                |> TerminalProtocol.resizeFrame
                |> sendFrame upstream
            with
            | true -> return Ok()
            | false -> return Error "ttyd WebSocket is not open"
        }

    let internal closeSocket status reason (socket: WebSocket) =
        async {
            use cancellation =
                new CancellationTokenSource(socketOperationTimeout)

            try
                match socket.State with
                | WebSocketState.Open
                | WebSocketState.CloseReceived ->
                    do!
                        socket.CloseOutputAsync(status, reason, cancellation.Token)
                        |> Async.AwaitTask
                | WebSocketState.None
                | WebSocketState.Connecting
                | WebSocketState.CloseSent
                | WebSocketState.Closed
                | WebSocketState.Aborted -> ()
                | _ -> ()
            with _ ->
                try
                    socket.Abort()
                with _ ->
                    ()

            try
                socket.Dispose()
            with _ ->
                ()
        }

    let private initialBrowserFrames state =
        let controlFrames =
            [ state.TitleFrame; state.PreferencesFrame ]
            |> List.choose id

        let replayFrames =
            state.Replay
            |> ReplayBuffer.frames
            |> List.map _.Data

        controlFrames @ replayFrames

    let private initializeAttachment upstream state attachment frame =
        async {
            match TerminalProtocol.parseHandshakeSize frame with
            | Error error -> return state, Error error
            | Ok terminalSize ->
                match!
                    initialBrowserFrames state
                    |> sendReplay attachment
                with
                | Error error ->
                    return
                        { state with Attachment = None },
                        Error error
                | Ok() ->
                    match! sendResize upstream terminalSize with
                    | Error error -> return state, Error error
                    | Ok() ->
                        let initialized =
                            { attachment with
                                Initialized = true
                                NextSequence = ReplayBuffer.nextSequence state.Replay }

                        return
                            { state with
                                Attachment = Some initialized
                                TerminalSize = terminalSize },
                            Ok()
        }

    let private resumeAttachment upstream state attachment =
        async {
            let frames =
                state.Replay
                |> ReplayBuffer.framesFrom attachment.NextSequence
                |> List.map _.Data

            match! sendReplay attachment frames with
            | Error error ->
                return
                    { state with Attachment = None },
                    Error error
            | Ok() ->
                match! sendResize upstream state.TerminalSize with
                | Error error -> return state, Error error
                | Ok() ->
                    let resumed =
                        { attachment with
                            Paused = false
                            NextSequence = ReplayBuffer.nextSequence state.Replay }

                    return { state with Attachment = Some resumed }, Ok()
        }

    let private handleInitializedBrowserFrame upstream state attachment (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state, Error "Terminal browser frame is empty"
            else
                match char frame[0] with
                | '0' ->
                    match! sendFrame upstream frame with
                    | true -> return state, Ok()
                    | false -> return state, Error "ttyd WebSocket is not open"
                | '1' ->
                    match TerminalProtocol.parseResizeFrame frame with
                    | Error error -> return state, Error error
                    | Ok terminalSize ->
                        match! sendResize upstream terminalSize with
                        | Error error -> return state, Error error
                        | Ok() ->
                            return { state with TerminalSize = terminalSize }, Ok()
                | '2' ->
                    let paused = { attachment with Paused = true }
                    return { state with Attachment = Some paused }, Ok()
                | '3' -> return! resumeAttachment upstream state attachment
                | _ -> return state, Error "Unknown ttyd browser command"
        }

    let private handleBrowserFrame upstream state attachment frame =
        if attachment.Initialized then
            handleInitializedBrowserFrame upstream state attachment frame
        else
            initializeAttachment upstream state attachment frame

    let private updatedControlFrame state (frame: byte array) =
        if frame.Length = 0 then
            state
        else
            match char frame[0] with
            | '1' -> { state with TitleFrame = Some(Array.copy frame) }
            | '2' -> { state with PreferencesFrame = Some(Array.copy frame) }
            | _ -> state

    let private sendLiveFrame state sequence (frame: byte array) =
        async {
            match state.Attachment with
            | Some attachment when attachment.Initialized && not attachment.Paused ->
                match! sendFrame attachment.Socket frame with
                | false ->
                    do!
                        closeSocket
                            WebSocketCloseStatus.EndpointUnavailable
                            "Terminal attachment closed"
                            attachment.Socket

                    return { state with Attachment = None }
                | true ->
                    let delivered =
                        match sequence with
                        | Some nextSequence ->
                            { attachment with NextSequence = nextSequence }
                        | None -> attachment

                    return { state with Attachment = Some delivered }
            | Some _
            | None -> return state
        }

    let private handleUpstreamFrame replayCapacity state (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state
            elif frame[0] = byte '0' then
                let replay =
                    state.Replay
                    |> ReplayBuffer.append replayCapacity frame

                let updated = { state with Replay = replay }

                return!
                    sendLiveFrame
                        updated
                        (Some(ReplayBuffer.nextSequence replay))
                        frame
            else
                return!
                    sendLiveFrame
                        (updatedControlFrame state frame)
                        None
                        frame
        }

    let private notifyUpstreamEnded onUpstreamEnded =
        try
            onUpstreamEnded ()
        with _ ->
            ()

    let internal createCore replayCapacity upstream onUpstreamEnded =
        let mailbox =
            MailboxProcessor.Start(fun inbox ->
                let rec loop state =
                    async {
                        let! message = inbox.Receive()

                        match message with
                        | Attach(socket, reply) when state.Stopped ->
                            do!
                                closeSocket
                                    WebSocketCloseStatus.EndpointUnavailable
                                    "Terminal session closed"
                                    socket

                            reply.Reply None
                            return! loop state
                        | Attach(socket, reply) ->
                            match state.Attachment with
                            | Some previous ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.NormalClosure
                                        "Replaced by a new attachment"
                                        previous.Socket
                            | None -> ()

                            let attachment =
                                { Id = Guid.NewGuid()
                                  Socket = socket
                                  Initialized = false
                                  Paused = false
                                  NextSequence = ReplayBuffer.nextSequence state.Replay }

                            reply.Reply(Some attachment.Id)

                            return!
                                loop
                                    { state with
                                        Attachment = Some attachment }
                        | BrowserFrame(attachmentId, frame, reply) ->
                            match state.Attachment with
                            | Some attachment when attachment.Id = attachmentId ->
                                let! updated, result =
                                    handleBrowserFrame upstream state attachment frame

                                reply.Reply result
                                return! loop updated
                            | Some _
                            | None ->
                                reply.Reply(Error "Browser attachment was replaced")
                                return! loop state
                        | Detach(attachmentId, reply) ->
                            let updated =
                                match state.Attachment with
                                | Some attachment when attachment.Id = attachmentId ->
                                    { state with Attachment = None }
                                | Some _
                                | None -> state

                            reply.Reply()
                            return! loop updated
                        | UpstreamFrame(frame, reply) when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | UpstreamFrame(frame, reply) ->
                            let! updated =
                                handleUpstreamFrame replayCapacity state frame

                            reply.Reply()
                            return! loop updated
                        | UpstreamClosed reply when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | UpstreamClosed reply ->
                            match state.Attachment with
                            | Some attachment ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.EndpointUnavailable
                                        "Terminal session interrupted"
                                        attachment.Socket
                            | None -> ()

                            do!
                                closeSocket
                                    WebSocketCloseStatus.NormalClosure
                                    "Terminal upstream closed"
                                    upstream

                            notifyUpstreamEnded onUpstreamEnded
                            reply.Reply()

                            return!
                                loop
                                    { state with
                                        Attachment = None
                                        Stopped = true }
                        | Stop reply when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | Stop reply ->
                            match state.Attachment with
                            | Some attachment ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.NormalClosure
                                        "Terminal session closed"
                                        attachment.Socket
                            | None -> ()

                            do!
                                closeSocket
                                    WebSocketCloseStatus.NormalClosure
                                    "Terminal session closed"
                                    upstream

                            reply.Reply()

                            return!
                                loop
                                    { state with
                                        Attachment = None
                                        Stopped = true }
                    }

                loop
                    { Replay = ReplayBuffer.empty
                      Attachment = None
                      TerminalSize = TerminalProtocol.defaultSize
                      TitleFrame = None
                      PreferencesFrame = None
                      Stopped = false })

        { AttachmentEndpoint = ""
          AttachSocket = fun socket -> mailbox.PostAndAsyncReply(fun reply -> Attach(socket, reply))
          AcceptBrowserFrame =
            fun attachmentId frame ->
                mailbox.PostAndAsyncReply(fun reply ->
                    BrowserFrame(attachmentId, Array.copy frame, reply))
          DetachSocket =
            fun attachmentId ->
                mailbox.PostAndAsyncReply(fun reply -> Detach(attachmentId, reply))
          AcceptUpstreamFrame =
            fun frame ->
                mailbox.PostAndAsyncReply(fun reply ->
                    UpstreamFrame(Array.copy frame, reply))
          UpstreamEnded =
            fun () -> mailbox.PostAndAsyncReply UpstreamClosed
          Stop = fun () -> mailbox.PostAndAsyncReply Stop }
