module Server.CanvasSelectionScript

let source = EmbeddedResource.readText "CanvasSelectionContext.js"

let script = "<script>" + source + "</script>"
