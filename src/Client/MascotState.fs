module MascotState

open Shared

/// The mascot eyes' slice of the dashboard model. Grouped out of App.Model so the
/// mascot's owned state (gaze direction + activity tracking) and its pure helpers live
/// together, away from core worktree/repo concerns (mirrors how CanvasState nests the
/// canvas pane's state into Model).
type MascotState =
    { EyeDirection: float * float
      LastActivityTime: float
      ActivityLevel: ActivityLevel }

/// Initial mascot state: eyes centered, presence Active. The live first-load timestamp is
/// stamped in App.init via Date.now (); kept 0.0 here so this value carries no Fable runtime
/// call at module load (which would trip the .NET test host's static initializer, the same
/// hazard that made worktreeApi lazy — see AppTypes.fs).
let empty : MascotState =
    { EyeDirection = (0.0, 0.0)
      LastActivityTime = 0.0
      ActivityLevel = ActivityLevel.Active }

let private rng = System.Random()

/// A small random gaze offset (dx in [-1.5, 1.5], dy in [-1.0, 1.0]), re-rolled on each
/// data refresh so the eyes subtly drift around.
let randomEyeDirection () =
    let dx = rng.NextDouble() * 3.0 - 1.5
    let dy = rng.NextDouble() * 2.0 - 1.0
    (dx, dy)

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
