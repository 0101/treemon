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

[<Emit("new DOMMatrix(getComputedStyle($0).transform).m42")>]
let translatedY (element: Browser.Types.Element) : float = jsNative

[<Emit("parseFloat(getComputedStyle($0).getPropertyValue($1))")>]
let cssPixelValue (element: Browser.Types.Element) (propertyName: string) : float = jsNative

[<Emit("window.matchMedia($0)")>]
let matchMedia (query: string) : obj = jsNative

[<Emit("window.innerWidth")>]
let viewportWidth () : float = jsNative

[<Emit("$0.addEventListener('change', $1)")>]
let addMediaChangeListener (media: obj) (listener: obj -> unit) : unit = jsNative

[<Emit("$0.removeEventListener('change', $1)")>]
let removeMediaChangeListener (media: obj) (listener: obj -> unit) : unit = jsNative
