module CanvasTab

open Shared
open Feliz

type State =
    { IsActive: bool
      IsViewed: bool
      IsTerminalLinked: bool }

let className classes (state: State) =
    [ yield! classes
      if state.IsActive then "active"
      if state.IsViewed && not state.IsActive then "canvas-tab-viewed"
      if state.IsTerminalLinked then "canvas-terminal-linked" ]
    |> String.concat " "

let attributes classes state (selectDoc: string -> unit) (doc: CanvasDoc) =
    [ prop.className (className ("canvas-tab" :: classes) state)
      prop.type'.button
      prop.custom ("data-filename", doc.Filename)
      prop.onClick (fun event ->
          event.stopPropagation()
          selectDoc doc.Filename)
      prop.onKeyDown (fun event ->
          if event.key = "Enter" || event.key = " " then event.stopPropagation()) ]

let labelText (doc: CanvasDoc) =
    doc.Filename.Substring(0, doc.Filename.Length - 5)

let label classes doc =
    Html.span [
        prop.className (String.concat " " ("canvas-tab-title" :: classes))
        prop.text (labelText doc)
    ]

let age classes now modified =
    Html.span [
        prop.className (String.concat " " ("canvas-tab-age" :: classes))
        prop.text (Components.relativeTimeCompact now modified)
    ]
