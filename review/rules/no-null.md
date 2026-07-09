---
autofix: false
model: haiku
applies-to: "**/*.fs"
---
# No Null

## Rule
Use Option types instead of null.

## Why
Eliminates null reference exceptions and makes absence of values explicit in the type system.

## Requirements
- No `null` literals in F# code (except when required for .NET interop boundaries)
- Use `Option<'T>` to represent optional values
- Use `Option.map`, `Option.bind`, `Option.defaultValue` for option transformations
- When interfacing with .NET APIs that return null, convert to Option immediately at the boundary

## Exceptions
- **.NET interop boundaries** — `null` is permitted where a .NET API requires or returns it, converted to `Option` immediately.
- **Test files** (`*Tests.fs`, `**/Tests/**`) — `null` literals are permitted when they are the *subject* of the test, not a modelling choice:
  - A negative test that deliberately passes `null` to verify a boundary function's null-safety/defensive behavior (there is no null-free way to exercise that branch). Do not demand the production signature change to `string option` to satisfy the test.
  - Setup-seeded fixture fields (`let mutable x = null` populated in `[<SetUp>]`/`[<OneTimeSetUp>]`) where an `Option` field would only add a `.Value` unwrap at every read without removing a reachable crash. Prefer a canonical accessor (e.g. `requireProbe ()`) over raw `Option` when clarity matters, but a null-seeded fixture field is acceptable.
  Production code (non-test files) stays strictly null-free.

## Wrong
```fsharp
let findUser id =
    let user = db.Find(id)
    if user = null then failwith "not found"
    else user

let name: string = null
```

## Correct
```fsharp
let findUser id =
    db.Find(id)
    |> Option.ofObj

let name: string option = None
```
