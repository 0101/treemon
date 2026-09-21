namespace Shared

/// `postMessage` vocabulary of the proxied terminal page: the shortcuts it forwards to the
/// dashboard and the focus request the dashboard sends back. The terminal host injects these
/// literals into ttyd's page and the client matches them, so a rename fails the build on both
/// sides instead of leaving one end posting a message the other silently ignores.
[<RequireQualifiedAccess>]
module TerminalPageMessage =
    [<Literal>]
    let OpenWorktreeSearch = "open-worktree-search"

    [<Literal>]
    let CycleTerminal = "cycle-terminal"

    [<Literal>]
    let CloseTerminal = "close-terminal"

    [<Literal>]
    let StartTerminal = "start-terminal"

    [<Literal>]
    let FocusTerminal = "focus-terminal"

    [<Literal>]
    let TerminalVisible = "treemon-terminal-visible"

    [<Literal>]
    let NextDirection = "next"

    [<Literal>]
    let PreviousDirection = "previous"
