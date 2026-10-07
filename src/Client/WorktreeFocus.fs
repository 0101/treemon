module WorktreeFocus

open System
open Shared
open Navigation

[<RequireQualifiedAccess>]
type Window =
    | Off
    | Hours2
    | Hours8
    | Hours24

let nextWindow =
    function
    | Window.Off -> Window.Hours2
    | Window.Hours2 -> Window.Hours8
    | Window.Hours8 -> Window.Hours24
    | Window.Hours24 -> Window.Off

let label =
    function
    | Window.Off -> "off"
    | Window.Hours2 -> "2h"
    | Window.Hours8 -> "8h"
    | Window.Hours24 -> "24h"

let isEnabled =
    function
    | Window.Off -> false
    | Window.Hours2
    | Window.Hours8
    | Window.Hours24 -> true

let private duration =
    function
    | Window.Off -> None
    | Window.Hours2 -> Some(TimeSpan.FromHours 2.0)
    | Window.Hours8 -> Some(TimeSpan.FromHours 8.0)
    | Window.Hours24 -> Some(TimeSpan.FromHours 24.0)

let includes window (now: DateTimeOffset) (worktree: WorktreeStatus) =
    match duration window with
    | None -> true
    | Some duration ->
        worktree.SessionActivityAt
        |> Option.exists (fun activityAt -> activityAt >= now - duration)

/// Project both card lists without changing repository controls, ordering, or the source data.
let project window now (repos: RepoModel list) =
    if not (isEnabled window) then repos
    else
        repos
        |> List.map (fun repo ->
            { repo with
                Worktrees = repo.Worktrees |> List.filter (includes window now)
                ArchivedWorktrees = repo.ArchivedWorktrees |> List.filter (includes window now) })
