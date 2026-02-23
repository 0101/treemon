module Server.CodingToolStatus

open Shared

let getStatus (worktreePath: string) : CodingToolStatus * CodingToolProvider option =
    let status = ClaudeDetector.getStatus worktreePath
    status, Some Claude

let getLastMessage (worktreePath: string) : CardEvent option =
    ClaudeDetector.getLastMessage worktreePath
