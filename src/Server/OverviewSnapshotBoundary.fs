module Server.OverviewSnapshotBoundary

open System

[<Literal>]
let internal resolutionSeconds = 30L

let internal resolution =
    TimeSpan.FromSeconds(float resolutionSeconds)

let private timestampOf bucket =
    DateTimeOffset.FromUnixTimeSeconds bucket

let internal floor (timestamp: DateTimeOffset) =
    timestamp.ToUnixTimeSeconds()
    |> fun seconds -> seconds - seconds % resolutionSeconds
    |> timestampOf

let internal next timestamp =
    floor timestamp + resolution

let internal bucketOf parameterName (timestamp: DateTimeOffset) =
    let bucket = timestamp.ToUnixTimeSeconds()

    if bucket % resolutionSeconds <> 0L
       || timestamp.ToUniversalTime() <> timestampOf bucket then
        invalidArg
            parameterName
            $"Overview snapshot timestamps must be exact {resolutionSeconds}-second boundaries."

    bucket
