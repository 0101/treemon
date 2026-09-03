# Specification Integrity

## Goals

- Detect broken specification links and stale implementation paths automatically.
- Keep every retained spec structurally consistent with the repository's specification rules.
- Add the check without introducing a separate documentation toolchain.

## Expected Behavior

A repository test scans every Markdown file under `docs/spec`, including `future`. It verifies that
each spec contains Goals, Expected Behavior, and Technical Approach sections and that every literal
`docs/spec/*.md` cross-reference resolves.

Literal repository paths in Key Files tables are checked when they name a concrete file or
directory. Wildcards, illustrative placeholders, URLs, and generated runtime paths are excluded.
Failures identify the source spec, broken value, and expected repository-relative target.

The check runs with the existing fast structural tests so a module move cannot merge while its
authoritative spec still names the old location.

## Technical Approach

Extend `WorkspaceLayoutTests` or another existing repository-structure fixture. Parse only the small
Markdown conventions the specs own: level-two headings, `docs/spec/*.md` references, and backticked
paths in Key Files tables. Use `Path.Combine` from the repository root and return all failures in one
assertion.

The test validates references, not prose semantics. Behavioral drift still requires review and
source verification.

## Decisions

- **Existing test suite over a new linter:** the repository already has structural tests and needs
  no documentation dependency.
- **Literal paths only:** guessing whether arbitrary prose denotes a file would create noisy false
  positives.
- **Report all failures:** one run should expose the complete repair set after a refactor.

## Related Specs

- `docs/spec/worktree-monitor.md` - repository architecture and Key Files conventions.
