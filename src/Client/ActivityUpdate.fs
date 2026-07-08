module ActivityUpdate

// User-activity / idle-detection update-arm bodies plus the activity-detection subscription,
// extracted from `App.fs`. Mirrors `CanvasUpdate.fs`: body extraction only — the root `update`
// keeps the flat `match` and delegates to these functions (no sub-`Msg`/`Cmd.map` split). `Tick`
// stays in the root arm because it also expires canvas events and drives the worktree/sync poll;
// only its activity-recompute lifts here. Compiled after `AppTypes.fs` (which holds `Model`/`Msg`)
// and before `App.fs`. See docs/spec/user-idle-detection.md.

open Shared
open Elmish
open Browser
open AppTypes

/// `Tick` activity-recompute: derive the new `ActivityLevel` from the elapsed idle time and emit a
/// presence report only when it actually changed. Returns the updated activity slice and that
/// `Cmd`; the root `Tick` arm threads the slice into the model alongside its canvas-event expiry
/// and poll fetches (the reason `Tick` does not delegate wholesale).
let tickActivity (now: float) (activity: ActivityState.ActivityState) : ActivityState.ActivityState * Cmd<Msg> =
    let newLevel = ActivityState.computeActivityLevel activity.LastActivityTime now

    let reportCmd =
        if newLevel <> activity.ActivityLevel then
            Cmd.OfAsync.attempt worktreeApi.Value.reportActivity newLevel (fun _ -> NoOp)
        else
            Cmd.none

    { activity with ActivityLevel = newLevel }, reportCmd

/// `UserActivity` arm body: stamp the latest activity time and force the level back to `Active`.
/// Only when waking from a non-`Active` level does it kick an immediate `Tick` + presence report,
/// so the poll cadence and server presence resync without waiting for the next interval.
let userActivity (now: float) (model: Model) : Model * Cmd<Msg> =
    let wasActive = model.Activity.ActivityLevel = ActivityLevel.Active

    let wakeUpCmd =
        if not wasActive then
            Cmd.batch [
                Cmd.ofMsg (Tick now)
                Cmd.OfAsync.attempt worktreeApi.Value.reportActivity ActivityLevel.Active (fun _ -> NoOp)
            ]
        else
            Cmd.none

    { model with Activity.LastActivityTime = now; Activity.ActivityLevel = ActivityLevel.Active },
    wakeUpCmd

/// Activity-detection subscription: dispatches `UserActivity` on user input
/// (mousemove/keydown/click/scroll), throttled to once per 5s, and removes its listeners on
/// dispose. Mirrors `CanvasUpdate.messageListener` as the activity entry in `appSubscriptions`.
let activityDetection (dispatch: Dispatch<Msg>) =
    // Throttle timestamp confined to this subscription closure — Elmish's designated impure
    // boundary (same pattern as setInterval). An immutable alternative would have to re-register
    // the DOM listeners on every event; `let mutable` (never a ref cell) is the right tool here.
    let mutable lastDispatchTime = Fable.Core.JS.Constructors.Date.now ()
    let throttleMs = 5000.0

    let handler =
        fun (_: Browser.Types.Event) ->
            let now = Fable.Core.JS.Constructors.Date.now ()
            if now - lastDispatchTime >= throttleMs then
                lastDispatchTime <- now
                dispatch (UserActivity now)

    let events = [| "mousemove"; "keydown"; "click"; "scroll" |]
    events |> Array.iter (fun evt -> Dom.document.addEventListener (evt, handler))

    { new System.IDisposable with
        member _.Dispose() =
            events |> Array.iter (fun evt -> Dom.document.removeEventListener (evt, handler)) }
