namespace TerminalHost

open System
open System.Net.WebSockets
open System.Text
open System.Threading

[<RequireQualifiedAccess>]
type internal TerminalAttachmentMode =
    | Browser
    | Command

type private BrowserAttachment =
    { Id: Guid
      Socket: WebSocket
      Mode: TerminalAttachmentMode
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
    | Attach of TerminalAttachmentMode * WebSocket * AsyncReplyChannel<Guid option>
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
          AttachSocket: TerminalAttachmentMode -> WebSocket -> Async<Guid option>
          AcceptBrowserFrame: Guid -> byte array -> Async<Result<unit, string>>
          DetachSocket: Guid -> Async<unit>
          AcceptUpstreamFrame: byte array -> Async<unit>
          UpstreamEnded: unit -> Async<unit>
          Stop: unit -> Async<unit> }

[<RequireQualifiedAccess>]
module TerminalDataPlane =
    let private socketOperationTimeout = TimeSpan.FromSeconds 2.0

    let [<Literal>] private ReplyTimeoutMilliseconds = 60_000

    let private replayGapFrame =
        Encoding.UTF8.GetBytes(
            "0\u001bc\u001b[2J\u001b[H[treemon] Earlier terminal output was omitted because the 1 MiB replay buffer was exceeded while this view was paused.\r\n"
        )

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

    let private requireSent error workflow =
        async {
            match! workflow with
            | true -> return Ok()
            | false -> return Error error
        }

    let private sendReplay attachment frames =
        sendFrames attachment.Socket frames
        |> requireSent "Browser attachment closed during replay"

    let private sendResize upstream terminalSize =
        terminalSize
        |> TerminalProtocol.resizeFrame
        |> sendFrame upstream
        |> requireSent "ttyd WebSocket is not open"

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
        List.append
            ([ state.TitleFrame; state.PreferencesFrame ] |> List.choose id)
            (state.Replay |> ReplayBuffer.frames |> List.map _.Data)

    let private activateAttachment upstream state attachment terminalSize frames =
        async {
            match! sendReplay attachment frames with
            | Error error ->
                return { state with Attachment = None }, Error error
            | Ok() ->
                match! sendResize upstream terminalSize with
                | Error error -> return state, Error error
                | Ok() ->
                    let active =
                        { attachment with
                            Initialized = true
                            Paused = attachment.Mode = TerminalAttachmentMode.Command
                            NextSequence = ReplayBuffer.nextSequence state.Replay }

                    return
                        { state with
                            Attachment = Some active
                            TerminalSize = terminalSize },
                        Ok()
        }

    let private resumeAttachment upstream state attachment =
        async {
            let frames =
                match
                    state.Replay
                    |> ReplayBuffer.framesFrom attachment.NextSequence
                with
                | ReplaySlice.Complete frames -> frames |> List.map _.Data
                | ReplaySlice.Gap frames ->
                    replayGapFrame :: (frames |> List.map _.Data)

            return! activateAttachment upstream state attachment state.TerminalSize frames
        }

    let private handleInitializedBrowserFrame upstream state attachment (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state, Error "Terminal browser frame is empty"
            else
                match char frame[0] with
                | '0' ->
                    let! result =
                        sendFrame upstream frame
                        |> requireSent "ttyd WebSocket is not open"

                    return state, result
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
            async {
                match TerminalProtocol.parseHandshakeSize frame with
                | Error error -> return state, Error error
                | Ok terminalSize ->
                    let frames =
                        match attachment.Mode with
                        | TerminalAttachmentMode.Browser -> initialBrowserFrames state
                        | TerminalAttachmentMode.Command -> []

                    return! activateAttachment upstream state attachment terminalSize frames
            }

    let private sendLiveFrame state sequence (frame: byte array) =
        async {
            match state.Attachment with
            | Some attachment when attachment.Initialized && not attachment.Paused ->
                match! sendFrame attachment.Socket frame with
                | false ->
                    do! closeSocket WebSocketCloseStatus.EndpointUnavailable "Terminal attachment closed" attachment.Socket

                    return { state with Attachment = None }
                | true ->
                    let delivered =
                        sequence
                        |> Option.map (fun next -> { attachment with NextSequence = next })
                        |> Option.defaultValue attachment

                    return { state with Attachment = Some delivered }
            | Some _
            | None -> return state
        }

    let private handleUpstreamFrame replayCapacity state (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state
            else
                let updated, sequence =
                    if frame[0] = byte '0' then
                        let replay =
                            state.Replay |> ReplayBuffer.append replayCapacity frame

                        { state with Replay = replay },
                        Some(ReplayBuffer.nextSequence replay)
                    else
                        match char frame[0] with
                        | '1' -> { state with TitleFrame = Some(Array.copy frame) }, None
                        | '2' -> { state with PreferencesFrame = Some(Array.copy frame) }, None
                        | _ -> state, None

                return! sendLiveFrame updated sequence frame
        }

    let private respond (channel: AsyncReplyChannel<'value>) value state =
        channel.Reply value; state

    let private stopPlane upstream attachmentStatus attachmentReason upstreamReason notify state =
        async {
            match state.Attachment with
            | Some attachment -> do! closeSocket attachmentStatus attachmentReason attachment.Socket
            | None -> ()

            do! closeSocket WebSocketCloseStatus.NormalClosure upstreamReason upstream
            notify ()
            return { state with Attachment = None; Stopped = true }
        }

    let private recoverMessage state message =
        async {
            try
                match message with
                | Attach(_, socket, reply) ->
                    do! closeSocket WebSocketCloseStatus.EndpointUnavailable "Terminal data plane unavailable" socket

                    return respond reply None state
                | BrowserFrame(_, _, reply) ->
                    return respond reply (Error "Terminal data plane operation failed") state
                | Detach(_, reply)
                | UpstreamFrame(_, reply)
                | UpstreamClosed reply
                | Stop reply -> return respond reply () state
            with _ ->
                return state
        }

    let internal createCore replayCapacity upstream onUpstreamEnded =
        let initial =
            { Replay = ReplayBuffer.empty
              Attachment = None
              TerminalSize = TerminalProtocol.defaultSize
              TitleFrame = None
              PreferencesFrame = None
              Stopped = false }

        let upstreamStopped state =
            stopPlane
                upstream
                WebSocketCloseStatus.EndpointUnavailable
                "Terminal session interrupted"
                "Terminal upstream closed"
                (fun () ->
                    try onUpstreamEnded () with _ -> ())
                state

        let explicitlyStopped state =
            stopPlane
                upstream
                WebSocketCloseStatus.NormalClosure
                "Terminal session closed"
                "Terminal session closed"
                ignore
                state

        let processMessage _ state message =
            async {
                match message with
                | Attach(_, socket, reply) when state.Stopped ->
                    do! closeSocket WebSocketCloseStatus.EndpointUnavailable "Terminal session closed" socket

                    return respond reply None state
                | Attach(mode, socket, reply) ->
                    match state.Attachment with
                    | Some previous ->
                        do! closeSocket WebSocketCloseStatus.NormalClosure "Replaced by a new attachment" previous.Socket
                    | None -> ()

                    let attachment =
                        { Id = Guid.NewGuid()
                          Socket = socket
                          Mode = mode
                          Initialized = false
                          Paused = false
                          NextSequence = ReplayBuffer.nextSequence state.Replay }

                    return
                        { state with Attachment = Some attachment }
                        |> respond reply (Some attachment.Id)
                | BrowserFrame(attachmentId, frame, reply) ->
                    match state.Attachment with
                    | Some attachment when attachment.Id = attachmentId ->
                        let! updated, result = handleBrowserFrame upstream state attachment frame
                        return respond reply result updated
                    | Some _
                    | None ->
                        return respond reply (Error "Browser attachment was replaced") state
                | Detach(attachmentId, reply) ->
                    let updated =
                        match state.Attachment with
                        | Some attachment when attachment.Id = attachmentId ->
                            { state with Attachment = None }
                        | Some _
                        | None -> state

                    return respond reply () updated
                | UpstreamFrame(_, reply)
                | UpstreamClosed reply
                | Stop reply when state.Stopped ->
                    return respond reply () state
                | UpstreamFrame(frame, reply) ->
                    let! updated = handleUpstreamFrame replayCapacity state frame
                    return respond reply () updated
                | UpstreamClosed reply ->
                    let! stopped = upstreamStopped state
                    return respond reply () stopped
                | Stop reply ->
                    let! stopped = explicitlyStopped state
                    return respond reply () stopped
            }

        let mailbox = ResilientMailbox.start "TerminalDataPlane" initial recoverMessage processMessage

        let ask build = ResilientMailbox.ask ReplyTimeoutMilliseconds build mailbox

        { AttachmentEndpoint = ""
          AttachSocket = fun mode socket -> ask (fun reply -> Attach(mode, socket, reply))
          AcceptBrowserFrame = fun attachmentId frame -> ask (fun reply -> BrowserFrame(attachmentId, Array.copy frame, reply))
          DetachSocket = fun attachmentId -> ask (fun reply -> Detach(attachmentId, reply))
          AcceptUpstreamFrame = fun frame -> ask (fun reply -> UpstreamFrame(Array.copy frame, reply))
          UpstreamEnded = fun () -> ask UpstreamClosed
          Stop = fun () -> ask Stop }
