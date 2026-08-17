namespace CanvasShareViewer

open System
open System.Globalization

module internal ShareExpiry =

    [<Literal>]
    let MetadataKey = "expiresOn"

    let private tryParseUtcRoundTrip value =
        match
            DateTimeOffset.TryParseExact(
                value,
                "o",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )
        with
        | true, expiresOn
            when expiresOn.Offset = TimeSpan.Zero
                 && expiresOn.ToString(
                     "o",
                     CultureInfo.InvariantCulture
                 ) = value ->
            Some expiresOn
        | _ ->
            None

    let isLive now metadata =
        metadata
        |> Map.tryFind MetadataKey
        |> Option.bind tryParseUtcRoundTrip
        |> Option.exists (fun expiresOn -> expiresOn > now)
