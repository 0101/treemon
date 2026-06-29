---
name: canvas
description: Creates and updates HTML documents displayed in the Treemon canvas pane. Use when building dashboards, status pages, interactive forms, or any visual content for an agent session.
---

# Canvas Docs

Canvas docs are HTML files in `.agents/canvas/` that Treemon auto-detects and displays in a side pane. No registration needed — write an `.html` file and it appears as a tab.

## Creating a canvas doc

Write an HTML file to `.agents/canvas/<name>.html`. Treemon scans for new files automatically. Use a descriptive filename — it becomes the tab name (e.g. `build-status.html`, `test-results.html`).

## Styling

Canvas docs render in a dark-themed IDE pane, and Treemon **already injects a typographic base into every doc** — so most docs need little or no CSS of their own. Out of the box you get:

- Dark theme, system font, a readable **16px / line-height 1.6** body, and a heading scale (`h1`…`h4`).
- A comfortable line length (~70ch) on paragraphs and list items.
- Quiet tables (header underline + row separators, no heavy gridlines), styled `code`/`pre`, links, scrollbars, and themed form controls (`button`, `textarea`, `input`, `select`).
- Design tokens as CSS variables — reuse these instead of inventing colors:
  `--bg-deep` `--bg-surface` `--bg-elevated` `--border` `--border-bright`
  `--text-primary` `--text-secondary` `--text-muted` `--accent`
  `--status-wip` `--status-blocked` `--status-closed`

Your own rules always win (the base is zero-specificity), so override anything freely — but you rarely need to redeclare `body`, headings, `code`, tables, or buttons.

### Aim for whitespace and type, not boxes

Docs read best when hierarchy comes from **size, weight, and spacing** rather than borders and filled panels. A few gentle nudges:

- Lead with headings and short paragraphs and let the margins do the separating — you don't need a bordered card around every section.
- Use `var(--text-muted)` (or a `<small>`) for secondary text instead of boxing it off.
- Reserve a visible container for genuinely set-apart content — a single callout, a code block, the input form. A semantic `<blockquote>` is pre-styled as a light, non-boxy callout.
- Keep tables to the default quiet style; resist a border around every cell.
- Use the tokens for accents (`--accent`, `--status-*`) so docs stay consistent with the rest of Treemon.

If you do set your own colors, match the dark theme — the tokens above are the palette.

### Use the visual medium

A canvas doc can do what markdown can't — so when a concept is visual, *show* it. Lean on real HTML/SVG: a small inline `<svg>` for a flow, timeline, or state diagram; a table for comparisons; nested lists for hierarchy. A wall of paragraphs that would read the same as a `.md` file is a missed opportunity — a diagram or a labelled flow usually explains a pipeline, schedule, or decision far faster than prose.

## Interactivity

Canvas docs send messages back to the agent session with the injected **`canvasSend(action, payload)`** helper. Treemon validates the origin and forwards the message to the session that owns the doc.

```js
canvasSend('my-action', { payload: 'data' });
```

`canvasSend` is the primary API. It builds the flat message shape, posts it to the pane, and — before sending — checks the serialized size against the pane's limit (`JSON.stringify(message).length`, i.e. **64000 UTF-16 code units**). An oversized message is **not** sent; instead it logs a `console.error` in the doc so you get immediate feedback instead of a silent drop. `canvasSend` returns `true` when the message was posted and `false` when it was too large.

The message shape is flat: `canvasSend('navigate-canvas-doc', { filename })` posts `{ action: 'navigate-canvas-doc', filename }` (which switches the active tab); `canvasSend('comment', { text })` posts `{ action: 'comment', text }`. That raw `postMessage` shape is the underlying contract and still works directly if you ever need it (e.g. the helper isn't available) — but it sends without the size check:

```js
window.parent.postMessage({ action: 'my-action', payload: 'data' }, '*');
```

### Don't block the conversation when the doc collects the answer

If the canvas doc itself gathers the user's input — choices, a form, buttons, a comment box — **do not** also call `ask_user` (or any other blocking prompt). The doc's `canvasSend` reply *is* the channel for the answer. Calling `ask_user` at the same time pops a separate blocking modal, freezes the session, and prevents the user from responding through the doc you just built.

Instead: write the doc, briefly tell the user it's ready for their input, then **end your turn and leave the conversation open**. The user's selection arrives as a normal message via `canvasSend`, and you continue from there. Only use `ask_user` when there is no canvas doc collecting the response.

## Ownership

When you create or update a canvas doc, your session is automatically recorded as that doc's **owner**. That ownership is what routes the user's message replies back to *your* session — even when several agent sessions are running in the same worktree.

You never need to know or send your own session ID: writing the `.html` file with the **create** or **edit** tool *is* the ownership declaration — the extension stamps in the session ID and reports it to Treemon for you. So always author canvas docs with those tools under `.agents/canvas/`. Don't shell out to write the file (e.g. redirecting command output into it); the declaration only fires for the create/edit tools, and without it the doc's messages may reach the wrong session.

Editing a doc another session created transfers ownership to you (most recent author wins), so from then on its messages arrive in your session.

## Updating

Overwrite the file — Treemon detects content changes (via hash) and reloads the pane automatically. If Treemon isn't monitoring the directory, the extension serves canvas files over HTTP and returns the browser URL in the tool result right after you create or edit a canvas file (open it for the user or share the ctrl+clickable URL). `canvasSend` interactions work identically in both modes — no changes needed in your HTML.

## Multiple docs

Each `.html` file in `.agents/canvas/` becomes a separate tab. Use distinct filenames for different views.

## Archive

Users can archive docs to `.agents/canvas/archive/`. Don't rely on canvas docs for persistent state — store important data in regular files.

## Minimal template

The base theme already styles `body`, headings, and the form controls, so an input-collecting doc can be tiny — no `<style>` block needed:

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
</head>
<body>
  <h3>Comment</h3>
  <p><small>Share a quick note — it comes straight back to the agent.</small></p>
  <textarea id="msg" placeholder="Type a message..."></textarea>
  <p><button onclick="send()">Send</button></p>
  <script>
    function send() {
      const text = document.getElementById('msg').value.trim();
      if (text) {
        canvasSend('comment', { text });
        document.getElementById('msg').value = '';
      }
    }
  </script>
</body>
</html>
```

Reach for extra CSS only when a doc genuinely needs it (a custom layout, an accent) — and prefer the injected tokens and whitespace over new boxes.
