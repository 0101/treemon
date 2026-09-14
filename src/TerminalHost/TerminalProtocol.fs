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
          NextSequence: int64 }

[<RequireQualifiedAccess>]
type internal ReplaySlice =
    | Complete of ReplayFrame list
    | Gap of ReplayFrame list

type internal TerminalModeReplay =
    private
        { Modes: Map<int, bool>
          Pending: string }

[<RequireQualifiedAccess>]
module internal TerminalProtocol =
    let [<Literal>] private DefaultColumns = 120
    let [<Literal>] private DefaultRows = 30
    let [<Literal>] private MaximumColumns = 1_000
    let [<Literal>] private MaximumRows = 500

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

    let parseHandshakeSize (data: byte array) =
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

    let parseResizeFrame (data: byte array) =
        if data.Length < 2 || data[0] <> byte '1' then
            Error "Terminal resize frame must start with command 1"
        else
            data[1..] |> parseHandshakeSize

    let resizeFrame size =
        Encoding.UTF8.GetBytes(
            $"1{{\"columns\":{size.Columns},\"rows\":{size.Rows}}}"
        )

    let initialHandshake size =
        Encoding.UTF8.GetBytes(
            $"{{\"AuthToken\":\"\",\"columns\":{size.Columns},\"rows\":{size.Rows}}}"
        )

[<RequireQualifiedAccess>]
module internal TerminalModeReplay =
    [<RequireQualifiedAccess>]
    type private ReplayPlacement =
        | BeforeOutput
        | BeforeOutputWhenEnabled
        | AfterOutput

    let private trackedModes =
        set [ 1; 6; 7; 9; 12; 25; 45; 66; 1000; 1001; 1002; 1003
              1004; 1005; 1006; 1007; 1015; 1016; 1047; 1049; 47; 2004 ]

    let private mouseTrackingModes = set [ 9; 1000; 1001; 1002; 1003 ]
    let private mouseEncodingModes = set [ 1005; 1006; 1015; 1016 ]
    let private alternateScreenModes = set [ 47; 1047; 1049 ]
    let private outputRenderingModes = set [ 6; 7; 45 ]
    let private softResetModes = set [ 1; 6; 7; 25; 45; 66; 1004; 2004 ]

    let empty =
        { Modes = Map.empty
          Pending = "" }

    let private updateMode enabled mode modes =
        let family =
            if mouseTrackingModes.Contains mode then
                Some mouseTrackingModes
            elif mouseEncodingModes.Contains mode then
                Some mouseEncodingModes
            elif alternateScreenModes.Contains mode then
                Some alternateScreenModes
            else
                None

        family
        |> Option.map (fun values ->
            modes
            |> Map.filter (fun candidate _ ->
                not (values.Contains candidate)))
        |> Option.defaultValue modes
        |> Map.add mode enabled

    let private finish enabled (pending: string) modes =
        pending.Substring(3).Split(';')
        |> Array.choose (fun value ->
            match Int32.TryParse value with
            | true, mode when trackedModes.Contains mode -> Some mode
            | _ -> None)
        |> Array.fold (fun updated mode ->
            updated |> updateMode enabled mode) modes

    let private pendingStart value = if value = 0x1Buy then "\u001b" else ""

    let private softReset state =
        { Modes =
            state.Modes
            |> Map.filter (fun mode _ ->
                not (softResetModes.Contains mode))
          Pending = "" }

    let private observeByte state value =
        let character = char value

        match state.Pending with
        | "" when value <> 0x1Buy -> state
        | "" -> { state with Pending = "\u001b" }
        | "\u001b" when character = 'c' -> empty
        | "\u001b" when character = '[' ->
            { state with Pending = "\u001b[" }
        | "\u001b[" when character = '?' || character = '!' ->
            { state with Pending = state.Pending + string character }
        | "\u001b[!" when character = 'p' -> softReset state
        | pending
            when pending.StartsWith("\u001b[?", StringComparison.Ordinal)
                 && (Char.IsAsciiDigit character || character = ';')
                 && pending.Length < 64 ->
            { state with Pending = pending + string character }
        | pending
            when pending.StartsWith("\u001b[?", StringComparison.Ordinal)
                 && (character = 'h' || character = 'l') ->
            { Modes = finish (character = 'h') pending state.Modes
              Pending = "" }
        | _ ->
            { state with Pending = pendingStart value }

    let observeOutputFrame (data: byte array) state =
        let rec observe index current =
            if index < data.Length then
                observe (index + 1) (observeByte current data[index])
            else current

        observe 1 state

    let private replayPlacement mode =
        if alternateScreenModes.Contains mode then
            ReplayPlacement.BeforeOutputWhenEnabled
        elif outputRenderingModes.Contains mode then
            ReplayPlacement.BeforeOutput
        else
            ReplayPlacement.AfterOutput

    let private frame matching state =
        state.Modes
        |> Map.toList
        |> List.filter matching
        |> List.sortBy (fun (mode, _) ->
            match replayPlacement mode with
            | ReplayPlacement.BeforeOutputWhenEnabled -> 0, mode
            | ReplayPlacement.BeforeOutput -> 1, mode
            | ReplayPlacement.AfterOutput -> 2, mode)
        |> List.map (fun (mode, enabled) ->
            let setting = if enabled then "h" else "l"
            $"\u001b[?{mode}{setting}")
        |> String.concat ""
        |> function
            | "" -> None
            | modes -> Some(Encoding.ASCII.GetBytes($"0{modes}"))

    let beforeReplayFrame state =
        state
        |> frame (fun (mode, enabled) ->
            match replayPlacement mode with
            | ReplayPlacement.BeforeOutput -> true
            | ReplayPlacement.BeforeOutputWhenEnabled -> enabled
            | ReplayPlacement.AfterOutput -> false)

    let afterReplayFrame state =
        state
        |> frame (fun (mode, _) ->
            replayPlacement mode = ReplayPlacement.AfterOutput)

[<RequireQualifiedAccess>]
module internal ReplayBuffer =
    let empty =
        { Frames = []
          Bytes = 0
          NextSequence = 0L }

    let private boundedFrame maximumBytes (data: byte array) =
        let copied = Array.copy data

        if copied.Length <= maximumBytes then
            copied
        else
            let suffixBytes = max 0 (maximumBytes - 1)
            let suffix =
                if suffixBytes = 0 then
                    Array.empty
                else
                    copied[copied.Length - suffixBytes ..]

            Array.append [| byte '0' |] suffix

    let private trim maximumBytes frames bytes =
        let rec trimOldest remaining remainingBytes =
            if remainingBytes <= maximumBytes then
                remaining, remainingBytes
            else
                match remaining with
                | [] -> [], 0
                | first :: rest ->
                    trimOldest
                        rest
                        (remainingBytes - first.Data.Length)

        trimOldest frames bytes

    let append maximumBytes data replay =
        if maximumBytes <= 0 then
            invalidArg (nameof maximumBytes) "Replay capacity must be positive"

        let bounded = boundedFrame maximumBytes data

        let frame =
            { Sequence = replay.NextSequence
              Data = bounded }

        let frames, bytes =
            trim
                maximumBytes
                (replay.Frames @ [ frame ])
                (replay.Bytes + bounded.Length)

        { Frames = frames
          Bytes = bytes
          NextSequence = replay.NextSequence + 1L }

    let frames replay = replay.Frames

    let framesFrom sequence replay =
        let frames =
            replay.Frames
            |> List.filter (fun frame -> frame.Sequence >= sequence)

        let oldestRetainedSequence =
            replay.Frames
            |> List.tryHead
            |> Option.map _.Sequence
            |> Option.defaultValue replay.NextSequence

        if sequence < oldestRetainedSequence then
            ReplaySlice.Gap frames
        else
            ReplaySlice.Complete frames

    let nextSequence replay = replay.NextSequence
