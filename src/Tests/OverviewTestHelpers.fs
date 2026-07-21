module Tests.OverviewTestHelpers

open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityStore

let evt id sid worktree status skill at : ActivityEventRow =
    { EventId = EventId id
      SessionId = SessionId sid
      WorktreePath = WorktreePath worktree
      Provider = CopilotCli
      Kind = "status"
      Status = status
      Skill = skill
      Ts = at }
