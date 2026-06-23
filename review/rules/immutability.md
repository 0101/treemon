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
- No `let mutable` bindings in ordinary code
- Never pass collections into functions to be mutated — return new collections instead
- Prefer immutable data structures (records, DUs, lists) over mutable ones (ResizeArray, Dictionary)
- If mutable state is genuinely unavoidable (e.g. a throttle/`setInterval` timestamp confined to an Elmish subscription closure, MailboxProcessor internals, NUnit `[<SetUp>]`/`[<TearDown>]` lifecycle fields), use `let mutable` isolated to the narrowest possible scope **and add an inline comment justifying why an immutable solution doesn't fit**. A justified, scoped, commented `let mutable` is compliant — do not flag it.

## Use `let mutable`, never `ref`, when mutation is justified
A `ref` cell is **not** more immutable than `let mutable` — it is the identical mutation with worse ergonomics (a heap-allocated cell plus `:=`/`.Value` noise). Never recommend or write a `ref` cell to "satisfy" this rule, and never rewrite an existing `let mutable` into a `ref`. The only question this rule asks is *"is the mutation justified and scoped?"* — never *"which mutation syntax?"*. When the answer is yes, a `let mutable` carrying a justifying comment is the correct and final form.

## Wrong
```fsharp
// Unjustified module-level mutable in ordinary code
let mutable count = 0
let increment () = count <- count + 1

// Mutating a collection passed in as an argument
let addToList (items: ResizeArray<string>) name =
    items.Add(name)

// `ref` cell used to dodge the rule — same mutation, worse ergonomics, still wrong
let lastTime = ref (Date.now ())
let onEvent () = lastTime := Date.now ()
```

## Correct
```fsharp
let increment count = count + 1

let addToList items name =
    name :: items

// Justified, closure-scoped `let mutable` — the impure boundary is documented inline,
// so it is compliant. `let mutable`, never a `ref` cell.
let activityDetection dispatch =
    // Throttle timestamp confined to this subscription closure (Elmish's designated impure
    // boundary, same as setInterval); an immutable version would re-add listeners per event.
    let mutable lastDispatch = Date.now ()
    ...
```
