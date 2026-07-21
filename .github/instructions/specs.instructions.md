---
description: "Long-lived Treemon specification hygiene"
applyTo: "docs/spec/**/*.md"
---

# Specification Hygiene

- Describe current behavior, durable architecture, and non-obvious decisions. Do not record branch history, implementation diaries, or completed task narratives.
- Fold minor features into the authoritative parent spec. Avoid standalone files for fixes, cleanup, migrations, updates, buttons, tooltips, or similarly narrow changes.
- When behavior changes, update the relevant existing spec in the same change and remove stale counts, names, wire kinds, and architecture descriptions.
- Before adding a spec, search `docs/spec/` for the existing document that owns the subsystem.
