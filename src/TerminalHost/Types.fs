namespace TerminalHost

open System

[<RequireQualifiedAccess>]
module Protocol =
    [<Literal>]
    let ControlApiVersion = 2

    [<Literal>]
    let MaximumRequestBodyBytes = 16_384L

    [<Literal>]
    let MaximumReplayBytes = 1_048_576

    [<Literal>]
    let MaximumAttachmentMessageBytes = 16_384

type CanonicalWorktree =
    private
        { Path: string }

type TerminalProcess =
    { ProcessId: int
      ProcessStartTimeUtcTicks: int64
      TtydPort: int
      HasExited: unit -> bool
      Close: unit -> unit }

type TerminalRecord =
    { SessionId: string
      WorktreePath: string
      AttachmentEndpoint: string }

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
    let internal create path = { Path = path }
    let path worktree = worktree.Path
