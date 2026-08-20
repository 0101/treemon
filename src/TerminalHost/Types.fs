namespace TerminalHost

open System

[<RequireQualifiedAccess>]
module Protocol =
    [<Literal>]
    let ControlApiVersion = 1

    [<Literal>]
    let MaximumRequestBodyBytes = 16_384L

type CanonicalWorktree =
    private
        { Path: string
          Key: string }

type TerminalProcess =
    { ProcessId: int
      ProcessStartTimeUtcTicks: int64
      TtydPort: int
      HasExited: unit -> bool
      Close: unit -> unit }

type TerminalDataPlane =
    { Upstream: System.Net.WebSockets.ClientWebSocket option
      Attachment: System.Net.WebSockets.WebSocket option
      Replay: byte array }

type TerminalRecord =
    { SessionId: string
      WorktreePath: string }

type RegistrySnapshot =
    { Revision: int64
      Terminals: TerminalRecord list }

type HostIdentity =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      Endpoint: string
      HostVersion: string
      ControlApiVersion: int }

type HostManifest =
    { Identity: HostIdentity
      BearerToken: string
      StagedExecutableVersion: string option }

[<RequireQualifiedAccess>]
module CanonicalWorktree =
    let internal create path key = { Path = path; Key = key }
    let path worktree = worktree.Path
    let internal key worktree = worktree.Key

[<RequireQualifiedAccess>]
module TerminalDataPlane =
    let empty =
        { Upstream = None
          Attachment = None
          Replay = Array.empty }
