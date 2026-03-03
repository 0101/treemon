---
autofix: false
model: haiku
applies-to: "*.fs"
source: ".claude/CLAUDE.md"
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
