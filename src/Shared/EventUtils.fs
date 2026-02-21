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
        | _ ->
            let trimmed = message.Trim()
            if trimmed.Length > 0 && not (message.Contains(" (")) && not (message.Contains(": ")) then
                Some trimmed
            else
                None

let eventKey (evt: CardEvent) =
    evt.Source, extractBranchName evt.Message |> Option.defaultValue ""

let pinnedErrors (events: CardEvent list) =
    events
    |> List.sortByDescending (fun e -> e.Timestamp)
    |> List.distinctBy eventKey
    |> List.filter (fun evt ->
        match evt.Status with
        | Some (StepStatus.Failed _) -> true
        | _ -> false)

let mergeWithPinnedErrors (events: CardEvent list) (pinnedMap: Map<string * string, CardEvent>) =
    let existingKeys = events |> List.map eventKey |> Set.ofList
    let missing =
        pinnedMap
        |> Map.toList
        |> List.map snd
        |> List.filter (fun evt -> existingKeys |> Set.contains (eventKey evt) |> not)
    events @ missing

let sortWorktrees sortMode worktrees =
    match sortMode with
    | ByName -> worktrees |> List.sortBy _.Branch
    | ByActivity -> worktrees |> List.sortByDescending _.LastCommitTime