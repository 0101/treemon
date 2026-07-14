module OverviewViews

open Shared
open Shared.EventUtils
open Navigation
open Feliz
open Components

let knownCategories =
    [ "WorktreeList"; "GitRefresh"; "BeadsRefresh"; "CodingToolRefresh"; "PrFetch"; "GitFetch" ]

let categoryDisplayName =
    function
    | "WorktreeList"   -> "Worktree \u2630"
    | "GitRefresh"     -> "Git \u21BB"
    | "BeadsRefresh"   -> "Beads \u21BB"
    | "CodingToolRefresh" -> "Agent \u21BB"
    | "PrFetch"        -> "PR \u2913"
    | "GitFetch"       -> "Git \u2913"
    | other            -> other

let private lastSepIndex (s: string) =
    max (s.LastIndexOf('/')) (s.LastIndexOf('\\'))

let commonPathPrefix (paths: string list) =
    match paths with
    | [] -> ""
    | [ single ] ->
        match lastSepIndex single with
        | -1 -> ""
        | i -> single[..i]
    | first :: rest ->
        let prefixLen =
            rest |> List.fold (fun len path ->
                let maxLen = min len path.Length
                let rec findMismatch i =
                    if i >= maxLen then maxLen
                    elif System.Char.ToLowerInvariant first[i] = System.Char.ToLowerInvariant path[i] then findMismatch (i + 1)
                    else i
                findMismatch 0) first.Length
        let prefix = first[..prefixLen - 1]
        match lastSepIndex prefix with
        | -1 -> ""
        | i -> prefix[..i]

let stripPrefix (prefix: string) (target: string) =
    if prefix.Length > 0 && target.Length >= prefix.Length
       && target[..prefix.Length - 1].ToLowerInvariant() = prefix.ToLowerInvariant()
    then target[prefix.Length..]
    else target

let statusOverviewRow (prefix: string) (latestBySource: Map<string, CardEvent>) (category: string) =
    let label = categoryDisplayName category
    match Map.tryFind category latestBySource with
    | None ->
        Html.div [
            prop.className "status-row pending"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text label ]
                Html.span [ prop.className "status-target" ]
                Html.span [ prop.className "status-duration" ]
                Html.span [ prop.className "status-time" ]
                Html.span [ prop.className "status-badge pending"; prop.text "pending" ]
            ]
        ]
    | Some evt ->
        let target = extractBranchName evt.Message |> Option.defaultValue "" |> stripPrefix prefix
        Html.div [
            prop.className "status-row"
            prop.children [
                Html.span [ prop.className "status-category"; prop.text label ]
                Html.span [ prop.className "status-target"; prop.text target ]
                match evt.Duration with
                | Some d -> Html.span [ prop.className "status-duration"; prop.text $"%.1f{d.TotalSeconds}s" ]
                | None -> Html.span [ prop.className "status-duration" ]
                Html.span [ prop.className "status-time"; prop.text (relativeEventTime evt.Timestamp) ]
                match evt.Status with
                | Some _ ->
                    Html.span [
                        prop.className (stepStatusClassName evt.Status)
                        prop.text (stepStatusText evt.Status)
                    ]
                | None -> Html.none
            ]
        ]

let pinnedErrorEntry (prefix: string) (evt: CardEvent) =
    Html.div [
        prop.className "event-entry pinned-error"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
            Html.span [ prop.className "event-source"; prop.text evt.Source ]
            Html.span [ prop.className "event-message"; prop.text (stripPrefix prefix evt.Message) ]
            match evt.Status with
            | Some _ ->
                Html.span [
                    prop.className (stepStatusClassName evt.Status)
                    prop.text (stepStatusText evt.Status)
                ]
            | None -> Html.none
        ]
    ]

let schedulerFooter (repos: RepoModel list) (events: CardEvent list) (latestByCategory: Map<string, CardEvent>) =
    let prefix = repos |> List.map (fun r -> RepoId.value r.RepoId) |> commonPathPrefix
    let errors = pinnedErrors events
    Html.div [
        prop.className "scheduler-footer"
        prop.children [
            match errors with
            | [] -> Html.none
            | errs ->
                Html.div [
                    prop.className "pinned-errors"
                    prop.children (errs |> List.map (pinnedErrorEntry prefix))
                ]
            Html.div [
                prop.className "status-overview"
                prop.children (knownCategories |> List.map (statusOverviewRow prefix latestByCategory))
            ]
            Html.div [
                prop.className "nav-hint"
                prop.children [
                    Html.kbd "Esc"
                    Html.span " to refocus worktree navigation"
                ]
            ]
        ]
    ]
