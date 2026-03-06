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
                svg.d "M231,273 C229.896,273 229,272.104 229,271 C229,269.896 229.896,269 231,269 C232.104,269 233,269.896 233,271 C233,272.104 232.104,273 231,273 L231,273 Z M223,273 C221.896,273 221,272.104 221,271 C221,269.896 221.896,269 223,269 C224.104,269 225,269.896 225,271 C225,272.104 224.104,273 223,273 L223,273 Z M215,273 C213.896,273 213,272.104 213,271 C213,269.896 213.896,269 215,269 C216.104,269 217,269.896 217,271 C217,272.104 216.104,273 215,273 L215,273 Z M223,257 C214.164,257 207,263.269 207,271 C207,275.419 209.345,279.354 213,281.919 L213,289 L220.009,284.747 C220.979,284.907 221.977,285 223,285 C231.836,285 239,278.732 239,271 C239,263.269 231.836,257 223,257 L223,257 Z"
                svg.custom ("transform", "translate(-207, -257)")
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
