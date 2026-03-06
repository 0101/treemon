module ActionButtons

open Shared
open Feliz

let noFocusProps = [
    prop.tabIndex -1
    prop.onMouseDown (fun e -> e.preventDefault())
    prop.onKeyDown (fun e -> if e.key = "Enter" || e.key = " " then e.preventDefault())
]

let isCodingToolBusy (wt: WorktreeStatus) =
    wt.CodingTool = Working || wt.CodingTool = WaitingForUser

let commentIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 32, 32)
        svg.fill "currentColor"
        svg.children [
            Svg.path [
                svg.d "M24,16 C22.896,16 22,15.104 22,14 C22,12.896 22.896,12 24,12 C25.104,12 26,12.896 26,14 C26,15.104 25.104,16 24,16 L24,16 Z M16,16 C14.896,16 14,15.104 14,14 C14,12.896 14.896,12 16,12 C17.104,12 18,12.896 18,14 C18,15.104 17.104,16 16,16 L16,16 Z M8,16 C6.896,16 6,15.104 6,14 C6,12.896 6.896,12 8,12 C9.104,12 10,12.896 10,14 C10,15.104 9.104,16 8,16 L8,16 Z M16,0 C7.164,0 0,6.269 0,14 C0,18.419 2.345,22.354 6,24.919 L6,32 L13.009,27.747 C13.979,27.907 14.977,28 16,28 C24.836,28 32,21.732 32,14 C32,6.269 24.836,0 16,0 L16,0 Z"
            ]
        ]
    ]

let wrenchIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 64, 64)
        svg.fill "currentColor"
        svg.children [
            Svg.path [ svg.d "M44.575,55.581l-16.411,-16.436c0,0 -8.462,2.97 -15.352,-3.931c-7.896,-7.908 -3.505,-17.121 -3.505,-17.121c0,0 8.199,8.117 10.347,10.269c1.71,1.713 3.686,1.47 7.043,-1.892c2.54,-2.544 3.233,-5.108 1.59,-6.754c-2.288,-2.291 -10.347,-10.269 -10.347,-10.269c0,0 9.763,-4.66 17.508,3.097c6.884,6.894 4.282,15.017 4.122,15.178c-0.064,0.063 7.343,7.509 16.341,16.505l-11.336,11.354Z" ]
        ]
    ]

let createPrIcon =
    Svg.svg [
        svg.className "btn-icon"
        svg.viewBox (0, 0, 15, 15)
        svg.fill "currentColor"
        svg.children [
            Svg.path [ svg.d "M2.5 0C1.11929 0 0 1.11929 0 2.5C0 3.70948 0.85888 4.71836 2 4.94999V10.05C0.85888 10.2816 0 11.2905 0 12.5C0 13.8807 1.11929 15 2.5 15C3.88071 15 5 13.8807 5 12.5C5 11.2905 4.14112 10.2816 3 10.05V4.94999C4.14112 4.71836 5 3.70948 5 2.5C5 1.11929 3.88071 0 2.5 0Z" ]
            Svg.path [ svg.d "M8.85355 0.853554L8.14645 0.146446L5.79289 2.5L8.14645 4.85355L8.85355 4.14645L7.70711 3H9.5C10.8807 3 12 4.11929 12 5.5V7.05001C10.8589 7.28164 10 8.29052 10 9.5C10 10.8807 11.1193 12 12.5 12C13.8807 12 15 10.8807 15 9.5C15 8.29052 14.1411 7.28164 13 7.05001V5.5C13 3.567 11.433 2 9.5 2H7.70711L8.85355 0.853554Z" ]
        ]
    ]

let actionButton (onAction: unit -> unit) (disabledTitle: string) (disabled: bool) (title: string) (icon: ReactElement) =
    Html.button [
        prop.className (if disabled then "action-btn disabled" else "action-btn")
        prop.disabled disabled
        yield! noFocusProps
        prop.title (if disabled then disabledTitle else title)
        prop.onClick (fun e -> e.stopPropagation(); if not disabled then onAction ())
        prop.children [ icon ]
    ]
