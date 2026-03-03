---
autofix: false
model: sonnet
applies-to: "*.fs"
source: "global CLAUDE.md"
---
# Single Responsibility

## Rule
Each function and module should do one thing well.

## Why
Focused functions are easier to test, reuse, and understand. Large functions that do many things become hard to maintain.

## Requirements
- Functions should have a single, clear purpose reflected in their name
- If a function does parsing AND processing AND formatting, split it into separate functions
- Compose small, focused functions rather than writing large monolithic ones
- Modules should group related functions around a single concept

## Wrong
```fsharp
let processAndSaveUser input =
    let parsed = JsonSerializer.Deserialize<User>(input)
    let validated =
        if String.IsNullOrEmpty(parsed.Name) then failwith "invalid"
        else parsed
    let formatted = sprintf "%s <%s>" validated.Name validated.Email
    File.WriteAllText("users.txt", formatted)
    formatted
```

## Correct
```fsharp
let parseUser input =
    JsonSerializer.Deserialize<User>(input)

let validateUser user =
    if String.IsNullOrEmpty(user.Name) then Error "invalid"
    else Ok user

let formatUser user =
    sprintf "%s <%s>" user.Name user.Email
```
