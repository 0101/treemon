module Server.CanvasSendScript

let source = EmbeddedResource.readText "CanvasSend.js"

let script = "<script>" + source + "</script>"
