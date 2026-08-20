namespace TerminalHost

open System
open System.Text
open System.Text.Json

type internal TerminalSize =
    { Columns: int
      Rows: int }

type internal ReplayFrame =
    { Sequence: int64
      Data: byte array }

type internal ReplayBuffer =
    private
        { Frames: ReplayFrame list
          Bytes: int
          NextSequence: int64
          DroppedBytes: int64 }

[<RequireQualifiedAccess>]
module internal TerminalProtocol =
    [<Literal>]
    let private DefaultColumns = 120

    [<Literal>]
    let private DefaultRows = 30

    [<Literal>]
    let private MaximumColumns = 1_000

    [<Literal>]
    let private MaximumRows = 500

    let defaultSize =
        { Columns = DefaultColumns
          Rows = DefaultRows }

    let private boundedDimension fallback maximum name (root: JsonElement) =
        root.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name = name)
        |> Option.bind (fun property ->
            if property.Value.ValueKind <> JsonValueKind.Number then
                None
            else
                // JsonElement.TryGetInt32 writes through a byref parser boundary.
                let mutable value = 0

                if property.Value.TryGetInt32(&value) then
                    Some value
                else
                    None)
        |> Option.filter (fun value -> value > 0 && value <= maximum)
        |> Option.defaultValue fallback

    let private parseSize (data: byte array) =
        try
            use document = JsonDocument.Parse data
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "Terminal handshake must be a JSON object"
            else
                Ok
                    { Columns =
                        boundedDimension DefaultColumns MaximumColumns "columns" root
                      Rows = boundedDimension DefaultRows MaximumRows "rows" root }
        with :? JsonException ->
            Error "Terminal handshake is not valid JSON"

    let parseInitialHandshake data = parseSize data

    let parseResizeFrame (data: byte array) =
        if data.Length < 2 || data[0] <> byte '1' then
            Error "Terminal resize frame must start with command 1"
        else
            data[1..] |> parseSize

    let resizeFrame size =
        Encoding.UTF8.GetBytes(
            $"1{{\"columns\":{size.Columns},\"rows\":{size.Rows}}}"
        )

    let initialHandshake size =
        Encoding.UTF8.GetBytes(
            $"{{\"AuthToken\":\"\",\"columns\":{size.Columns},\"rows\":{size.Rows}}}"
        )

[<RequireQualifiedAccess>]
module internal ReplayBuffer =
    let empty =
        { Frames = []
          Bytes = 0
          NextSequence = 0L
          DroppedBytes = 0L }

    let private boundedFrame maximumBytes (data: byte array) =
        let copied = Array.copy data

        if copied.Length <= maximumBytes then
            copied, 0
        else
            let suffixBytes = max 0 (maximumBytes - 1)
            let suffix =
                if suffixBytes = 0 then
                    Array.empty
                else
                    copied[copied.Length - suffixBytes ..]

            Array.append [| byte '0' |] suffix, copied.Length - maximumBytes

    let private trim maximumBytes frames bytes droppedBytes =
        let rec trimOldest remaining remainingBytes dropped =
            if remainingBytes <= maximumBytes then
                remaining, remainingBytes, dropped
            else
                match remaining with
                | [] -> [], 0, dropped
                | first :: rest ->
                    trimOldest
                        rest
                        (remainingBytes - first.Data.Length)
                        (dropped + int64 first.Data.Length)

        trimOldest frames bytes droppedBytes

    let append maximumBytes data replay =
        if maximumBytes <= 0 then
            invalidArg (nameof maximumBytes) "Replay capacity must be positive"

        let bounded, oversizedBytes = boundedFrame maximumBytes data

        let frame =
            { Sequence = replay.NextSequence
              Data = bounded }

        let frames, bytes, droppedBytes =
            trim
                maximumBytes
                (replay.Frames @ [ frame ])
                (replay.Bytes + bounded.Length)
                (replay.DroppedBytes + int64 oversizedBytes)

        { Frames = frames
          Bytes = bytes
          NextSequence = replay.NextSequence + 1L
          DroppedBytes = droppedBytes }

    let frames replay = replay.Frames

    let framesFrom sequence replay =
        replay.Frames
        |> List.filter (fun frame -> frame.Sequence >= sequence)

    let bytes replay = replay.Bytes
    let nextSequence replay = replay.NextSequence
    let droppedBytes replay = replay.DroppedBytes
