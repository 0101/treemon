# Runtime State Storage

## Goals

- Evaluate whether Treemon should have one durable runtime-state mechanism instead of SQLite plus
  several per-port JSON stores.
- Reduce duplicated load, validation, serialization, and failure-handling code only when the
  resulting system is simpler.
- Avoid a partial migration that leaves one store as the new exception.

## Expected Behavior

If consolidation proceeds, `MergedPrStore`, `AutoSyncStore`, and `CanvasDocOwnership` move together
to SQLite. Their current identities, port isolation, atomic update semantics, corruption behavior,
and startup defaults remain unchanged from the caller's perspective.

Startup imports each valid legacy JSON store idempotently inside a bounded migration, then uses
SQLite as the sole writer. A legacy file is removed only after its complete content is durably
committed. Invalid input remains recoverable and produces the existing bounded diagnostic behavior.

If evidence does not show meaningful maintenance or reliability benefit, the current split remains
the chosen design and no migration is added.

## Technical Approach

First compare the duplicated code and observed failure modes against the cost of schema, migration,
and retention changes. Treat the three point-read/point-write stores as one decision. Reuse
`SqliteStorage` timestamp and reader helpers, but keep concept-specific tables and modules.

Any implementation adds current-schema tables, a transactionally idempotent import keyed by the
existing store identities, and focused compatibility tests. It does not rebuild the stores as one
generic key-value table or expose SQL outside their owning modules.

## Decisions

- **All three stores or none:** migrating one increases inconsistency rather than reducing it.
- **Evidence before migration:** `JsonStore` already writes atomically, so durability alone is not
  justification.
- **Concept-specific tables:** one database mechanism does not require one generic data model.
- **Bounded one-way import:** compatibility exists only to prevent startup failure or data loss.

## Related Specs

- `docs/spec/worktree-monitor.md` - AutoSync and merged-PR runtime state.
- `docs/spec/canvas-interaction-routing.md` - persistent AgentDoc ownership.
- `docs/spec/session-status-push.md` - existing SQLite runtime-state patterns.
