module Server.OverviewHistoryRollup

open System
open OverviewData

[<Literal>]
let schemaVersion = 1

[<Literal>]
let resolutionSeconds = 30

let resolution = TimeSpan.FromSeconds(int64 resolutionSeconds)
let exposedHorizon = HistoryWindow.duration HistoryWindow.Hours72
let predecessorRetention = resolution

type RollupRow =
    { Boundary: DateTimeOffset
      Tasks: TaskCount list
      Agents: AgentCount list }

type StagedRollupRow =
    { Generation: int64
      Row: RollupRow }

type PublicationState =
    { SchemaVersion: int
      ResolutionSeconds: int
      SourceGeneration: int64
      PublishedGeneration: int64
      CompleteThrough: DateTimeOffset option
      EarliestDirty: DateTimeOffset option }

let private fromUtcTicks (ticks: int64) =
    DateTimeOffset(ticks, TimeSpan.Zero)

let private utcTicks (timestamp: DateTimeOffset) =
    timestamp.UtcDateTime.Ticks

let isBoundary (timestamp: DateTimeOffset) =
    timestamp.Offset = TimeSpan.Zero
    && utcTicks timestamp % resolution.Ticks = 0L

/// The greatest canonical UTC boundary at or before the supplied instant.
let latestCompleteBoundary (timestamp: DateTimeOffset) =
    let ticks = utcTicks timestamp
    fromUtcTicks (ticks - ticks % resolution.Ticks)

/// The least canonical UTC boundary at or after the supplied source timestamp.
let firstBoundaryAtOrAfter (timestamp: DateTimeOffset) =
    let ticks = utcTicks timestamp
    let remainder = ticks % resolution.Ticks

    if remainder = 0L then
        fromUtcTicks ticks
    else
        fromUtcTicks (ticks + resolution.Ticks - remainder)

/// Canonical UTC boundaries intersecting the inclusive input range.
let boundaries (startTime: DateTimeOffset) (endTime: DateTimeOffset) : DateTimeOffset seq =
    let first = firstBoundaryAtOrAfter startTime
    let last = latestCompleteBoundary endTime

    Seq.unfold (fun (boundary: DateTimeOffset) ->
        if boundary > last then None
        else Some(boundary, boundary + resolution)
    ) first

let stride =
    function
    | HistoryWindow.Hours12 -> 5
    | HistoryWindow.Hours24 -> 10
    | HistoryWindow.Hours72 -> 30

let oldestExposedBoundary (anchor: DateTimeOffset) =
    latestCompleteBoundary anchor - exposedHorizon

let oldestRetainedBoundary (anchor: DateTimeOffset) =
    oldestExposedBoundary anchor - predecessorRetention

let toBucket (boundary: DateTimeOffset) =
    if not (isBoundary boundary) then
        invalidArg (nameof boundary) "Overview rollup buckets must be canonical UTC boundaries."

    boundary.ToUnixTimeSeconds()

let tryFromBucket (bucket: int64) =
    try
        let boundary = DateTimeOffset.FromUnixTimeSeconds bucket
        if isBoundary boundary then Some boundary else None
    with :? ArgumentOutOfRangeException ->
        None

let isSupportedState (state: PublicationState) =
    state.SchemaVersion = schemaVersion
    && state.ResolutionSeconds = resolutionSeconds
    && state.SourceGeneration >= 0L
    && state.PublishedGeneration >= 0L
    && state.PublishedGeneration <= state.SourceGeneration
    && (state.CompleteThrough |> Option.forall isBoundary)
    && (state.EarliestDirty |> Option.forall isBoundary)
