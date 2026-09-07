module WorktreeSearch

open System
open Shared
open Navigation
open Feliz
open Browser
open Fable.Core.JsInterop

type MatchIndexes =
    { Repository: Set<int>
      Branch: Set<int>
      Path: Set<int> }

type SearchResult =
    { Repository: string
      Worktree: WorktreeStatus
      Matches: MatchIndexes
      Score: int }

[<RequireQualifiedAccess>]
type SelectionDirection =
    | Up
    | Down

[<RequireQualifiedAccess>]
type ReturnFocus =
    | Dashboard
    | Canvas
    | EmbeddedTerminal of EmbeddedTerminalId

type OpenState =
    { Query: string
      SelectedPath: WorktreePath option
      ReturnFocus: ReturnFocus }

[<RequireQualifiedAccess>]
type State =
    | Closed
    | Open of OpenState

[<RequireQualifiedAccess>]
type Msg =
    | Open
    | OpenFromCanvas
    | OpenFromTerminal of EmbeddedTerminalId
    | Close
    | QueryChanged of string
    | MoveSelection of SelectionDirection
    | SelectResult of WorktreePath
    | ChooseSelection
    | ChooseResult of WorktreePath

[<RequireQualifiedAccess>]
type Action =
    | NoAction
    | RevealSelection
    | RestoreFocus of ReturnFocus
    | FocusWorktree of WorktreePath

type private SearchField =
    | Repository
    | Branch
    | Path

type private SearchCharacter =
    { Value: char
      Field: SearchField
      Index: int }

type private ScoredMatch =
    { Score: int
      Matches: MatchIndexes }

type private SearchEntry =
    { Repository: string
      Worktree: WorktreeStatus }

let initial = State.Closed

let isOpen =
    function
    | State.Open _ -> true
    | State.Closed -> false

let isOpenShortcut (key: string) (ctrl: bool) (meta: bool) (alt: bool) =
    (ctrl || meta) && not alt && key.ToLowerInvariant() = "p"

let isOpenRequest =
    function
    | Msg.Open
    | Msg.OpenFromCanvas
    | Msg.OpenFromTerminal _ -> true
    | _ -> false

let private emptyMatches =
    { Repository = Set.empty
      Branch = Set.empty
      Path = Set.empty }

let private searchCharacters field (value: string) =
    value
    |> Seq.mapi (fun index character ->
        { Value = Char.ToLowerInvariant character
          Field = field
          Index = index })
    |> Seq.filter (fun character -> Char.IsLetterOrDigit character.Value)
    |> Seq.toList

let private queryCharacters (value: string) =
    value
    |> Seq.map Char.ToLowerInvariant
    |> Seq.filter Char.IsLetterOrDigit
    |> Seq.toList

let private queryTokens (query: string) =
    let normalized =
        query
        |> Seq.map (fun character ->
            if Char.IsLetterOrDigit character then
                Char.ToLowerInvariant character
            else
                ' ')
        |> Seq.toArray
        |> fun characters -> String(characters)

    normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

let private hasEffectiveQuery query =
    queryTokens query |> List.isEmpty |> not

let private charactersToString (characters: char list) =
    characters
    |> List.toArray
    |> fun values -> String(values)

let private scoreSequence source query =
    let rec score sourcePosition previousPosition remainingSource remainingQuery total matched =
        match remainingQuery, remainingSource with
        | [], _ -> Some(total, List.rev matched)
        | _, [] -> None
        | queryCharacter :: queryTail, candidate :: sourceTail ->
            if candidate.Value = queryCharacter then
                let adjacency =
                    previousPosition
                    |> Option.exists (fun previous -> sourcePosition = previous + 1)

                let increment =
                    (if adjacency then 8 else 2)
                    + (if candidate.Index = 0 then 5 else 0)

                score
                    (sourcePosition + 1)
                    (Some sourcePosition)
                    sourceTail
                    queryTail
                    (total + increment)
                    (candidate :: matched)
            else
                score
                    (sourcePosition + 1)
                    previousPosition
                    sourceTail
                    remainingQuery
                    total
                    matched

    score 0 None source query 0 []

let private matchesFor (field: SearchField) (indexes: int list) : MatchIndexes =
    let matched = Set.ofList indexes

    match field with
    | Repository -> { emptyMatches with Repository = matched }
    | Branch -> { emptyMatches with Branch = matched }
    | Path -> { emptyMatches with Path = matched }

let private scoreField (field: SearchField) weight value query : ScoredMatch option =
    let source = searchCharacters field value
    let query = queryCharacters query

    scoreSequence source query
    |> Option.map (fun (score, matched) ->
        let sourceText = source |> List.map _.Value |> charactersToString
        let queryText = charactersToString query
        let contiguousBonus =
            if sourceText.StartsWith queryText then 42
            elif sourceText.Contains queryText then 24
            else 0

        { Score = (score + contiguousBonus) * weight
          Matches = matched |> List.map _.Index |> matchesFor field })

let private combineMatches (left: MatchIndexes) (right: MatchIndexes) =
    { Repository = Set.union left.Repository right.Repository
      Branch = Set.union left.Branch right.Branch
      Path = Set.union left.Path right.Path }

let private combineScoredMatches (matches: ScoredMatch list) : ScoredMatch =
    matches
    |> List.fold
        (fun combined current ->
            { Score = combined.Score + current.Score
              Matches = combineMatches combined.Matches current.Matches })
        { Score = 0; Matches = emptyMatches }

let private identityMatch (entry: SearchEntry) query : ScoredMatch option =
    let source =
        [ searchCharacters Repository entry.Repository
          searchCharacters Branch entry.Worktree.Branch ]
        |> List.collect id

    scoreSequence source (queryCharacters query)
    |> Option.bind (fun (score, matched) ->
        let matchedFields = matched |> List.map _.Field |> Set.ofList

        if
            not (Set.contains Repository matchedFields)
            || not (Set.contains Branch matchedFields)
        then
            None
        else
            let addMatch (matches: MatchIndexes) (character: SearchCharacter) =
                match character.Field with
                | Repository ->
                    { matches with Repository = Set.add character.Index matches.Repository }
                | Branch ->
                    { matches with Branch = Set.add character.Index matches.Branch }
                | Path ->
                    { matches with Path = Set.add character.Index matches.Path }

            Some
                { Score = score + 36
                  Matches = matched |> List.fold addMatch emptyMatches })

let private bestTokenMatch (entry: SearchEntry) token =
    [ scoreField Branch 3 entry.Worktree.Branch token
      scoreField Repository 2 entry.Repository token
      identityMatch entry token
      scoreField Path 1 (WorktreePath.value entry.Worktree.Path) token ]
    |> List.choose id
    |> List.sortByDescending _.Score
    |> List.tryHead

let private collectFieldMatches (entry: SearchEntry) tokens =
    let rec collect remaining =
        match remaining with
        | [] -> Some []
        | token :: rest ->
            bestTokenMatch entry token
            |> Option.bind (fun current ->
                collect rest
                |> Option.map (fun matches -> current :: matches))

    collect tokens

let private entries (repos: RepoModel list) : SearchEntry list =
    repos
    |> List.collect (fun repo ->
        repo.Worktrees
        |> List.map (fun worktree ->
            { Repository = repo.Name
              Worktree = worktree }))

let private toSearchResult (entry: SearchEntry) (scored: ScoredMatch) : SearchResult =
    { Repository = entry.Repository
      Worktree = entry.Worktree
      Matches = scored.Matches
      Score = scored.Score }

let private recentSessionsFirst entries =
    entries
    |> List.mapi (fun index entry -> index, entry)
    |> List.sortByDescending (fun (index, entry) ->
        entry.Worktree.SessionActivityAt, -index)
    |> List.map snd

let search (repos: RepoModel list) query : SearchResult list =
    let tokens = queryTokens query

    match tokens with
    | [] ->
        entries repos
        |> recentSessionsFirst
        |> List.map (fun entry ->
            toSearchResult entry { Score = 0; Matches = emptyMatches })
    | _ ->
        entries repos
        |> List.choose (fun entry ->
            collectFieldMatches entry tokens
            |> Option.map combineScoredMatches
            |> Option.map (toSearchResult entry))
        |> List.sortByDescending _.Score

let private selectedResultIndex (results: SearchResult list) selectedPath =
    selectedPath
    |> Option.bind (fun path ->
        results
        |> List.tryFindIndex (fun result -> result.Worktree.Path = path))
    |> Option.defaultValue 0

let private trySelectedResult repos openState =
    let results = search repos openState.Query
    results
    |> List.tryItem (selectedResultIndex results openState.SelectedPath)

let private openSearch repos returnFocus =
    let selectedPath =
        search repos ""
        |> List.tryHead
        |> Option.map _.Worktree.Path

    State.Open
        { Query = ""
          SelectedPath = selectedPath
          ReturnFocus = returnFocus },
    Action.NoAction

let update repos message state =
    match message, state with
    | Msg.Open, State.Closed ->
        openSearch repos ReturnFocus.Dashboard
    | Msg.OpenFromCanvas, State.Closed ->
        openSearch repos ReturnFocus.Canvas
    | Msg.OpenFromTerminal terminalId, State.Closed ->
        openSearch repos (ReturnFocus.EmbeddedTerminal terminalId)
    | Msg.Open, State.Open _
    | Msg.OpenFromCanvas, State.Open _
    | Msg.OpenFromTerminal _, State.Open _ ->
        state, Action.NoAction
    | Msg.Close, State.Open openState ->
        State.Closed, Action.RestoreFocus openState.ReturnFocus
    | Msg.Close, State.Closed ->
        state, Action.NoAction
    | Msg.QueryChanged query, State.Open openState ->
        let selectedPath =
            search repos query
            |> List.tryHead
            |> Option.map _.Worktree.Path

        State.Open { openState with Query = query; SelectedPath = selectedPath },
        if selectedPath.IsSome then Action.RevealSelection else Action.NoAction
    | Msg.QueryChanged _, State.Closed ->
        state, Action.NoAction
    | Msg.MoveSelection direction, State.Open openState ->
        let results = search repos openState.Query
        let count = results.Length
        let offset =
            match direction with
            | SelectionDirection.Up -> -1
            | SelectionDirection.Down -> 1

        let selectedPath =
            if count = 0 then
                None
            else
                let current = selectedResultIndex results openState.SelectedPath
                let next = (current + offset + count) % count
                results |> List.tryItem next |> Option.map _.Worktree.Path

        State.Open { openState with SelectedPath = selectedPath },
        if selectedPath.IsSome then Action.RevealSelection else Action.NoAction
    | Msg.MoveSelection _, State.Closed ->
        state, Action.NoAction
    | Msg.SelectResult path, State.Open openState when openState.SelectedPath <> Some path ->
        State.Open { openState with SelectedPath = Some path }, Action.NoAction
    | Msg.SelectResult _, State.Open _
    | Msg.SelectResult _, State.Closed ->
        state, Action.NoAction
    | Msg.ChooseSelection, State.Open openState ->
        match trySelectedResult repos openState with
        | Some result -> State.Closed, Action.FocusWorktree result.Worktree.Path
        | None -> state, Action.NoAction
    | Msg.ChooseSelection, State.Closed ->
        state, Action.NoAction
    | Msg.ChooseResult path, State.Open _ ->
        State.Closed, Action.FocusWorktree path
    | Msg.ChooseResult _, State.Closed ->
        state, Action.NoAction

let private shortcutKey (text: string) =
    Html.span [
        prop.className "worktree-search-key"
        prop.text text
    ]

let private highlightedText (indexes: Set<int>) (text: string) =
    text
    |> Seq.mapi (fun index character ->
        if Set.contains index indexes then
            Html.span [
                prop.className "worktree-search-match"
                prop.text (string character)
            ]
        else
            Html.text (string character))
    |> Seq.toList

let private searchIcon () =
    Svg.svg [
        svg.className "worktree-search-icon"
        svg.viewBox "0 0 24 24"
        svg.fill "none"
        svg.children [
            Svg.circle [
                svg.cx 11
                svg.cy 11
                svg.r 6.5
                svg.stroke "currentColor"
                svg.strokeWidth 1.8
            ]
            Svg.path [
                svg.d "M16 16l4 4"
                svg.stroke "currentColor"
                svg.strokeWidth 1.8
                svg.custom ("strokeLinecap", "round")
            ]
        ]
    ]

let private resultStatus (worktree: WorktreeStatus) =
    match worktree.CodingTool, worktree.IsDirty with
    | (Working | WaitingForUser), _ -> "worktree-search-status active"
    | _, true -> "worktree-search-status dirty"
    | _, false -> "worktree-search-status idle"

let private resultView dispatch selected index (result: SearchResult) =
    Html.button [
        prop.key (WorktreePath.value result.Worktree.Path)
        prop.id $"worktree-search-result-{index}"
        prop.className (
            if selected then
                "worktree-search-result selected"
            else
                "worktree-search-result")
        prop.type'.button
        prop.role "option"
        prop.ariaSelected selected
        prop.tabIndex -1
        prop.onMouseMove (fun _ -> dispatch (Msg.SelectResult result.Worktree.Path))
        prop.onClick (fun _ -> dispatch (Msg.ChooseResult result.Worktree.Path))
        prop.children [
            Html.span [
                prop.className (resultStatus result.Worktree)
                prop.custom ("aria-hidden", "true")
            ]
            Html.span [
                prop.className "worktree-search-main"
                prop.children [
                    Html.span [
                        prop.className "worktree-search-repository"
                        prop.children (highlightedText result.Matches.Repository result.Repository)
                    ]
                    Html.span [
                        prop.className "worktree-search-detail"
                        prop.children [
                            Html.span [
                                prop.className "worktree-search-branch"
                                prop.children (
                                    highlightedText
                                        result.Matches.Branch
                                        result.Worktree.Branch)
                            ]
                            Html.span [
                                prop.className "worktree-search-path"
                                prop.children (
                                    highlightedText
                                        result.Matches.Path
                                        (WorktreePath.value result.Worktree.Path))
                            ]
                        ]
                    ]
                ]
            ]
            Html.span [
                prop.className "worktree-search-result-action"
                prop.custom ("aria-hidden", "true")
                prop.text "\u2192"
            ]
        ]
    ]

let private inputKeyDown dispatch (event: Browser.Types.KeyboardEvent) =
    let handle message =
        event.preventDefault()
        event.stopPropagation()
        dispatch message

    if
        not (
            emitJsExpr<bool>
                event
                "$0.isComposing === true || ($0.nativeEvent && $0.nativeEvent.isComposing === true)"
        )
    then
        match event.key with
        | "ArrowDown" -> handle (Msg.MoveSelection SelectionDirection.Down)
        | "ArrowUp" -> handle (Msg.MoveSelection SelectionDirection.Up)
        | "Enter" -> handle Msg.ChooseSelection
        | "Escape" -> handle Msg.Close
        | _ -> ()

let scrollSelectedIntoView () =
    Dom.window?requestAnimationFrame(fun (_: float) ->
        Dom.document.querySelector ".worktree-search-result.selected"
        |> Option.ofObj
        |> Option.iter (fun element ->
            emitJsExpr<unit>
                element
                "$0.scrollIntoView({block:'nearest'})"))
    |> ignore

let view dispatch repos state =
    match state with
    | State.Closed -> Html.none
    | State.Open openState ->
        let results = search repos openState.Query
        let selectedIndex = selectedResultIndex results openState.SelectedPath
        let hasInput = not (String.IsNullOrWhiteSpace openState.Query)
        let hasQuery = hasEffectiveQuery openState.Query

        ModalOverlay.modalOverlayWithClasses
            (Some "worktree-search-overlay")
            (Some "worktree-search-dialog")
            (Some(fun () -> dispatch Msg.Close))
            [
                Html.div [
                    prop.className "worktree-search"
                    prop.role "dialog"
                    prop.custom ("aria-modal", "true")
                    prop.custom ("aria-labelledby", "worktree-search-title")
                    prop.children [
                        Html.div [
                            prop.className "worktree-search-header"
                            prop.children [
                                Html.span [
                                    prop.id "worktree-search-title"
                                    prop.className "worktree-search-title"
                                    prop.text "Go to worktree"
                                ]
                                Html.span [
                                    prop.className "worktree-search-shortcut"
                                    prop.children [ shortcutKey "Ctrl"; shortcutKey "P" ]
                                ]
                            ]
                        ]
                        Html.div [
                            prop.className "worktree-search-input-wrap"
                            prop.children [
                                searchIcon ()
                                Html.input [
                                    prop.id "worktree-search-input"
                                    prop.className "worktree-search-input"
                                    prop.type'.text
                                    prop.autoFocus true
                                    prop.autoComplete "off"
                                    prop.spellCheck false
                                    prop.value openState.Query
                                    prop.placeholder "Search branch, repository, or path..."
                                    prop.ariaLabel "Search worktrees"
                                    prop.custom ("aria-controls", "worktree-search-results")
                                    prop.custom ("aria-autocomplete", "list")
                                    if not results.IsEmpty then
                                        prop.custom (
                                            "aria-activedescendant",
                                            $"worktree-search-result-{selectedIndex}")
                                    prop.onChange (Msg.QueryChanged >> dispatch)
                                    prop.onKeyDown (inputKeyDown dispatch)
                                ]
                                if hasInput then
                                    Html.button [
                                        prop.className "worktree-search-clear"
                                        prop.type'.button
                                        prop.tabIndex -1
                                        prop.ariaLabel "Clear search"
                                        prop.onMouseDown _.preventDefault()
                                        prop.onClick (fun _ -> dispatch (Msg.QueryChanged ""))
                                        prop.text "\u00D7"
                                    ]
                            ]
                        ]
                        Html.div [
                            prop.className "worktree-search-meta"
                            prop.children [
                                Html.span [
                                    prop.text (
                                        if hasQuery then
                                            "Best matches"
                                        else
                                            "All worktrees")
                                ]
                                Html.span [
                                    let count = results.Length
                                    let suffix = if count = 1 then "" else "s"
                                    prop.text $"{count} worktree{suffix}"
                                ]
                            ]
                        ]
                        Html.div [
                            prop.id "worktree-search-results"
                            prop.className "worktree-search-results"
                            prop.role "listbox"
                            prop.ariaLabel "Matching worktrees"
                            if results.IsEmpty then
                                prop.children [
                                    Html.div [
                                        prop.className "worktree-search-empty"
                                        prop.children [
                                            Html.strong "No matching worktree"
                                            Html.span "Try a branch, repository, or path fragment."
                                        ]
                                    ]
                                ]
                            else
                                prop.children (
                                    results
                                    |> List.mapi (fun index result ->
                                        resultView dispatch (index = selectedIndex) index result))
                        ]
                        Html.div [
                            prop.className "worktree-search-footer"
                            prop.children [
                                Html.span [
                                    prop.className "worktree-search-behavior"
                                    prop.text "Focuses and reveals the card"
                                ]
                                Html.span [
                                    prop.className "worktree-search-hints"
                                    prop.children [
                                        Html.span [
                                            prop.children [
                                                shortcutKey "\u2191"
                                                shortcutKey "\u2193"
                                                Html.text " Navigate"
                                            ]
                                        ]
                                        Html.span [
                                            prop.children [
                                                shortcutKey "Enter"
                                                Html.text " Go"
                                            ]
                                        ]
                                        Html.span [
                                            prop.children [
                                                shortcutKey "Esc"
                                                Html.text " Close"
                                            ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                ]
            ]
