---
description: "Elmish MVU constraints for Treemon client code"
applyTo: "src/Client/**/*.fs"
---

# Elmish MVU

- Keep `update` pure: the same `(Msg, Model)` produces the same `(Model, Cmd)`. Capture clocks, randomness, and external results in commands or subscriptions and pass them through `Msg` payloads.
- Event handlers dispatch messages only, except for synchronous `preventDefault()` and `stopPropagation()` plumbing.
- Express API calls, timers, browser operations, and other effects as `Cmd` or subscriptions. Do not start async workflows directly from views.
- Keep application state in `Model`; do not use mutable refs to share state across renders or between views and `update`.
- Use Feliz/React props and CSS classes instead of direct DOM mutation or inline style changes.
