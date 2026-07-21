---
autofix: false
model: sonnet
applies-to: "src/Client/**/*.fs"
---
# Elmish MVU Discipline

## Rule
Application/model state changes and application side effects must flow through the Elmish MVU cycle
(`Msg → update → Model + Cmd`). A component-local React hook may own ephemeral rendering-only state
and bounded animation-frame scheduling when it cannot affect application behavior and cleans up with
the component lifecycle.

## Why
The MVU (Model-View-Update) pattern guarantees unidirectional application data flow and makes domain
state transitions explicit and traceable. Bypassing the message loop for business state or application
effects creates side channels that are invisible to the Elmish debugger. React still needs narrowly
scoped local mechanics for rendering interactions such as hover feedback, memoization, DOM handles, and
`requestAnimationFrame` coalescing; forcing those mechanics into the global model adds messages and
re-renders without improving application-state traceability.

## Requirements
- User interactions that change application/model state or trigger application behavior must dispatch a
  `Msg` — never call APIs, mutate shared state, or perform application side effects inline
- Application side effects (API calls, storage, navigation, process control, or timers that drive
  application behavior) must be expressed as `Cmd` values returned from `update`
- The `update` function must be pure: same `(Msg, Model)` must produce the same `(Model, Cmd)`. Non-deterministic values (clocks, random) must be captured at the impure boundary (subscriptions, Cmd callbacks) and passed via `Msg` payloads
- No mutable refs (`ref`, `Ref`, `IRefValue`) may carry model, domain, fetched, or other application
  state between renders, communicate with `update`, or act as an application-state side channel
- Component-local React hooks may use `useState`, `useMemo`, `useRef`, and `useEffect` for ephemeral
  rendering-only interaction state and bookkeeping. Allowed refs include DOM handles, memoized geometry,
  the latest pointer/hover candidate, and an animation-frame token or queue, provided they remain private
  to the component and cannot change business behavior
- A rendering-only hook may own `requestAnimationFrame` scheduling when its callback only commits local
  React rendering state, repeated requests are bounded/coalesced, and the hook cancels pending work on
  dependency change or unmount. An event handler may call the callback exposed by that hook
- No direct DOM manipulation (e.g., `document.getElementById`, `element.style`, `element.classList`) in view functions — use React props/attributes instead
- No `async { ... } |> Async.StartImmediate` or `promise { ... }` fire-and-forget in view functions — use `Cmd.OfAsync` or `Cmd.OfPromise` in update
- Event handlers must dispatch messages for application behavior. They may additionally perform the
  synchronous DOM plumbing below or pass rendering-only input to a bounded component-local hook
- Exception: `e.stopPropagation()` and `e.preventDefault()` are acceptable in event handlers — these are view-layer DOM plumbing, not business-logic side effects, and must execute synchronously during the browser event (they cannot be deferred to the update cycle)
- Subscriptions and effects that produce application data must dispatch messages to feed results back into
  the MVU loop. A component-local effect may instead perform cleanup for allowed rendering-only resources
- The `update` function is the single source of truth for application/model transitions. Local React
  state must remain presentation-only and must not duplicate model state
- `Cmd.ofEffect` is acceptable for fire-and-forget effects (e.g., `window.location.reload()`) but should not be used to update model state

## Wrong
```fsharp
// Side effect in view function
let view model dispatch =
    Html.button [
        prop.onClick (fun _ ->
            // BAD: API call directly in event handler
            async {
                let! result = api.doSomething()
                dispatch (GotResult result)
            } |> Async.StartImmediate)
        prop.text "Click"
    ]

// Mutable ref to share state outside MVU
let mutable lastFetchTime = System.DateTime.MinValue

let update msg model =
    match msg with
    | Fetch ->
        lastFetchTime <- System.DateTime.Now  // BAD: side-channel state
        model, Cmd.OfAsync.perform api.fetch () Fetched

// React ref carries application data outside the model
let view model dispatch =
    let latestResponse = React.useRef model.Response
    latestResponse.current <- model.Response  // BAD: duplicates model state across renders

// Direct DOM manipulation in view
let view model dispatch =
    Html.div [
        prop.ref (fun el ->
            if el <> null then
                el?style?color <- "red")  // BAD: imperative DOM mutation
    ]
```

## Correct
```fsharp
// All effects via Cmd in update
let update msg model =
    match msg with
    | DoSomething ->
        model, Cmd.OfAsync.either api.doSomething () GotResult GotError
    | GotResult result ->
        { model with Result = Some result }, Cmd.none

// View only dispatches messages
let view model dispatch =
    Html.button [
        prop.onClick (fun _ -> dispatch DoSomething)
        prop.text "Click"
    ]

// State is part of the model
let update msg model =
    match msg with
    | Fetch ->
        { model with LastFetchTime = Some System.DateTime.Now },
        Cmd.OfAsync.perform api.fetch () Fetched

// Styling via React props
let view model dispatch =
    Html.div [
        prop.style [ style.color.red ]
    ]

// A dedicated local hook may coalesce rendering-only hover feedback
let useHover () =
    let hover, setHover = React.useState None
    let scheduledFrame = React.useRef None

    let queueHover sample =
        if scheduledFrame.current.IsNone then
            let flush _ =
                scheduledFrame.current <- None
                setHover (Some sample)

            scheduledFrame.current <-
                Some (Browser.Dom.window.requestAnimationFrame flush)

    React.useEffect(
        (fun () ->
            React.createDisposable (fun () ->
                scheduledFrame.current
                |> Option.iter Browser.Dom.window.cancelAnimationFrame)),
        [||]
    )

    hover, queueHover
```
