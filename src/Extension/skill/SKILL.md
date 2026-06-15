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

Canvas docs can send messages back to the agent session via `postMessage`. Treemon validates the origin and forwards the message.

```js
window.parent.postMessage({ action: 'my-action', payload: 'data' }, '*');
```

## Updating

Overwrite the file — Treemon detects content changes (via hash) and reloads the pane automatically. If Treemon isn't monitoring the directory, the extension serves canvas files over HTTP and returns the browser URL in the tool result right after you create or edit a canvas file (open it for the user or share the ctrl+clickable URL). `postMessage` interactions work identically in both modes — no changes needed in your HTML.

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
        window.parent.postMessage({ action: 'comment', text }, '*');
        document.getElementById('msg').value = '';
      }
    }
  </script>
</body>
</html>
```
