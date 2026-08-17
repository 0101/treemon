---
autofix: false
model: haiku
applies-to: ["src/Cli/**/*.fs", "src/Client/**/*.fs", "src/Extension/**/*.fs", "src/Server/**/*.fs", "src/Shared/**/*.fs"]
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
