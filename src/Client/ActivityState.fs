module ActivityState

open Shared

/// The user-activity / idle-detection slice of the dashboard model: the timestamp of the last
/// user interaction and the derived ActivityLevel. Owned here (not on the Mascot) because this
/// state drives the refresh-poll cadence, server presence reporting, and the canvas auto-display
/// idle gate — the mascot eyes are only one *observer* of ActivityLevel. See
/// docs/spec/user-idle-detection.md.
type ActivityState =
    { LastActivityTime: float
      ActivityLevel: ActivityLevel }

/// Initial activity state: presence Active. The live first-load timestamp is stamped in App.init
/// via Date.now (); kept 0.0 here so this value carries no Fable runtime call at module load
/// (which would trip the .NET test host's static initializer — see AppTypes.fs worktreeApi).
let empty : ActivityState =
    { LastActivityTime = 0.0
      ActivityLevel = ActivityLevel.Active }

/// Idle thresholds in ms since the last user activity. idleThresholdMs / deepIdleThresholdMs
/// bucket the elapsed time into the ActivityLevel that drives presence reporting and the
/// refresh-poll cadence; autoDisplayIdleMs is the shorter idle window after which a changed
/// agent doc may auto-steal focus into the canvas.
let idleThresholdMs = 180_000.0
let deepIdleThresholdMs = 900_000.0
let autoDisplayIdleMs = 60_000.0

/// Activity level from elapsed idle time:
/// Active (< idleThresholdMs) -> Idle (< deepIdleThresholdMs) -> DeepIdle.
let computeActivityLevel (lastActivityTime: float) (now: float) =
    let elapsed = now - lastActivityTime

    if elapsed < idleThresholdMs then ActivityLevel.Active
    elif elapsed < deepIdleThresholdMs then ActivityLevel.Idle
    else ActivityLevel.DeepIdle
