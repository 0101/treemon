---
description: "Elmish MVU constraints for Treemon client code"
applyTo: "src/Client/**/*.fs"
---

# Elmish MVU

- Keep `update` pure: the same `(Msg, Model)` produces the same `(Model, Cmd)`. Capture clocks, randomness, and external results in commands or subscriptions and pass them through `Msg` payloads.
- Event handlers dispatch messages for application behavior. They may also perform synchronous `preventDefault()`/`stopPropagation()` plumbing or pass rendering-only input to a bounded component-local hook.
- Express API calls, timers, browser operations, and other application effects as `Cmd` or subscriptions. A component-local hook may schedule bounded browser work that only updates local presentation state and cleans up with the component lifecycle.
- Resolve DOM handles when an effect or subscription callback runs, not when it is attached. React can replace a node without any model change, so a captured element, observer root, or listener target detaches silently and never recovers.
- Key a subscription by every input its attached resource depends on, including the view structure it observes. A key that stays constant while its dependencies change keeps a dead subscription alive instead of restarting it.
- Keep application state in `Model`; component-local hook state must remain presentation-only and must not communicate with `update`.
- Use Feliz/React props and CSS classes instead of direct DOM mutation or inline style changes.
