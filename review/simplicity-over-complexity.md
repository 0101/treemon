---
autofix: false
model: inherit
source: "global CLAUDE.md"
---
# Simplicity Over Complexity

## Rule
Choose the simplest solution that works; minimize moving parts.

## Why
Fewer components, dependencies, and interactions mean fewer failure points, less debugging, and lower maintenance burden.

## Requirements
- Prefer straightforward implementations over clever or over-engineered ones
- Don't add abstractions, helpers, or utilities for one-time operations
- Don't design for hypothetical future requirements
- If a solution requires significant boilerplate, reconsider the approach
- Three similar lines of code is better than a premature abstraction
- Don't add error handling or validation for scenarios that can't happen
- Don't use feature flags or backwards-compatibility shims — just change the code

## Wrong
```fsharp
// Abstract factory for a one-time operation
type IFormatterFactory =
    abstract CreateFormatter: string -> IFormatter

type FormatterFactory() =
    interface IFormatterFactory with
        member _.CreateFormatter(format) =
            match format with
            | "json" -> JsonFormatter() :> IFormatter
            | _ -> TextFormatter() :> IFormatter

let format (factory: IFormatterFactory) data =
    let formatter = factory.CreateFormatter("json")
    formatter.Format(data)
```

## Correct
```fsharp
let formatAsJson data =
    JsonSerializer.Serialize(data)
```
