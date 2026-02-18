module Shared.EventUtils

type SortMode =
    | ByName
    | ByActivity

let extractBranchName (message: string) =
    match message.IndexOf(" (") with
    | i when i > 0 -> Some message.[..i-1]
    | _ ->
        match message.IndexOf(": ") with
        | i when i > 0 -> Some message.[..i-1]
        | _ -> None

let eventKey (evt: CardEvent) =
    evt.Source, extractBranchName evt.Message |> Option.defaultValue ""

let pinnedErrors (events: CardEvent list) =
    let sorted = events |> List.sortByDescending (fun e -> e.Timestamp)
    let latestByKey =
        sorted
        |> List.fold (fun acc evt ->
            let key = eventKey evt
            match Map.containsKey key acc with
            | true -> acc
            | false -> Map.add key evt acc) Map.empty
    latestByKey
    |> Map.toList
    |> List.map snd
    |> List.filter (fun evt ->
        match evt.Status with
        | Some (StepStatus.Failed _) -> true
        | _ -> false)
    |> List.sortByDescending (fun e -> e.Timestamp)

let mergeWithPinnedErrors (events: CardEvent list) (pinnedMap: Map<string * string, CardEvent>) =
    let existingKeys =
        events
        |> List.map eventKey
        |> Set.ofList
    let missing =
        pinnedMap
        |> Map.toList
        |> List.map snd
        |> List.filter (fun evt -> Set.contains (eventKey evt) existingKeys |> not)
    events @ missing

let sortWorktrees sortMode worktrees =
    match sortMode with
    | ByName ->
        worktrees |> List.sortBy (fun wt -> wt.Branch)
    | ByActivity ->
        worktrees |> List.sortByDescending (fun wt -> wt.LastCommitTime)