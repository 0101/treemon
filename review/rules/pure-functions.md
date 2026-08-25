---
autofix: false
model: sonnet
applies-to: ["src/Cli/**/*.fs", "src/Client/**/*.fs", "src/Extension/**/*.fs", "src/Server/**/*.fs", "src/Shared/**/*.fs"]
---
# Pure Functions

## Rule
Use pure functions wherever possible; isolate side effects at the boundaries.

## Why
Pure functions are easier to test, reason about, and compose. Isolating side effects makes the codebase more predictable.

## Requirements
- Functions that transform data should be pure — same inputs always produce same outputs, no side effects
- Side effects (I/O, process spawning, file access) should be pushed to the edges, not mixed into business logic
- Error-path logging in exception handlers is acceptable — diagnostic logs in `with`/`catch` blocks don't violate purity in practice
- Prefer returning values over mutating state
- For data transformations, prefer `if` and `match` expressions that return values over mutable accumulators or side effects in each branch
- Use computation expressions to sequence effectful workflows; side-effecting branches are acceptable at an effect boundary where no value is being computed
- Functions called from computation expressions should remain pure where possible

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
