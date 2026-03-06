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
      ArchivedWorktrees: WorktreeStatus list
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
        let cols: string = Dom.window.getComputedStyle(el).getPropertyValue("grid-template-columns")
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
    match targets, current with
    | [], _ -> None
    | _, None -> Some targets.Head
    | _, Some c ->
        let idx =
            targets
            |> List.tryFindIndex ((=) c)
            |> Option.defaultValue -1
        if idx < 0 then Some targets.Head
        else Some targets[(idx + direction + targets.Length) % targets.Length]

type ScrollHint = Normal | ScrollToTop | ScrollToBottom

let navigateSpatial (key: string) (cols: int) (repos: RepoModel list) (focusedElement: FocusTarget option) =
    let sections = repoNavSections repos
    let allTargets = sections |> List.collect (fun s -> s.Header :: s.Cards)
    let isFirstSection si = si = 0
    let isLastSection si = si = sections.Length - 1
    let isOnLastRow si cardIdx = isLastSection si && cardIdx + cols >= sections[si].Cards.Length
    let isOnFirstRow si cardIdx = isFirstSection si && cardIdx < cols
    let hintFor si cardIdx =
        if isOnFirstRow si cardIdx then ScrollToTop
        elif isOnLastRow si cardIdx then ScrollToBottom
        else Normal
    let cardPosition target =
        sections
        |> List.tryFindIndex (fun s ->
            s.Header = target || s.Cards |> List.contains target)
        |> Option.map (fun si ->
            let cardIdx = sections[si].Cards |> List.tryFindIndex ((=) target) |> Option.defaultValue 0
            si, cardIdx)
    let hintForTarget target =
        match target with
        | RepoHeader repoId ->
            if sections |> List.tryHead |> Option.exists (fun s -> s.RepoId = repoId) then ScrollToTop else Normal
        | Card _ ->
            cardPosition target
            |> Option.map (fun (si, ci) -> hintFor si ci)
            |> Option.defaultValue Normal
    let hintOf = Option.map hintForTarget >> Option.defaultValue Normal
    let repoFor repoId = repos |> List.tryFind (fun r -> r.RepoId = repoId)
    match allTargets, focusedElement with
    | [], _ -> None, NoAction, Normal
    | _, None -> Some allTargets.Head, NoAction, ScrollToTop
    | _, Some current ->
        match current, key with
        | RepoHeader repoId, "ArrowLeft" when repoFor repoId |> Option.exists (fun r -> not r.IsCollapsed) ->
            Some current, CollapseRepo repoId, Normal

        | RepoHeader repoId, "ArrowRight" when repoFor repoId |> Option.exists _.IsCollapsed ->
            Some current, ExpandRepo repoId, Normal

        | RepoHeader _, ("ArrowLeft" | "ArrowRight") ->
            Some current, NoAction, Normal

        | RepoHeader _, ("ArrowUp" | "ArrowDown") ->
            let target = navigateLinear (if key = "ArrowDown" then 1 else -1) allTargets (Some current)
            target, NoAction, hintOf target

        | Card _, "ArrowLeft" ->
            match cardPosition current with
            | None -> Some current, NoAction, Normal
            | Some (si, cardIdx) ->
                let colPos = cardIdx % cols
                if colPos > 0 then
                    Some sections[si].Cards[cardIdx - 1], NoAction, hintFor si (cardIdx - 1)
                else
                    if si > 0 then
                        let prev = sections[si - 1]
                        match prev.Cards with
                        | [] -> Some prev.Header, NoAction, Normal
                        | cards ->
                            let target = List.last cards
                            Some target, NoAction, hintForTarget target
                    else
                        Some sections[si].Header, NoAction, ScrollToTop

        | Card _, "ArrowRight" ->
            match cardPosition current with
            | None -> Some current, NoAction, Normal
            | Some (si, cardIdx) ->
                let section = sections[si]
                let colPos = cardIdx % cols
                let rowEnd = colPos >= cols - 1 || cardIdx >= section.Cards.Length - 1
                if not rowEnd then
                    Some section.Cards[cardIdx + 1], NoAction, hintFor si (cardIdx + 1)
                else
                    let nextRowStart = cardIdx - colPos + cols
                    if nextRowStart < section.Cards.Length then
                        Some section.Cards[nextRowStart], NoAction, hintFor si nextRowStart
                    elif si + 1 < sections.Length then
                        Some sections[si + 1].Header, NoAction, Normal
                    else
                        let target = navigateLinear 1 allTargets (Some current)
                        target, NoAction, hintOf target

        | Card _, "ArrowDown" ->
            match cardPosition current with
            | None -> Some current, NoAction, Normal
            | Some (si, cardIdx) ->
                let targetIdx = cardIdx + cols
                if targetIdx < sections[si].Cards.Length then
                    Some sections[si].Cards[targetIdx], NoAction, hintFor si targetIdx
                else
                    if si + 1 < sections.Length then
                        Some sections[si + 1].Header, NoAction, Normal
                    else
                        Some allTargets.Head, NoAction, ScrollToTop

        | Card _, "ArrowUp" ->
            match cardPosition current with
            | None -> Some current, NoAction, Normal
            | Some (si, cardIdx) ->
                let targetIdx = cardIdx - cols
                if targetIdx >= 0 then
                    Some sections[si].Cards[targetIdx], NoAction, hintFor si targetIdx
                else
                    Some sections[si].Header, NoAction, if isFirstSection si then ScrollToTop else Normal

        | _ -> Some current, NoAction, Normal

let private headerHeight = 36.0
let private scrollPadding = 50.0
let private headerOffset = headerHeight + scrollPadding

let scrollFocusedIntoView (hint: ScrollHint) (target: FocusTarget option) =
    match target with
    | None -> ()
    | Some _ ->
        Dom.window.requestAnimationFrame(fun (_: float) ->
            Dom.document.querySelector ".focused"
            |> Option.ofObj
            |> Option.iter (fun el ->
                let rect = el.getBoundingClientRect()
                let rectTop = rect.top
                let rectBottom = rect.bottom
                let viewH: float = Dom.window.innerHeight
                let scrollY: float = Dom.window.scrollY
                let elTop = rectTop + scrollY
                let elBottom = rectBottom + scrollY
                let docHeight: float = Dom.document.documentElement.scrollHeight
                // Dom.window.scrollTo typed overload only accepts (x, y) — keep dynamic for options object
                let scrollTo top = Dom.window?scrollTo(createObj [ "top" ==> top; "behavior" ==> "smooth" ])
                match hint with
                | ScrollToTop -> scrollTo 0
                | ScrollToBottom -> scrollTo docHeight
                | Normal when rectTop < headerOffset -> scrollTo (elTop - headerOffset)
                | Normal when rectBottom > viewH - scrollPadding -> scrollTo (elBottom - viewH + scrollPadding)
                | _ -> ())) |> ignore

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
