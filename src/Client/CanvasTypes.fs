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

[<RequireQualifiedAccess>]
type CanvasShareState =
    | Idle
    | Publishing of scopedKey: string * filename: string
    | WritingClipboard of scopedKey: string * filename: string

module CanvasShareState =
    let buttonFlags (activeScopedKey: string option) (filename: string) (shareState: CanvasShareState) =
        let isSharing =
            match activeScopedKey, shareState with
            | Some currentScopedKey, CanvasShareState.Publishing (sharingScopedKey, sharingFilename)
            | Some currentScopedKey, CanvasShareState.WritingClipboard (sharingScopedKey, sharingFilename) ->
                currentScopedKey = sharingScopedKey && filename = sharingFilename
            | _ -> false
        shareState <> CanvasShareState.Idle, isSharing

[<RequireQualifiedAccess>]
type CanvasPathCopyState =
    | Idle of revision: int
    | Copying of scopedKey: string * filename: string * revision: int
    | Copied of scopedKey: string * filename: string * revision: int

module CanvasPathCopyState =
    let revision = function
        | CanvasPathCopyState.Idle value
        | CanvasPathCopyState.Copying (_, _, value)
        | CanvasPathCopyState.Copied (_, _, value) -> value

    let isCopied (activeScopedKey: string option) (filename: string) = function
        | CanvasPathCopyState.Copied (scopedKey, copiedFilename, _) ->
            activeScopedKey = Some scopedKey && filename = copiedFilename
        | _ -> false

    let isCopying = function
        | CanvasPathCopyState.Copying _ -> true
        | _ -> false

/// A doc-scoped banner error stamped with the worktree + the doc it is attributed to. Two producers
/// feed it: (1) a doc-side JS error (window.onerror / unhandledrejection forwarded from an AgentDoc
/// iframe via the injected errorOverlayScript), which self-identifies via the postMessage `wt`/`doc`
/// fields (the overlay derives the worktree from its own location.pathname, mirroring the bridge
/// heartbeat, and is served per-doc) and is validated against that worktree's docs before being
/// stored, so it is attributed to the doc that actually threw — independent of the active tab — and a
/// stale/forged identity is dropped; and (2) a malformed/unroutable doc message (no usable string
/// `action`), which carries no self-identifying fields and is attributed to the active visible doc.
/// Either way the pane shows the banner only while that same doc is focused (CanvasPane.docErrorBanner
/// gates on ScopedKey+Filename), so card/tab/keyboard navigation to any OTHER doc auto-hides a stale
/// error — truly doc-scoped — without a clear in every focus reducer.
type DocJsError =
    { ScopedKey: string
      Filename: string
      Message: string }

/// Identity and target content version for one in-place AgentDoc morph.
type CanvasMorph =
    { ScopedKey: string
      Filename: string
      ContentHash: string }
