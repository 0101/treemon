---
autofix: false
model: haiku
applies-to: "**/*.fs"
---
# Immutability

## Rule
All bindings must be immutable; never mutate collections passed as arguments.

## Why
Mutable state is the root of most bugs. Immutable data makes data flow explicit, eliminates side effects, and makes code easier to reason about.

## Requirements
- No `let mutable` bindings
- Never pass collections into functions to be mutated — return new collections instead
- Prefer immutable data structures (records, DUs, lists) over mutable ones (ResizeArray, Dictionary)
- If mutable state is absolutely required (e.g. MailboxProcessor internals), it must be explicitly isolated and documented

## Wrong
```fsharp
let mutable count = 0
let increment () = count <- count + 1

let addToList (items: ResizeArray<string>) name =
    items.Add(name)
```

## Correct
```fsharp
let increment count = count + 1

let addToList items name =
    name :: items
```
