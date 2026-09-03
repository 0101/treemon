# Test Infrastructure

## Goals

- Keep tests isolated from production and from concurrent test runs.
- Ensure test cleanup affects only processes started by the fixture.
- Keep the documented Fast-suite budget accurate and useful as a development gate.
- Reuse expensive repository fixtures without sharing mutable test state.

## Expected Behavior

Every server, Canvas, Vite, and demo fixture uses OS-assigned loopback ports. Tests never terminate a
process merely because it owns a port; they stop only exact process IDs they started and fail when
owned-process cleanup is incomplete.

`DemoModeTests` follows the existing `ServerFixture` pattern: allocate distinct ports through
`TestUtils.getFreeTcpPorts`, pass them through the existing environment/CLI inputs, and remove
`killOrphansOnPort` once it has no callers.

The documented `Category=Fast` budget and any automated threshold agree. A representative run is
measured before changing either. Expensive fixtures, especially the real repository used by
`WorktreeDiffTests`, may share an immutable one-time baseline while each test receives an isolated
branch, clone, or working directory.

## Technical Approach

Extend the existing test helpers rather than adding a second port allocator or process supervisor.
Fixture setup records the exact spawned process and awaits observable readiness; teardown stops that
process by PID and verifies exit.

Measure suite and fixture durations through the existing test runner. Optimize the dominant setup
paths first, preserving deterministic per-test filesystem and Git state. Update `AGENTS.md` only
when the measured target changes.

## Decisions

- **Dynamic test ports, stable production ports:** production remains on 5000/5002; tests never
  reserve those values.
- **Exact ownership over port ownership:** a listening port is not proof that a process belongs to
  the current test.
- **Measure before changing the budget:** the threshold is evidence, not an aspirational number.
- **Shared immutable baseline only:** mutable repository state remains isolated per test.

## Related Specs

- `docs/spec/worktree-diff-viewer.md` - real-Git comparison fixtures.
- `docs/spec/canvas-pane.md` - Canvas origin and test-port injection.
