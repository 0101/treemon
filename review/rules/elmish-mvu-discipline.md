---
autofix: false
model: sonnet
applies-to: "src/Client/**/*.fs"
---
# Elmish MVU Discipline

## Rule
All state changes and side effects must flow through the Elmish MVU cycle (Msg → update → Model + Cmd).

## Why
The MVU (Model-View-Update) pattern guarantees unidirectional data flow and makes state transitions explicit and traceable. Bypassing the message loop — by mutating state directly, performing side effects in view functions, or using mutable refs to share state — breaks this guarantee and leads to bugs that are invisible to the Elmish debugger.

## Requirements
- All user interactions in view functions must dispatch a `Msg` — never call APIs, mutate state, or perform side effects inline
- Side effects (API calls, DOM manipulation, timers) must be expressed as `Cmd` values returned from `update`, never executed directly
- The `update` function must be pure: same `(Msg, Model)` must produce the same `(Model, Cmd)`. Non-deterministic values (clocks, random) must be captured at the impure boundary (subscriptions, Cmd callbacks) and passed via `Msg` payloads
- No mutable refs (`ref`, `Ref`, `IRefValue`) used to share state between view renders or between view and update
- No direct DOM manipulation (e.g., `document.getElementById`, `element.style`, `element.classList`) in view functions — use React props/attributes instead
- No `async { ... } |> Async.StartImmediate` or `promise { ... }` fire-and-forget in view functions — use `Cmd.OfAsync` or `Cmd.OfPromise` in update
- Event handlers in view must only dispatch messages: `prop.onClick (fun _ -> dispatch SomeMsg)` — not `prop.onClick (fun _ -> doSomeSideEffect(); dispatch SomeMsg)`
- Exception: `e.stopPropagation()` and `e.preventDefault()` are acceptable in event handlers — these are view-layer DOM plumbing, not business-logic side effects, and must execute synchronously during the browser event (they cannot be deferred to the update cycle)
- Subscriptions (`Sub`, `useEffect`, timers) must dispatch messages to feed results back into the MVU loop
- The `update` function is the single source of truth for state transitions — no model fields should be set outside of it
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
```
