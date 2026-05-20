---
autofix: false
model: sonnet
applies-to: "**/*.fs"
---
# Module Cohesion

## Rule
Code should live in the module where it logically belongs, not wherever it was first needed.

## Why
When shared utilities accumulate in unrelated modules, the module name becomes misleading and the codebase harder to navigate. A function used across multiple modules belongs in a shared module named after what it does, not after the first consumer.

## Requirements
- General-purpose helpers (formatting, UI components, utilities) should live in appropriately named shared modules
- A module named after a feature (e.g. `ArchiveViews`) should only contain code specific to that feature
- If a function is imported by multiple unrelated modules, it likely belongs in a shared module
- When adding a new function, check whether the target module's name accurately describes the function's purpose
- Don't create "Utils" or "Helpers" dumping grounds either — group by concept (e.g. `Components` for shared UI, `Formatting` for display helpers)
