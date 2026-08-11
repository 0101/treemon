---
autofix: false
model: inherit
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
- Don't add feature flags or compatibility shims for hypothetical or unsupported legacy states
- For durable stores that survive application upgrades, allow the smallest bounded, idempotent migration when it prevents startup failure or data loss and has focused tests
- If preserving old durable state is not required, make the versioning or recreation decision explicit and remove the compatibility path

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

## Does not apply
- **Durable schema evolution** — a small startup migration for a persisted store is not unnecessary compatibility machinery when existing installations can contain the old schema, new queries require the new schema, and recreating the store would discard meaningful user data. Keep the migration narrow, idempotent, and tested.
