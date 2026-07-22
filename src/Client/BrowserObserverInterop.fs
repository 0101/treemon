module BrowserObserverInterop

open Browser
open Fable.Core

[<Emit("new ResizeObserver($0)")>]
let createResizeObserver (callback: obj -> unit) : obj = jsNative

[<Emit("new IntersectionObserver($0, $1)")>]
let createIntersectionObserver (callback: obj -> unit) (options: obj) : obj = jsNative

[<Emit("$0.observe($1)")>]
let observeElement (observer: obj) (element: Browser.Types.Element) : unit = jsNative

[<Emit("$0.disconnect()")>]
let disconnectObserver (observer: obj) : unit = jsNative

[<Emit("$0[0]")>]
let firstIntersectionEntry (entries: obj) : obj = jsNative
