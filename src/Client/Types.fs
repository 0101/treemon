module Client.Types

open Shared

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

type ScrollHint = Normal | ScrollToTop | ScrollToBottom
