---
name: canvas
description: Creates and updates HTML documents displayed in the Treemon canvas pane. Use when building dashboards, status pages, interactive forms, or any visual content for an agent session.
---

# Canvas Docs

Canvas docs are HTML files in `.agents/canvas/` that Treemon auto-detects and displays in a side pane. No registration needed — write an `.html` file and it appears as a tab.

## Creating a canvas doc

Write an HTML file to `.agents/canvas/<name>.html`. Treemon scans for new files automatically. Use a descriptive filename — it becomes the tab name (e.g. `build-status.html`, `test-results.html`).

## Styling

Canvas docs are displayed in a dark-themed IDE. Use self-contained inline CSS (no external stylesheets). Recommended base styles:

```css
body {
  background: #1e1e2e;
  color: #cdd6f4;
  font-family: system-ui, -apple-system, sans-serif;
  margin: 0;
  padding: 16px;
}
a { color: #89b4fa; }
button {
  background: #45475a;
  color: #cdd6f4;
  border: 1px solid #585b70;
  border-radius: 4px;
  padding: 6px 12px;
  cursor: pointer;
}
button:hover { background: #585b70; }
```

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

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
  body {
    background: #1e1e2e;
    color: #cdd6f4;
    font-family: system-ui, -apple-system, sans-serif;
    margin: 0;
    padding: 16px;
  }
  textarea {
    width: 100%;
    min-height: 80px;
    background: #313244;
    color: #cdd6f4;
    border: 1px solid #585b70;
    border-radius: 4px;
    padding: 8px;
    resize: vertical;
    box-sizing: border-box;
  }
  button {
    margin-top: 8px;
    background: #45475a;
    color: #cdd6f4;
    border: 1px solid #585b70;
    border-radius: 4px;
    padding: 6px 16px;
    cursor: pointer;
  }
  button:hover { background: #585b70; }
</style>
</head>
<body>
  <h3>Comment</h3>
  <textarea id="msg" placeholder="Type a message..."></textarea>
  <button onclick="send()">Send</button>
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
