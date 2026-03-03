---
autofix: false
model: haiku
applies-to: "*.fs"
source: ".claude/CLAUDE.md"
---
# Cross-Platform APIs

## Rule
Use platform-agnostic .NET APIs for file paths and line endings.

## Why
Hardcoded path separators and newline characters break on other operating systems. The .NET standard library provides cross-platform alternatives.

## Requirements
- Use `Path.Combine(a, b)` instead of string concatenation with `/` or `\` for file system paths
- Use `Environment.NewLine` instead of `"\n"` or `"\r\n"` when constructing multi-line output
- Exception: `\n` is acceptable when splitting input known to use `\n` (e.g. parsing git output)
- Exception: String interpolation with `/` for URL construction is fine — only flag file system paths

## Wrong
```fsharp
let configPath = baseDir + "\\" + "config.json"
let logPath = $"{rootDir}/logs/server.log"
let header = title + "\n" + underline
```

## Correct
```fsharp
let configPath = Path.Combine(baseDir, "config.json")
let logPath = Path.Combine(rootDir, "logs", "server.log")
let header = title + Environment.NewLine + underline
```
