module CanvasTypes

// Canvas-specific shared types, owned here (compiled after Navigation, before CanvasPane) rather than
// parked in the focus/navigation-scoped Navigation module. Imported by the canvas pane, state, update
// and awareness modules.

[<RequireQualifiedAccess>]
type CanvasSendState =
    // scopedKey identifies the target worktree the message was queued for, so the "Waiting for
    // session…" banner can be cleared only by *that* worktree's session activity, never by an
    // unrelated worktree's doc change.
    | Idle
    | Waiting of scopedKey: string
    | Failed of message: string

/// A doc-side JS error (window.onerror / unhandledrejection forwarded from an AgentDoc iframe via the
/// injected errorOverlayScript), stamped with the worktree + the doc that EMITTED it. Both ride along
/// in the postMessage: the worktree in the `wt` field (the overlay derives it from its own
/// location.pathname, mirroring the bridge heartbeat) and the filename in the `doc` field (the overlay
/// is served per-doc). They are validated against that worktree's docs before being stored, so the
/// error is attributed to the doc that actually threw — independent of the active tab — and a
/// stale/forged identity is dropped. The pane shows the banner only while that same doc is focused
/// (CanvasPane.docErrorBanner gates on ScopedKey+Filename), so card/tab/keyboard navigation to any
/// OTHER doc auto-hides a stale error — truly doc-scoped — without a clear in every focus reducer.
type DocJsError =
    { ScopedKey: string
      Filename: string
      Message: string }
