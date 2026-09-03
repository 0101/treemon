# Canvas Templates

## Goals

- Give agents a fast, consistent starting point for common canvas documents.
- Demonstrate the shipped canvas theme, interaction helpers, and visual conventions without copying
  boilerplate into every prompt.
- Keep generated documents ordinary editable AgentDocs after scaffolding.

## Expected Behavior

A small bundled set covers recurring document shapes such as planning, review, status, and decision
comparison. Each template is self-contained, dark-theme compatible, responsive in the canvas pane,
and usable without external scripts, fonts, or stylesheets.

Scaffolding copies a chosen template to a contract-valid `.agents/canvas/<name>.html` file. The copy
becomes a normal AgentDoc: the author may edit any markup, and existing ownership, morphing,
`canvasSend`, `canvasExpand`, selected-text actions, and browser fallback behavior apply unchanged.

Templates should be useful without customization, but contain clear structural placeholders rather
than product-specific sample data.

## Technical Approach

Template HTML lives in one bundled resource set shared by the scaffolding command and rendering
tests. Templates rely on the injected base theme and design tokens instead of duplicating the
canvas reset. Interactive examples call the injected helpers rather than implementing their own
message transport.

The scaffold boundary validates the destination with the existing canvas filename contract and
refuses to overwrite an existing document unless the caller explicitly requests replacement.

## Decisions

- **Copy, do not render dynamically:** the scaffolded file remains transparent and fully editable.
- **Build on the base theme:** templates demonstrate layout and interaction patterns, not another
  styling framework.
- **Small curated set:** recurring document structures are valuable; a general template ecosystem
  or package manager is not required.
- **Existing canvas contract:** templates add no document kind, runtime API, or routing path.

## Related Specs

- `docs/spec/canvas-pane.md` - AgentDoc runtime, styling, ownership, and interactions.
- `docs/spec/canvas-browser-fallback.md` - standalone browser behavior for scaffolded documents.
