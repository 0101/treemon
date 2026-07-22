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

type RollupCandidate =
    { Generation: int64
      StartBoundary: DateTimeOffset
      EndBoundary: DateTimeOffset }

type PublicationState =
    { SchemaVersion: int
      ResolutionSeconds: int
      SourceGeneration: int64
      PublishedGeneration: int64
      CompleteThrough: DateTimeOffset option
      EarliestDirty: DateTimeOffset option }

[<RequireQualifiedAccess>]
type StagingResult =
    | Staged
    | SourceGenerationChanged of CurrentGeneration: int64

[<RequireQualifiedAccess>]
type PublicationResult =
    | Published of PublicationState
    | SourceGenerationChanged of CurrentGeneration: int64

let private countConverter: Newtonsoft.Json.JsonConverter =
    Fable.Remoting.Json.FableJsonConverter()

let internal serializeTasks (tasks: TaskCount list) =
    Newtonsoft.Json.JsonConvert.SerializeObject(
        tasks,
        Newtonsoft.Json.Formatting.None,
        [| countConverter |]
    )

let internal parseTasks (value: string) =
    Newtonsoft.Json.JsonConvert.DeserializeObject<TaskCount list>(
        value,
        [| countConverter |]
    )

let internal serializeAgents (agents: AgentCount list) =
    Newtonsoft.Json.JsonConvert.SerializeObject(
        agents,
        Newtonsoft.Json.Formatting.None,
        [| countConverter |]
    )

let internal parseAgents (value: string) =
    Newtonsoft.Json.JsonConvert.DeserializeObject<AgentCount list>(
        value,
        [| countConverter |]
    )

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

let dirtyBoundary (anchor: DateTimeOffset) (sourceTimestamp: DateTimeOffset) =
    max (oldestExposedBoundary anchor) (firstBoundaryAtOrAfter sourceTimestamp)

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

let private countsAreValid (tasks: TaskCount list) (agents: AgentCount list) =
    let taskKinds = tasks |> List.map _.Kind
    let agentKinds = agents |> List.map _.Kind

    tasks |> List.forall (fun count -> count.Count > 0)
    && agents |> List.forall (fun count -> count.Count > 0)
    && Set.count (Set.ofList taskKinds) = taskKinds.Length
    && Set.count (Set.ofList agentKinds) = agentKinds.Length

let internal countJsonIsValid tasksJson agentsJson =
    try
        countsAreValid (parseTasks tasksJson) (parseAgents agentsJson)
    with _ ->
        false

let internal candidateRangeIsValid (candidate: RollupCandidate) =
    candidate.Generation >= 0L
    && isBoundary candidate.StartBoundary
    && isBoundary candidate.EndBoundary
    && candidate.StartBoundary <= candidate.EndBoundary

let internal candidateRowsAreExact
    (candidate: RollupCandidate)
    (rows: StagedRollupRow list)
    =
    let expectedBoundaries =
        boundaries candidate.StartBoundary candidate.EndBoundary |> Seq.toList

    candidateRangeIsValid candidate
    && rows |> List.forall (fun staged -> staged.Generation = candidate.Generation)
    && rows |> List.forall (fun staged -> countsAreValid staged.Row.Tasks staged.Row.Agents)
    && rows |> List.map (fun staged -> staged.Row.Boundary) = expectedBoundaries
