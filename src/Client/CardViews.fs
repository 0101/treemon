module CardViews

open Shared
open Shared.EventUtils
open Navigation
open Feliz
open Components
open ActionButtons
open CanvasAwareness

let ctClassName =
    function
    | Working        -> "working"
    | WaitingForUser -> "waiting"
    | Idle           -> "idle"
    | NoSession      -> "nosession"

let ctTooltip =
    function
    | Working        -> "Working"
    | WaitingForUser -> "Waiting for user"
    | Idle           -> "Idle"
    | NoSession      -> "No session"

let isMerged (wt: WorktreeStatus) =
    match wt.Pr with
    | HasPr pr -> pr.IsMerged
    | NoPr -> false

let cardClassName (wt: WorktreeStatus) =
    let ct = ctClassName wt.CodingTool
    let session = if wt.HasActiveSession then " has-session" else ""
    if isMerged wt then $"wt-card ct-{ct} merged{session}" else $"wt-card ct-{ct}{session}"

let beadsTotal (b: BeadsSummary) = b.Open + b.InProgress + b.Blocked + b.Closed

let segmentPct count total =
    match total with
    | 0 -> 0.0
    | _ -> (float count * 100.0) / float total

let beadsCounts (className: string) (b: BeadsSummary) =
    Html.span [
        prop.className className
        prop.children [
            Html.span [ prop.className "beads-open"; prop.text (string b.Open) ]
            Html.span [ prop.className "beads-sep"; prop.text "/" ]
            Html.span [ prop.className "beads-inprogress"; prop.text (string b.InProgress) ]
            Html.span [ prop.className "beads-sep"; prop.text "/" ]
            Html.span [ prop.className "beads-blocked"; prop.text (string b.Blocked) ]
            Html.span [ prop.className "beads-sep"; prop.text "/" ]
            Html.span [ prop.className "beads-closed"; prop.text (string b.Closed) ]
        ]
    ]

let beadsProgressBar (b: BeadsSummary) =
    let total = beadsTotal b
    Html.div [
        prop.className "progress-bar"
        prop.children [
            Html.div [
                prop.className "progress-segment seg-open"
                prop.style [ style.width (length.percent (segmentPct b.Open total)) ]
            ]
            Html.div [
                prop.className "progress-segment seg-inprogress"
                prop.style [ style.width (length.percent (segmentPct b.InProgress total)) ]
            ]
            Html.div [
                prop.className "progress-segment seg-blocked"
                prop.style [ style.width (length.percent (segmentPct b.Blocked total)) ]
            ]
            Html.div [
                prop.className "progress-segment seg-closed"
                prop.style [ style.width (length.percent (segmentPct b.Closed total)) ]
            ]
        ]
    ]

/// The model read-slice the card views render over. Bundles the eight values that were
/// previously threaded as loose positional args through repoSection/renderCard/worktreeCard
/// (repoSection alone took nine), so a silent argument transposition can no longer compile.
/// References only Shared/Navigation/CanvasAwareness types — no Model/Msg dependency — so this
/// record can later move ahead of AppTypes alongside the card views.
type CardViewProps =
    { EditorName: string
      IsCompact: bool
      FocusedElement: FocusTarget option
      BranchEvents: Map<string, CardEvent list>
      SyncPending: Set<string>
      ActionCooldowns: Set<WorktreePath>
      CanvasEvents: Map<string, CanvasEvent list>
      /// Currently unread by the card views; kept as part of the model read-slice — it
      /// formalizes the previously dead `canvasPaneOpen` arg that was threaded through renderCard.
      CanvasPaneOpen: bool }

/// The dispatch-derived actions the card views raise back to the host, mirroring
/// CanvasPaneCallbacks. Every field is a plain `… -> unit` function — no Msg dependency — so the
/// card views invoke named card actions instead of holding raw `dispatch`, and App.fs builds the
/// record from `dispatch` in `view`. This is a strictly narrower capability than the old
/// `dispatch` (which could send any Msg); leaf helpers take this whole record in its place.
type CardCallbacks =
    { FocusCard: string -> unit
      ToggleRepo: RepoId -> unit
      CreateWorktree: RepoId -> unit
      /// Primary terminal action: focuses the active session when one exists, else opens a terminal.
      /// Intent-named (not a 1:1 Msg mirror) — do not "simplify" to always dispatch OpenTerminal.
      OpenTerminal: WorktreeStatus -> unit
      OpenEditor: WorktreeStatus -> unit
      OpenNewTab: WorktreeStatus -> unit
      ResumeSession: WorktreeStatus -> unit
      /// Raises the delete *confirmation* (ConfirmDeleteWorktree), not an immediate delete.
      DeleteWorktree: string -> unit
      /// Raises the archive *confirmation* (ConfirmArchiveWorktree), not an immediate archive.
      ArchiveWorktree: string -> unit
      StartSync: WorktreePath -> string -> unit
      CancelSync: WorktreePath -> unit
      LaunchAction: WorktreePath -> ActionKind -> unit
      OpenCanvasDoc: string -> string -> unit
      DispatchArchive: ArchiveViews.Msg -> unit }

let mainBehindIndicator (baseBranch: string) (count: int) =
    if count = 0 then
        Html.span [
            prop.className "main-behind up-to-date"
            prop.text "up to date"
        ]
    else
        Html.span [
            prop.className (if count > 20 then "main-behind behind-warning" else "main-behind")
            prop.text ($"{count} behind {baseBranch}")
        ]

let isBranchSyncing (events: CardEvent list) =
    events |> List.exists (fun e -> e.Status = Some StepStatus.Running && e.Source <> EventSource.PostFork)

/// Post-fork setup is routine when it works, so a successful or still-running run is noise on the
/// card — only its failures (including timeouts) are worth surfacing. Events from every other
/// source always show.
let isVisibleCardEvent (evt: CardEvent) =
    evt.Source <> EventSource.PostFork
    || (match evt.Status with Some (StepStatus.Failed _) -> true | _ -> false)

let private providerDisplayName (provider: CodingToolProvider option) =
    match provider with
    | Some CopilotCli -> "Copilot"
    | None -> "Coding tool"

let syncButton (callbacks: CardCallbacks) (baseBranch: string) (wt: WorktreeStatus) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) =
    if isPending then
        Html.button [
            prop.className "sync-starting-btn"
            prop.disabled true
            yield! noFocusProps
            prop.text "Sync starting"
        ]
    else
        let syncing = isBranchSyncing branchEvents
        let codingToolBusy = wt.CodingTool = Working || wt.CodingTool = WaitingForUser
        let disabled = syncing || codingToolBusy
        if syncing then
            Html.button [
                prop.className "sync-cancel-btn"
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); callbacks.CancelSync wt.Path)
                prop.text "Cancel"
            ]
        else
            Html.button [
                prop.className (if disabled then "sync-btn disabled" else "sync-btn")
                prop.disabled disabled
                yield! noFocusProps
                prop.onClick (fun e -> e.stopPropagation(); callbacks.StartSync wt.Path scopedKey)
                prop.title (if codingToolBusy then $"{providerDisplayName wt.CodingToolProvider} is active" else $"Sync with {baseBranch} (S)")
                prop.text "Sync"
            ]

let mainBehindWithSync (callbacks: CardCallbacks) (baseBranch: string) (wt: WorktreeStatus) (branchEvents: CardEvent list) (isPending: bool) (scopedKey: string) =
    Html.div [
        prop.className "main-behind-row"
        prop.children [
            mainBehindIndicator baseBranch wt.MainBehindCount
            if wt.MainBehindCount > 0 then
                if wt.IsDirty then
                    Html.span [
                        prop.className "dirty-warning"
                        prop.text "uncommitted changes"
                    ]
                else syncButton callbacks baseBranch wt branchEvents isPending scopedKey
            Html.span [
                prop.className "git-commit-msg"
                prop.children [
                    Html.text wt.LastCommitMessage
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime System.DateTimeOffset.Now wt.LastCommitTime) ]
                ]
            ]
        ]
    ]

let eventLogEntry (onFixTests: (unit -> unit) option) (onConfigureTests: (unit -> unit) option) (evt: CardEvent) =
    let isTestFailure =
        evt.Source = EventSource.Test && (match evt.Status with Some (StepStatus.Failed _) -> true | _ -> false)
    let isTestNotConfigured =
        evt.Source = EventSource.Test && evt.Status = Some StepStatus.NotConfigured
    let isClickable = (isTestFailure && onFixTests.IsSome) || (isTestNotConfigured && onConfigureTests.IsSome)
    Html.div [
        prop.className "event-entry"
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
            Html.span [ prop.className "event-source"; prop.text evt.Source ]
            Html.span [ prop.className "event-message"; prop.text evt.Message ]
            match evt.Status with
            | Some _ ->
                Html.span [
                    prop.className (
                        if isClickable
                        then stepStatusClassName evt.Status + " clickable"
                        else stepStatusClassName evt.Status)
                    prop.text (stepStatusText evt.Status)
                    if isTestFailure then
                        match onFixTests with
                        | Some handler ->
                            prop.title "Click to fix with coding tool"
                            prop.onClick (fun e -> e.stopPropagation(); handler())
                        | None -> ()
                    elif isTestNotConfigured then
                        match onConfigureTests with
                        | Some handler ->
                            prop.title "Click to configure test command"
                            prop.onClick (fun e -> e.stopPropagation(); handler())
                        | None -> ()
                ]
            | None -> Html.none
        ]
    ]

let eventLog (callbacks: CardCallbacks) (cooldowns: Set<WorktreePath>) (wtPath: WorktreePath) (hasTestFailureLog: bool) (events: CardEvent list) =
    match events with
    | [] -> Html.none
    | evts ->
        let onFixTests =
            if not hasTestFailureLog || cooldowns.Contains wtPath then None
            else Some (fun () -> callbacks.LaunchAction wtPath FixTests)
        let onConfigureTests =
            if cooldowns.Contains wtPath then None
            else Some (fun () -> callbacks.LaunchAction wtPath ConfigureTests)
        Html.div [
            prop.className "event-log"
            prop.children (evts |> List.map (eventLogEntry onFixTests onConfigureTests))
        ]

let canvasEventEntry (callbacks: CardCallbacks) (scopedKey: string) (evt: CanvasEvent) =
    let verb = match evt.Kind with NewDoc -> "published" | UpdatedDoc -> "updated"
    Html.div [
        prop.className "event-entry canvas-event"
        prop.onClick (fun e ->
            e.stopPropagation()
            callbacks.OpenCanvasDoc scopedKey evt.Filename)
        prop.children [
            Html.span [ prop.className "event-time"; prop.text (relativeEventTime evt.Timestamp) ]
            Html.span [ prop.className "event-message"; prop.text $"{verb} " ]
            Html.span [ prop.className "event-source"; prop.text evt.Filename ]
        ]
    ]

let canvasEventLog (callbacks: CardCallbacks) (scopedKey: string) (events: CanvasEvent list) =
    match events with
    | [] -> Html.none
    | evts ->
        Html.div [
            prop.className "event-log"
            prop.children (evts |> List.map (canvasEventEntry callbacks scopedKey))
        ]

let abbreviatePipelineName (repoName: string) (name: string) =
    let stripped =
        if name.Length >= repoName.Length && name.StartsWith(repoName, System.StringComparison.OrdinalIgnoreCase)
        then name[repoName.Length..].TrimStart()
        else name
    if stripped.EndsWith(" - pr", System.StringComparison.OrdinalIgnoreCase)
    then stripped[..stripped.Length-6].TrimEnd()
    else stripped

let buildBadge (repoName: string) (build: BuildInfo) =
    let statusText =
        match build.Status with
        | Building -> "Building"
        | Succeeded -> "Passed"
        | Failed -> "Failed"
        | PartiallySucceeded -> "Partial"
        | Canceled -> "Canceled"
    let abbreviated = abbreviatePipelineName repoName build.Name
    let text =
        match build.Failure with
        | Some f -> $"{f.StepName}: {statusText}"
        | None -> if abbreviated = "" then statusText else $"{abbreviated}: {statusText}"
    let className =
        match build.Status with
        | Building -> "build-badge building"
        | Succeeded -> "build-badge succeeded"
        | Failed -> "build-badge failed"
        | PartiallySucceeded -> "build-badge partial"
        | Canceled -> "build-badge canceled"
    let tooltip =
        match build.Failure with
        | Some f when f.Log.Length > 0 -> Some f.Log
        | _ -> None
    match build.Url with
    | Some url ->
        Interop.createElement "a" [
            prop.className className
            prop.text text
            prop.href url
            prop.target "_blank"
            match tooltip with
            | Some t -> prop.title t
            | None -> ()
        ]
    | None ->
        Html.span [
            prop.className className
            prop.text text
            match tooltip with
            | Some t -> prop.title t
            | None -> ()
        ]

let terminalButton (callbacks: CardCallbacks) (wt: WorktreeStatus) =
    let title = if wt.HasActiveSession then "Focus session window (Enter)" else "Open terminal (Enter)"
    Html.button [
        prop.className "terminal-btn"
        prop.title title
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); callbacks.OpenTerminal wt)
        prop.text ">"
    ]

let editorIcon () =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 16, 16)
        svg.fill "currentColor"
        svg.children [
            Svg.path [ svg.d "M5.002 10L12 3l2 2-7 7H5z" ]
            Svg.path [ svg.d "M1.094 0C.525 0 0 .503 0 1.063v13.874C0 15.498.525 16 1.094 16h10.812c.558 0 1.074-.485 1.094-1.031V8l-2 2v4H2V2h5l2 2 1.531-1.531L8.344.344A1.12 1.12 0 007.563 0z" ]
            Svg.path [ svg.d "M14.19 1.011a.513.513 0 00-.364.152l-1.162 1.16 2.004 2.005 1.163-1.162a.514.514 0 000-.728l-1.277-1.275a.514.514 0 00-.364-.152z" ]
        ]
    ]

let editorButton (callbacks: CardCallbacks) editorName (wt: WorktreeStatus) =
    Html.button [
        prop.className "editor-btn"
        prop.title $"Open in {editorName} (E)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); callbacks.OpenEditor wt)
        prop.children [ editorIcon () ]
    ]

let newTabButton (callbacks: CardCallbacks) (wt: WorktreeStatus) =
    Html.button [
        prop.className "new-tab-btn"
        prop.title "Open new tab in tracked window (+)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); callbacks.OpenNewTab wt)
        prop.text "+"
    ]

let resumeIcon () =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 48, 48)
        svg.fill "currentColor"
        svg.children [
            Svg.path [ svg.d "M25.6,25.6,22.2,29,19,25.8l3.4-3.4a2,2,0,0,0-2.8-2.8L16.2,23l-1.3-1.3a1.9,1.9,0,0,0-2.8,0l-3,3a9.8,9.8,0,0,0-3,7,9.1,9.1,0,0,0,1.8,5.6L4.6,40.6a1.9,1.9,0,0,0,0,2.8,1.9,1.9,0,0,0,2.8,0l3.2-3.2a10.1,10.1,0,0,0,5.9,1.9,10.2,10.2,0,0,0,7.1-2.9l3-3a2,2,0,0,0,.6-1.4,1.7,1.7,0,0,0-.6-1.4L25,31.8l3.4-3.4a2,2,0,0,0-2.8-2.8Z" ]
            Svg.path [ svg.d "M43.4,4.6a1.9,1.9,0,0,0-2.8,0L37.2,8a10,10,0,0,0-13,.9l-3,3a2,2,0,0,0-.6,1.4,1.7,1.7,0,0,0,.6,1.4L32.9,26.4a1.9,1.9,0,0,0,2.8,0l3-2.9a9.9,9.9,0,0,0,2.9-7.1A10.4,10.4,0,0,0,40,10.9l3.4-3.5A1.9,1.9,0,0,0,43.4,4.6Z" ]
        ]
    ]

let resumeButton (callbacks: CardCallbacks) (wt: WorktreeStatus) =
    Html.button [
        prop.className "resume-btn"
        prop.title "Resume last session (R)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); callbacks.ResumeSession wt)
        prop.children [ resumeIcon () ]
    ]

let binIcon () =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 24, 24)
        svg.fill "none"
        svg.stroke "currentColor"
        svg.custom ("strokeWidth", "1.5")
        svg.custom ("strokeLinecap", "round")
        svg.children [
            Svg.path [ svg.d "M20.5001 6H3.5" ]
            Svg.path [ svg.d "M9.5 11L10 16" ]
            Svg.path [ svg.d "M14.5 11L14 16" ]
            Svg.path [
                svg.d "M6.5 6C6.55588 6 6.58382 6 6.60915 5.99936C7.43259 5.97849 8.15902 5.45491 8.43922 4.68032C8.44784 4.65649 8.45667 4.62999 8.47434 4.57697L8.57143 4.28571C8.65431 4.03708 8.69575 3.91276 8.75071 3.8072C8.97001 3.38607 9.37574 3.09364 9.84461 3.01877C9.96213 3 10.0932 3 10.3553 3H13.6447C13.9068 3 14.0379 3 14.1554 3.01877C14.6243 3.09364 15.03 3.38607 15.2493 3.8072C15.3043 3.91276 15.3457 4.03708 15.4286 4.28571L15.5257 4.57697C15.5433 4.62992 15.5522 4.65651 15.5608 4.68032C15.841 5.45491 16.5674 5.97849 17.3909 5.99936C17.4162 6 17.4441 6 17.5 6"
                svg.custom ("strokeLinecap", "butt")
            ]
            Svg.path [ svg.d "M18.3735 15.3991C18.1965 18.054 18.108 19.3815 17.243 20.1907C16.378 21 15.0476 21 12.3868 21H11.6134C8.9526 21 7.6222 21 6.75719 20.1907C5.89218 19.3815 5.80368 18.054 5.62669 15.3991L5.16675 8.5M18.8334 8.5L18.6334 11.5" ]
        ]
    ]

let deleteButton (callbacks: CardCallbacks) scopedKey (wt: WorktreeStatus) =
    Html.button [
        prop.className "delete-btn"
        prop.title "Remove worktree (Del)"
        yield! noFocusProps
        prop.onClick (fun e ->
            e.stopPropagation()
            callbacks.DeleteWorktree scopedKey)
        prop.children [ binIcon () ]
    ]

let archiveButton (callbacks: CardCallbacks) scopedKey (wt: WorktreeStatus) =
    Html.button [
        prop.className "archive-btn"
        prop.title "Archive worktree (A)"
        yield! noFocusProps
        prop.onClick (fun e -> e.stopPropagation(); callbacks.ArchiveWorktree scopedKey)
        prop.children [ ArchiveViews.archiveIcon ]
    ]

let conflictIcon () =
    Svg.svg [
        svg.className "conflict-icon"
        svg.viewBox (0, 0, 1920, 1920)
        svg.custom ("role", "img")
        svg.children [
            Svg.title "Merge conflicts"
            Svg.path [
                svg.d "m1359.36 1279.51-79.85 79.85L960 1039.85l-319.398 319.51-79.85-79.85L880.152 960 560.753 640.602l79.85-79.85L960 880.152l319.51-319.398 79.85 79.85L1039.85 960l319.51 319.51ZM960 0C430.645 0 0 430.645 0 960s430.645 960 960 960 960-430.645 960-960S1489.355 0 960 0Z"
                svg.custom ("fillRule", "evenodd")
            ]
        ]
    ]

let prActionButton (callbacks: CardCallbacks) (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (action: ActionKind) (title: string) (icon: ReactElement) =
    let onCooldown = cooldowns.Contains wt.Path
    Html.button [
        prop.className (if onCooldown then "action-btn disabled" else "action-btn")
        prop.disabled onCooldown
        yield! noFocusProps
        prop.title (if onCooldown then "Action already triggered" else title)
        prop.onClick (fun e -> e.stopPropagation(); if not onCooldown then callbacks.LaunchAction wt.Path action)
        prop.children [ icon ]
    ]

let prBadgeContent (callbacks: CardCallbacks) (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) (pr: PrInfo) =
    React.fragment [
        if pr.IsMerged then
            Interop.createElement "a" [
                prop.className "pr-badge merged"
                prop.title pr.Title
                prop.href pr.Url
                prop.target "_blank"
                prop.text "Merged"
            ]
        else
            Interop.createElement "a" [
                prop.className (if pr.IsDraft then "pr-badge draft" else "pr-badge")
                prop.title pr.Title
                prop.href pr.Url
                prop.target "_blank"
                prop.children [
                    Html.text $"PR #{pr.Id}"
                    if pr.HasConflicts then conflictIcon ()
                ]
            ]
            match pr.Comments with
            | WithResolution (unresolved, total) when total > 0 ->
                Html.span [
                    prop.className (if unresolved = 0 then "thread-badge dimmed" else "thread-badge")
                    prop.text ($"{unresolved}/{total} threads")
                ]
                if unresolved > 0 then
                    prActionButton callbacks cooldowns wt (FixPr pr.Url) "Fix PR comments" commentIcon
            | _ -> ()
            yield! pr.Builds |> List.collect (fun build -> [
                    buildBadge repoName build
                    if build.Status = Failed then
                        match build.Url with
                        | Some url -> prActionButton callbacks cooldowns wt (FixBuild url) "Fix build" wrenchIcon
                        | None -> ()
                ])
    ]

let prSection (callbacks: CardCallbacks) (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) =
    match wt.Pr with
    | NoPr -> Html.none
    | HasPr pr -> prBadgeContent callbacks cooldowns wt repoName pr

let prRow (callbacks: CardCallbacks) (cooldowns: Set<WorktreePath>) (wt: WorktreeStatus) (repoName: string) =
    match wt.Pr, wt.Branch with
    | NoPr, ("main" | "master") -> Html.none
    | NoPr, _ ->
        Html.div [
            prop.className "pr-row"
            prop.children [
                prActionButton callbacks cooldowns wt CreatePr "Create PR" createPrIcon
            ]
        ]
    | HasPr pr, _ ->
        Html.div [
            prop.className "pr-row"
            prop.children [ prBadgeContent callbacks cooldowns wt repoName pr ]
        ]

let canResumeSession (wt: WorktreeStatus) =
    not wt.HasActiveSession
    && wt.LastUserMessage.IsSome
    && wt.CodingTool <> Working
    && wt.CodingTool <> WaitingForUser

/// The card's intent line: the agent's current intent (SDK `assistant.intent`) plus, when a skill is
/// running, that skill as a pill. Kept as a pure decision (not a ReactElement) so the presence logic
/// is unit-testable without rendering React. `Line` carries at least one of intent/skill; `Empty`
/// when neither is present. A blank/whitespace skill is treated as no skill.
[<RequireQualifiedAccess>]
type CardIntentLine =
    | Line of intent: (string * System.DateTimeOffset) option * skill: string option
    | Empty

let cardIntentLine (wt: WorktreeStatus) : CardIntentLine =
    let skill =
        wt.CurrentSkill
        |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Option.map _.Trim()
    match wt.AgentIntent, skill with
    | None, None -> CardIntentLine.Empty
    | intent, sk -> CardIntentLine.Line(intent, sk)

/// Line 1 of the footer: the intent text (with the time it last changed) and the running skill as a
/// right-aligned pill. Reuses `.user-prompt` for layout; the pill and intent text carry their own
/// classes. Renders nothing when there is neither an intent nor a skill.
let intentLineView (wt: WorktreeStatus) =
    match cardIntentLine wt with
    | CardIntentLine.Empty -> Html.none
    | CardIntentLine.Line (intent, skill) ->
        Html.div [
            prop.className "user-prompt intent-line"
            prop.children [
                match intent with
                | Some (text, since) ->
                    Html.span [ prop.className "event-time"; prop.text (relativeEventTime since) ]
                    Html.span [ prop.className "intent-text"; prop.text text ]
                | None -> ()
                match skill with
                | Some name -> Html.span [ prop.className "skill-pill"; prop.text $"▶ {name}" ]
                | None -> ()
            ]
        ]

/// A footer message line: `[time-ago] <source?> <text>`. Shared by the last-user-message and
/// last-assistant-message lines; the assistant line tags its source ("copilot"), the user line does not.
let private messageLineView (source: string option) (msg: (string * System.DateTimeOffset) option) =
    match msg with
    | None -> Html.none
    | Some (text, ts) ->
        Html.div [
            prop.className "user-prompt"
            prop.children [
                Html.span [ prop.className "event-time"; prop.text (relativeEventTime ts) ]
                match source with
                | Some s -> Html.span [ prop.className "event-source"; prop.text s ]
                | None -> ()
                Html.span [ prop.text text ]
            ]
        ]

/// Line 2 (last user message) and line 3 (last assistant message, tagged `copilot`) of the footer.
let userMsgLineView (wt: WorktreeStatus) = messageLineView None wt.LastUserMessage
let assistantMsgLineView (wt: WorktreeStatus) = messageLineView (Some "copilot") wt.LastAssistantMessage

let compactWorktreeCard (props: CardViewProps) (callbacks: CardCallbacks) (repoName: string) (baseBranch: string) (scopedKey: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt + " compact"
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.key (WorktreePath.value wt.Path)
        prop.className className
        prop.onClick (fun _ -> callbacks.FocusCard scopedKey)
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.div [
                        prop.className "header-info"
                        prop.children [
                            Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]
                            Html.span [ prop.className "branch-name"; prop.text (cardTitle wt) ]
                            FitOrHide (workMetricsItems wt.WorkMetrics)
                        ]
                    ]
                    Html.span [ prop.className "commit-time"; prop.text (relativeTime System.DateTimeOffset.Now wt.LastCommitTime) ]
                    terminalButton callbacks wt
                    if wt.HasActiveSession then newTabButton callbacks wt
                    if canResumeSession wt then resumeButton callbacks wt
                    editorButton callbacks props.EditorName wt
                    archiveButton callbacks scopedKey wt
                    if not wt.IsMainWorktree then deleteButton callbacks scopedKey wt
                ]
            ]
            Html.div [
                prop.className "compact-detail"
                prop.children [
                    if beadsTotal wt.Beads > 0 then beadsCounts "beads-inline" wt.Beads
                    mainBehindIndicator baseBranch wt.MainBehindCount
                    prSection callbacks props.ActionCooldowns wt repoName
                ]
            ]
        ]
    ]

let worktreeCard (props: CardViewProps) (callbacks: CardCallbacks) (repoName: string) (baseBranch: string) (branchEvents: CardEvent list) (canvasEvents: CanvasEvent list) (isPending: bool) (scopedKey: string) (isFocused: bool) (wt: WorktreeStatus) =
    let baseClass = cardClassName wt
    let className = if isFocused then baseClass + " focused" else baseClass
    let hasFooterLines =
        (match cardIntentLine wt with CardIntentLine.Empty -> false | _ -> true)
        || wt.LastUserMessage.IsSome
        || wt.LastAssistantMessage.IsSome
    let visibleBranchEvents = branchEvents |> List.filter isVisibleCardEvent
    let hasContent = hasFooterLines || (not (List.isEmpty visibleBranchEvents)) || (not (List.isEmpty canvasEvents))
    let footerClass = if hasContent then "card-footer has-content" else "card-footer"
    Html.div [
        prop.key (WorktreePath.value wt.Path)
        prop.className className
        prop.onClick (fun _ -> callbacks.FocusCard scopedKey)
        prop.children [
            Html.div [
                prop.className "card-body"
                prop.children [
                    Html.div [
                        prop.className "card-header"
                        prop.children [
                            Html.div [
                                prop.className "header-info"
                                prop.children [
                                    Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]
                                    Html.span [ prop.className "branch-name"; prop.text (cardTitle wt) ]
                                    FitOrHide (workMetricsItems wt.WorkMetrics)
                                ]
                            ]
                            terminalButton callbacks wt
                            if wt.HasActiveSession then newTabButton callbacks wt
                            if canResumeSession wt then resumeButton callbacks wt
                            editorButton callbacks props.EditorName wt
                            archiveButton callbacks scopedKey wt
                            if not wt.IsMainWorktree then deleteButton callbacks scopedKey wt
                        ]
                    ]

                    if beadsTotal wt.Beads > 0 then
                        Html.div [
                            prop.className "beads-row"
                            prop.children [
                                beadsCounts "beads-counts" wt.Beads
                                beadsProgressBar wt.Beads
                            ]
                        ]

                    mainBehindWithSync callbacks baseBranch wt branchEvents isPending scopedKey

                    prRow callbacks props.ActionCooldowns wt repoName
                ]
            ]

            Html.div [
                prop.className footerClass
                prop.children [
                    if List.isEmpty canvasEvents then
                        intentLineView wt
                        userMsgLineView wt
                        assistantMsgLineView wt

                    eventLog callbacks props.ActionCooldowns wt.Path wt.HasTestFailureLog visibleBranchEvents
                    canvasEventLog callbacks scopedKey canvasEvents
                ]
            ]
        ]
    ]

let renderCard (props: CardViewProps) (callbacks: CardCallbacks) (repoName: string) (baseBranch: string) (wt: WorktreeStatus) =
    let scopedKey = WorktreePath.value wt.Path
    let events = props.BranchEvents |> Map.tryFind scopedKey |> Option.defaultValue []
    let cvEvents = props.CanvasEvents |> Map.tryFind scopedKey |> Option.defaultValue []
    let isPending = props.SyncPending |> Set.contains scopedKey
    let isFocused = props.FocusedElement = Some (Card scopedKey)
    if props.IsCompact then compactWorktreeCard props callbacks repoName baseBranch scopedKey isFocused wt
    else worktreeCard props callbacks repoName baseBranch events cvEvents isPending scopedKey isFocused wt

let skeletonCard () =
    Html.div [
        prop.className "wt-card skeleton"
        prop.children [
            Html.div [
                prop.className "card-header"
                prop.children [
                    Html.span [ prop.className "skeleton-dot" ]
                    Html.span [ prop.className "skeleton-bar skeleton-branch" ]
                ]
            ]
            Html.div [ prop.className "skeleton-bar skeleton-commit" ]
            Html.div [ prop.className "skeleton-bar skeleton-beads" ]
        ]
    ]

let skeletonGrid () =
    Html.div [
        prop.className "card-grid"
        prop.children (List.init 6 (fun _ -> skeletonCard ()))
    ]

let sortLabel =
    function
    | ByName -> "A-Z"
    | ByActivity -> "Recent"

let providerIcon (provider: RepoProvider option) =
    let icon viewBox (svgPath: string) =
        Svg.svg [
            svg.className "provider-icon"
            svg.viewBox viewBox
            svg.children [ Svg.path [ svg.d svgPath; svg.fill "currentColor" ] ]
        ]

    let githubPath = "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"
    let azdoPath = "M17 4v9.74l-4 3.28-6.2-2.26V17l-3.51-4.59 10.23.8V4.44zm-3.41.49L7.85 1v2.29L2.58 4.84 1 6.87v4.61l2.26 1V6.57z"

    match provider with
    | None | Some UnknownProvider -> Html.none
    | Some(GitHubProvider url) ->
        Html.a [
            prop.className "provider-link"
            prop.href url
            prop.target "_blank"
            prop.onClick (fun e -> e.stopPropagation())
            prop.children [ icon (0, 0, 24, 24) githubPath ]
        ]
    | Some(AzDoProvider url) ->
        Html.a [
            prop.className "provider-link"
            prop.href url
            prop.target "_blank"
            prop.onClick (fun e -> e.stopPropagation())
            prop.children [ icon (0, 0, 18, 18) azdoPath ]
        ]

let repoSectionHeader (callbacks: CardCallbacks) (focusedElement: FocusTarget option) (repo: RepoModel) =
    let arrow = if repo.IsCollapsed then "\u25B6" else "\u25BC"
    let isFocused = focusedElement = Some (RepoHeader repo.RepoId)
    let baseClass = if repo.IsCollapsed then "repo-header collapsed" else "repo-header"
    let className = if isFocused then baseClass + " focused" else baseClass
    Html.div [
        prop.className className
        prop.onClick (fun _ -> callbacks.ToggleRepo repo.RepoId)
        prop.children [
            Html.span [ prop.className "collapse-arrow"; prop.text arrow ]
            Html.span [ prop.className "repo-name"; prop.text repo.Name ]
            providerIcon repo.Provider
            if repo.BaseBranch <> "main" then
                Html.span [ prop.className "deploy-branch"; prop.text repo.BaseBranch ]
            if repo.IsCollapsed then
                Html.span [
                    prop.className "repo-ct-dots"
                    prop.children (
                        repo.Worktrees
                        |> List.map (fun wt ->
                            Html.span [ prop.className ($"ct-dot {ctClassName wt.CodingTool}"); prop.title (ctTooltip wt.CodingTool) ]))
                ]
            Html.button [
                prop.className "create-wt-btn"
                prop.title "Create worktree"
                prop.onClick (fun e -> e.stopPropagation(); callbacks.CreateWorktree repo.RepoId)
                prop.text "+"
            ]
        ]
    ]

let repoSection (props: CardViewProps) (callbacks: CardCallbacks) (repo: RepoModel) =
    Html.div [
        prop.key (RepoId.value repo.RepoId)
        prop.className "repo-section"
        prop.children [
            repoSectionHeader callbacks props.FocusedElement repo
            if not repo.IsCollapsed then
                if not repo.IsReady && repo.Worktrees.IsEmpty then
                    skeletonGrid ()
                else
                    Html.div [
                        prop.className "card-grid"
                        prop.children (repo.Worktrees |> List.map (renderCard props callbacks repo.Name repo.BaseBranch))
                    ]
                    ArchiveViews.archiveSection callbacks.DispatchArchive repo.ArchivedWorktrees
        ]
    ]
