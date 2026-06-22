module MascotView

// The mascot eye SVGs, extracted from `App.fs`. Pure render functions where gaze comes from
// the mascot's `EyeDirection`, while the lid overlay comes from the observed `ActivityLevel`.
// Compiled before `AppTypes.fs`, so these depend only on `Shared`/`Feliz` — never on `Model`/`Msg`.

open Shared
open Feliz

/// Open, awake eye. `pupilColor` is derived by the header from worktree state (waiting vs not),
/// so it stays a separate argument; gaze offset comes from the mascot's `EyeDirection`, and the
/// half/closed lid overlay comes from the observed `ActivityLevel`.
let viewEyeOpen (pupilColor: string) (activityLevel: ActivityLevel) (eyeDirection: float * float) =
    let (dx, dy) = eyeDirection
    let activity = activityLevel
    Svg.svg [
        svg.className "eye-logo"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.path [
                svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.g [
                svg.className "eye-iris"
                svg.custom ("transform", $"translate({dx}, {dy})")
                svg.children [
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 9
                        svg.fill "#1a1b2e"
                    ]
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 6
                        svg.fill "#56b6c2"
                    ]
                    Svg.circle [
                        svg.cx 20
                        svg.cy 10
                        svg.r 3
                        svg.fill pupilColor
                    ]
                ]
            ]
            Svg.circle [
                svg.cx 23
                svg.cy 5
                svg.r 2
                svg.fill "rgba(255, 255, 255, 0.8)"
            ]
            match activity with
            | ActivityLevel.Idle ->
                Svg.path [
                    svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 4 20 5 Q10 4 2 10 Z"
                    svg.fill "#b0b0b0"
                ]
                Svg.path [
                    svg.d "M2 10 Q10 4 20 5 Q30 4 38 10"
                    svg.fill "none"
                    svg.stroke "#56b6c2"
                    svg.strokeWidth 2.0
                ]
            | ActivityLevel.DeepIdle ->
                Svg.path [
                    svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 9 20 12 Q10 9 2 10 Z"
                    svg.fill "#b0b0b0"
                ]
                Svg.path [
                    svg.d "M2 10 Q10 9 20 12 Q30 9 38 10"
                    svg.fill "none"
                    svg.stroke "#56b6c2"
                    svg.strokeWidth 2.0
                ]
            | ActivityLevel.Active -> ()
        ]
    ]

let viewEyeRolledBack () =
    Svg.svg [
        svg.className "eye-logo"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.defs [
                Svg.clipPath [
                    svg.id "eye-shape"
                    svg.children [
                        Svg.path [
                            svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                        ]
                    ]
                ]
            ]
            Svg.path [
                svg.d "M2 10 Q10 0 20 0 Q30 0 38 10 Q30 20 20 20 Q10 20 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.g [
                svg.custom ("clipPath", "url(#eye-shape)")
                svg.children [
                    Svg.g [
                        svg.custom ("transform", "translate(0, -9)")
                        svg.children [
                            Svg.circle [
                                svg.cx 20
                                svg.cy 10
                                svg.r 9
                                svg.fill "#1a1b2e"
                            ]
                            Svg.circle [
                                svg.cx 20
                                svg.cy 10
                                svg.r 6
                                svg.fill "#888"
                            ]
                            Svg.circle [
                                svg.cx 20
                                svg.cy 10
                                svg.r 3
                                svg.fill "#1a1b2e"
                            ]
                        ]
                    ]
                ]
            ]
            Svg.circle [
                svg.cx 23
                svg.cy 5
                svg.r 2
                svg.fill "rgba(255, 255, 255, 0.8)"
            ]
        ]
    ]

let viewEyeClosed () =
    Svg.svg [
        svg.className "eye-logo eye-closed"
        svg.viewBox (-2, -2, 44, 24)
        svg.children [
            Svg.path [
                svg.d "M2 10 Q10 4 20 4 Q30 4 38 10 Q30 16 20 16 Q10 16 2 10 Z"
                svg.fill "#e8e8e8"
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.5
            ]
            Svg.line [
                svg.x1 4
                svg.y1 10
                svg.x2 36
                svg.y2 10
                svg.stroke "#56b6c2"
                svg.strokeWidth 2.0
            ]
        ]
    ]
