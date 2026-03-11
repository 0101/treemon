# Focused Review GitHub Action

## Goals

- Run the focused-review plugin automatically on every PR to this repo via gh-aw (GitHub Agentic Workflows)
- Post inline review comments on specific lines for each violation found
- Submit APPROVE or REQUEST_CHANGES verdict based on whether violations exist
- Use existing `review/` rules and `focused-review.json` config as-is

## Expected Behavior

1. PR opened or updated triggers the gh-aw workflow
2. Copilot CLI starts with the `0101/focused-review` plugin installed (via frontmatter `plugins:` field)
3. Workflow prompt tells Copilot to run `/focused-review branch --no-autofix`
4. focused-review orchestrates: Python script chunks the diff, spawns review-runner agents per rule-chunk pair
5. After the review completes, the prompt tells Copilot to parse the report file, call `create-pull-request-review-comment` for each VIOLATION block, then call `submit-pull-request-review` with APPROVE or REQUEST_CHANGES
6. The workflow appears as a GitHub Actions check on the PR

## Technical Approach

### gh-aw Workflow

Create `.github/workflows/focused-review.md` with frontmatter:
- **Trigger**: `pull_request: [opened, synchronize]`
- **Engine**: `copilot` (requires `COPILOT_GITHUB_TOKEN` repo secret — a GitHub PAT)
- **Plugins**: `0101/focused-review` (compiled into install step automatically)
- **Tools**: `github` (for PR review safe-outputs), `bash: [":"]` (unrestricted, needed for Python script execution and git)
- **Safe-outputs**: `create-pull-request-review-comment` (max 30), `submit-pull-request-review` (max 1)

Workflow prompt (markdown body after frontmatter):
1. Run `/focused-review branch --no-autofix` to review the PR diff
2. Read the generated report file at `.agents/focused-review/review-*.md`
3. For each VIOLATION block, call `create-pull-request-review-comment` with the file path, line number, and violation description
4. Call `submit-pull-request-review` with event REQUEST_CHANGES if any violations were found, or APPROVE if clean

Compile with `gh aw compile` to generate `.github/workflows/focused-review.lock.yml`. Commit both files.

### Plugin Integration

The focused-review plugin (published at `github.com/0101/focused-review`):
- Provides SKILL.md (orchestrator), review-runner agent, and Python helper script
- Python script needs: Python 3, git -- both available on `ubuntu-latest` runners
- Config: existing `focused-review.json` at repo root points to `review/` directory
- The `--no-autofix` flag ensures violations are reported only, not fixed (read-only CI context)

### Potential Issues

- Plugin installation in CI (repo access, auth tokens for private plugin repos)
- Python script path handling on Linux vs Windows
- Context window limits for large diffs with many rules (17 rules currently)
- Whether Copilot can reliably parse VIOLATION blocks from the report and map them to safe-output calls

## Decisions

- **Engine**: Copilot (requires `COPILOT_GITHUB_TOKEN` repo secret)
- **Approach**: gh-aw with focused-review plugin, not a from-scratch workflow
- **Both repos may change**: this repo (workflow file) and `0101/focused-review` (plugin fixes)
- **No autofix in CI**: `--no-autofix` flag prevents the agent from modifying files during review
