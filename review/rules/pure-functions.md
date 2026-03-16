---
autofix: false
model: sonnet
applies-to: "*.fs"
---
# Pure Functions

## Rule
Use pure functions wherever possible; isolate side effects at the boundaries.

## Why
Pure functions are easier to test, reason about, and compose. Isolating side effects makes the codebase more predictable.

## Requirements
- Functions that transform data should be pure — same inputs always produce same outputs, no side effects
- Side effects (I/O, logging, process spawning, file access) should be pushed to the edges, not mixed into business logic
- Prefer returning values over mutating state
- Computation expressions like `async` should contain side effects; the functions they call should be pure where possible

## Wrong
```fsharp
let processItems items =
    printfn "Processing %d items" (List.length items)
    let result = items |> List.filter isValid |> List.map transform
    File.WriteAllText("output.txt", serialize result)
    result
```

## Correct
```fsharp
let processItems items =
    items |> List.filter isValid |> List.map transform

// Side effects at the boundary
let run () = async {
    let items = readInput()
    let result = processItems items
    do! writeOutput result
}
```
