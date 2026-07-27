module Server.CanvasMorphScript

let source = EmbeddedResource.readText "CanvasMorph.js"

let script = "<script>" + source + "</script>"

/// Marks the blocks a morph just changed. Yellow is Treemon's existing "this changed" colour —
/// worktree-card canvas notifications and the diff viewer's changed rows already use it — so the
/// card badge and the in-doc tint say the same thing.
///
/// The entry flash is derived from the resting tint rather than fixed: against a .22 rest a
/// hardcoded .3 flash is a 1.28:1 step and reads as no animation at all, while doubling holds a
/// consistent pop at any tint.
///
/// The box-shadow spread gives the tint breathing room around the text without padding or margin,
/// so highlighting never reflows the doc.
let style =
    "<style>.canvas-updated{--canvas-updated-rgb:249,226,175;--canvas-updated-alpha:.22;"
    + "background:rgba(var(--canvas-updated-rgb),var(--canvas-updated-alpha));"
    + "box-shadow:0 0 0 .35rem rgba(var(--canvas-updated-rgb),var(--canvas-updated-alpha));"
    + "border-radius:3px}"
    + "@media(prefers-reduced-motion:no-preference){"
    + ".canvas-updated{animation:canvas-updated-in .45s ease-out}"
    + "@keyframes canvas-updated-in{from{"
    + "background:rgba(var(--canvas-updated-rgb),calc(var(--canvas-updated-alpha)*2));"
    + "box-shadow:0 0 0 .35rem rgba(var(--canvas-updated-rgb),calc(var(--canvas-updated-alpha)*2))}}}"
    + "</style>"
