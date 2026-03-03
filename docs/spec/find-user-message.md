# Find User Message: Reverse-Scan for Large Sessions

## Problem

`getLastUserMessage` calls `readAllLinesNewestFirst`, which reads only the last 64KB of the session JSONL file. In large sessions the last user message is often buried beyond 64KB from the end (assistant responses with tool use are verbose), so the function returns `None` and worktree cards show no user message.

Additionally, skill prompt injections (slash-command instructions injected as `type:"user"` entries) can appear as the "last user message" when the real user message is further back. The existing `<command-name>` extraction in `extractUserContent` handles explicit slash commands, but some skill prompts arrive as long markdown-formatted text without the `<command-name>` tag wrapper.

## Goals

- Display the last user message on every worktree card, even when Claude sessions grow beyond 64KB
- Filter out skill prompt injections (long markdown-formatted `type:"user"` entries) so only genuine user input appears
- Maintain current performance for small sessions (< 64KB)

## Expected Behavior

1. **Normal sessions (< 64KB):** Behavior unchanged -- single 64KB buffer read finds the user message
2. **Large sessions (> 64KB):** Reverse-scan reads 64KB chunks from end of file backward until a user message is found, capped at 1MB total (16 chunks)
3. **Skill prompt filtering:** `isSystemNoise` rejects text entries starting with `# ` or `**` that exceed 200 characters -- these are system-injected skill instructions, not real user input. (Existing `<command-name>` extraction in `extractUserContent` is not changed.)
4. **No user message found within 1MB:** Returns `None` -- no crash, no placeholder text

## Technical Approach

### Reverse-scan function (`scanForUserMessage`)

Replace the `readAllLinesNewestFirst` call in `getLastUserMessage` with a dedicated reverse-scanner:

1. Open the file with `FileShare.ReadWrite` (Claude may be writing)
2. Read the last 64KB chunk
3. Split into lines, skip first partial line if mid-file, reverse to newest-first
4. Scan each line through `tryParseUserText` -- return immediately on match
5. If no match: read the previous 64KB chunk (with ~1KB overlap for line boundary safety)
6. Repeat until match found or 1MB total read (16 chunks max)
7. Return `None` if exhausted

### Skill prompt detection in `isSystemNoise`

Add two patterns to `isSystemNoise`:
- Text starting with `# ` where length > 200 (markdown heading skill prompts)
- Text starting with `**` where length > 200 (bold-prefix skill prompts)

These thresholds are conservative -- real user messages rarely start with markdown formatting and exceed 200 characters.

### Scope

- Only `getLastUserMessage` changes scan behavior; `getStatus` and `getLastMessage` keep using the existing 64KB `readLastLines`
- No API surface changes -- same `string option` return type
- Read-only operation, no side effects

## Key Files

- `src/Server/ClaudeDetector.fs` -- all changes here
- `src/Tests/` -- new unit tests for reverse-scan and noise filtering
