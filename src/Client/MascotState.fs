module MascotState

/// The mascot eyes' slice of the dashboard model: just the gaze direction. The activity/idle
/// state that used to live here moved to `ActivityState` (it drives polling/telemetry/canvas, not
/// the eyes); the eyes merely *observe* `ActivityLevel`. Mirrors how `CanvasState` nests a
/// feature's state into `Model`.
type MascotState =
    { EyeDirection: float * float }

let empty : MascotState =
    { EyeDirection = (0.0, 0.0) }

let private rng = System.Random()

/// A small random gaze offset (dx in [-1.5, 1.5], dy in [-1.0, 1.0]), re-rolled on each
/// data refresh so the eyes subtly drift around.
let randomEyeDirection () =
    let dx = rng.NextDouble() * 3.0 - 1.5
    let dy = rng.NextDouble() * 2.0 - 1.0
    (dx, dy)
