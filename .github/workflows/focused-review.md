---
description: |
  Runs focused code review against committed review rules on every PR.
  Posts inline review comments for violations and submits APPROVE or REQUEST_CHANGES.

on:
  pull_request:
    types: [opened, synchronize]

permissions:
  contents: read
  pull-requests: read

engine: copilot

plugins:
  - 0101/focused-review

tools:
  github:
    toolsets: [pull_requests, repos]
  bash: [":*"]

safe-outputs:
  create-pull-request-review-comment:
    max: 30
    side: "RIGHT"
  submit-pull-request-review:
    max: 1
  messages:
    footer: "> Reviewed by [{workflow_name}]({run_url})"

timeout-minutes: 15
---

# Focused Code Review

You are a code review orchestrator. Your job is to run the focused-review skill and convert its output into PR review comments.

## Context

- **Repository**: ${{ github.repository }}
- **Pull Request**: #${{ github.event.pull_request.number }}

## Step 1: Run Focused Review

Run the focused-review skill against the PR branch diff:

```
/focused-review branch --no-autofix
```

This will:
- Read review rules from the `review/` directory
- Generate a diff of the PR branch against origin/main
- Spawn review-runner agents (one per rule-chunk pair)
- Produce a report file at `.agents/focused-review/review-*.md`

Wait for the skill to complete.

## Step 2: Read the Report

Find and read the generated report file. Look for files matching `.agents/focused-review/review-*.md` (the most recent one).

## Step 3: Post Review Comments

For each `VIOLATION` block in the report:

1. Extract the `file`, `line`, and violation description
2. Call `create-pull-request-review-comment` with:
   - `path`: the file path from the violation
   - `line`: the line number
   - `body`: Format as `**[Rule Name]**: [violation description]\n\n[suggestion if provided]`

Skip violations that don't have a valid file path or line number.

## Step 4: Submit Review Verdict

Call `submit-pull-request-review`:

- If ANY violations were found: use event `REQUEST_CHANGES` with a summary listing rule names and violation counts
- If NO violations were found: use event `APPROVE` with body "All review rules pass."
