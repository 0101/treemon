module Navigation

open Shared
open Browser
open Fable.Core.JsInterop

type FocusTarget =
    | RepoHeader of RepoId
    | Card of scopedKey: string

type RepoModel =
    { RepoId: RepoId
      Name: string
      Worktrees: WorktreeStatus list
      IsReady: bool
      IsCollapsed: bool }

type NavAction =
    | NoAction
    | CollapseRepo of RepoId
    | ExpandRepo of RepoId

type RepoNav =
    { RepoId: RepoId
      Header: FocusTarget
      Cards: FocusTarget list }

let visibleFocusTargets (repos: RepoModel list) =
    repos
    |> List.collect (fun repo ->
        let repoId = RepoId.value repo.RepoId
        let header = RepoHeader repo.RepoId
        if repo.IsCollapsed then [ header ]
        else
            header :: (repo.Worktrees |> List.map (fun wt -> Card $"{repoId}/{wt.Branch}")))

let getColumnCount () =
    Dom.document.querySelector ".card-grid"
    |> Option.ofObj
    |> Option.map (fun el ->
        let cols: string = Dom.window?getComputedStyle(el)?getPropertyValue("grid-template-columns")
        cols.Trim().Split(' ') |> Array.length)
    |> Option.defaultValue 1

let repoNavSections (repos: RepoModel list) =
    repos
    |> List.map (fun repo ->
        let repoId = RepoId.value repo.RepoId
        { RepoId = repo.RepoId
          Header = RepoHeader repo.RepoId
          Cards =
            if repo.IsCollapsed then []
            else repo.Worktrees |> List.map (fun wt -> Card $"{repoId}/{wt.Branch}") })

let navigateLinear (direction: int) (targets: FocusTarget list) (current: FocusTarget option) =
    match targets with
    | [] -> None
    | _ ->
        match current with
        | None -> Some targets.Head
        | Some c ->
            let idx =
                targets
                |> List.tryFindIndex ((=) c)
                |> Option.defaultValue -1
            if idx < 0 then Some targets.Head
            else Some targets[(idx + direction + targets.Length) % targets.Length]

let navigateSpatial (key: string) (cols: int) (repos: RepoModel list) (focusedElement: FocusTarget option) =
    let sections = repoNavSections repos
    let allTargets = sections |> List.collect (fun s -> s.Header :: s.Cards)
    match allTargets with
    | [] -> None, NoAction
    | _ ->
        match focusedElement with
        | None -> Some allTargets.Head, NoAction
        | Some current ->
            let cardPosition target =
                sections
                |> List.tryFindIndex (fun s ->
                    s.Header = target || s.Cards |> List.contains target)
                |> Option.map (fun si ->
                    let cardIdx = sections[si].Cards |> List.tryFindIndex ((=) target) |> Option.defaultValue 0
                    si, cardIdx)
            match current, key with
            | RepoHeader repoId, "ArrowLeft" ->
                let repo = repos |> List.tryFind (fun r -> r.RepoId = repoId)
                match repo with
                | Some r when not r.IsCollapsed -> Some current, CollapseRepo repoId
                | _ -> Some current, NoAction

            | RepoHeader repoId, "ArrowRight" ->
                let repo = repos |> List.tryFind (fun r -> r.RepoId = repoId)
                match repo with
                | Some r when r.IsCollapsed -> Some current, ExpandRepo repoId
                | _ -> Some current, NoAction

            | RepoHeader _, ("ArrowUp" | "ArrowDown") ->
                navigateLinear (if key = "ArrowDown" then 1 else -1) allTargets (Some current), NoAction

            | Card _, "ArrowLeft" ->
                match cardPosition current with
                | None -> Some current, NoAction
                | Some (si, cardIdx) ->
                    let colPos = cardIdx % cols
                    if colPos > 0 then
                        Some sections[si].Cards[cardIdx - 1], NoAction
                    else
                        if si > 0 then
                            let prev = sections[si - 1]
                            match prev.Cards with
                            | [] -> Some prev.Header, NoAction
                            | cards -> Some (List.last cards), NoAction
                        else
                            Some sections[si].Header, NoAction

            | Card _, "ArrowRight" ->
                match cardPosition current with
                | None -> Some current, NoAction
                | Some (si, cardIdx) ->
                    let section = sections[si]
                    let colPos = cardIdx % cols
                    let rowEnd = colPos >= cols - 1 || cardIdx >= section.Cards.Length - 1
                    if not rowEnd then
                        Some section.Cards[cardIdx + 1], NoAction
                    else
                        let nextRowStart = cardIdx - colPos + cols
                        if nextRowStart < section.Cards.Length then
                            Some section.Cards[nextRowStart], NoAction
                        elif si + 1 < sections.Length then
                            Some sections[si + 1].Header, NoAction
                        else
                            navigateLinear 1 allTargets (Some current), NoAction

            | Card _, "ArrowDown" ->
                match cardPosition current with
                | None -> Some current, NoAction
                | Some (si, cardIdx) ->
                    let targetIdx = cardIdx + cols
                    if targetIdx < sections[si].Cards.Length then
                        Some sections[si].Cards[targetIdx], NoAction
                    else
                        if si + 1 < sections.Length then
                            Some sections[si + 1].Header, NoAction
                        else
                            Some allTargets.Head, NoAction

            | Card _, "ArrowUp" ->
                match cardPosition current with
                | None -> Some current, NoAction
                | Some (si, cardIdx) ->
                    let targetIdx = cardIdx - cols
                    if targetIdx >= 0 then
                        Some sections[si].Cards[targetIdx], NoAction
                    else
                        Some sections[si].Header, NoAction

            | _ -> Some current, NoAction

let scrollFocusedIntoView (useCenter: bool) (target: FocusTarget option) =
    match target with
    | None -> ()
    | Some _ ->
        Dom.document.querySelector ".focused"
        |> Option.ofObj
        |> Option.iter (fun el ->
            let block = if useCenter then "center" else "nearest"
            el?scrollIntoView(createObj [ "block" ==> block; "behavior" ==> "smooth" ]))

let navigateToFirst (repos: RepoModel list) =
    let targets = visibleFocusTargets repos
    match targets with
    | [] -> None
    | _ -> Some targets.Head

let navigateToLast (repos: RepoModel list) =
    let targets = visibleFocusTargets repos
    match targets with
    | [] -> None
    | _ -> Some (List.last targets)

let isLargeJump (repos: RepoModel list) (oldFocus: FocusTarget option) (newFocus: FocusTarget option) =
    let targets = visibleFocusTargets repos
    match oldFocus, newFocus with
    | None, _ -> true
    | _, None -> false
    | Some old, Some nw ->
        let oldIdx = targets |> List.tryFindIndex ((=) old) |> Option.defaultValue 0
        let newIdx = targets |> List.tryFindIndex ((=) nw) |> Option.defaultValue 0
        abs (oldIdx - newIdx) > 3

let adjustFocusAfterCollapse (collapsedRepoId: RepoId) (focusedElement: FocusTarget option) =
    match focusedElement with
    | Some (Card scopedKey) ->
        let repoIdStr = RepoId.value collapsedRepoId
        if scopedKey.StartsWith(repoIdStr + "/") then Some (RepoHeader collapsedRepoId)
        else focusedElement
    | other -> other

let adjustFocusForVisibility (repos: RepoModel list) (focusedElement: FocusTarget option) =
    match focusedElement with
    | None -> None
    | Some focus ->
        let targets = visibleFocusTargets repos
        if targets |> List.contains focus then Some focus
        else
            match targets with
            | [] -> None
            | _ -> Some targets.Head
