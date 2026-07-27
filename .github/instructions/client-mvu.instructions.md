---
description: "Elmish MVU constraints for Treemon client code"
applyTo: "src/Client/**/*.fs"
---

# Elmish MVU

- Keep `update` pure: the same `(Msg, Model)` produces the same `(Model, Cmd)`. Capture clocks, randomness, and external results in commands or subscriptions and pass them through `Msg` payloads.
- Event handlers dispatch messages for application behavior. They may also perform synchronous `preventDefault()`/`stopPropagation()` plumbing or pass rendering-only input to a bounded component-local hook.
- Express API calls, timers, browser operations, and other application effects as `Cmd` or subscriptions. A component-local hook may schedule bounded browser work that only updates local presentation state and cleans up with the component lifecycle.
- Resolve DOM nodes when an effect runs, never at the point the effect is created. React can replace a node without any model change, so a captured element goes stale silently and nothing reports it.
- A resource that binds to a node at setup — an observer, or a listener on anything other than `document` — cannot recover by re-querying inside its own callback, because that callback stops firing meaningfully once its target is replaced. Attach it to a node React never replaces and resolve the volatile targets per event, or restart it whenever its target can change.
- Key a subscription by every input its attached resource depends on, including the view structure it observes. A key that stays constant while its dependencies change keeps a dead subscription alive instead of restarting it.
- Keep application state in `Model`; component-local hook state must remain presentation-only and must not communicate with `update`.
- Use Feliz/React props and CSS classes instead of direct DOM mutation or inline style changes.
