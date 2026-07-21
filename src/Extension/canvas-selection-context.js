(function () {
  if (window.__canvasSelectionContextInstalled) return;
  window.__canvasSelectionContextInstalled = true;

  const editableSelector = 'input,textarea,select,[contenteditable]:not([contenteditable="false"])';
  const sectionPattern = /^[A-Za-z0-9_-]+$/;
  const contextLength = 160;
  let state = null;
  let host = null;
  let shadow = null;
  let box = null;
  let commentForm = null;
  let commentInput = null;
  let errorText = null;
  let processingHost = null;
  let processingShadow = null;
  let processingRange = null;
  let selectionFrame = 0;
  let positionFrame = 0;
  let processingFrame = 0;

  function elementFor(node) {
    if (!node) return null;
    return node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
  }

  function isEditableSelection(selection) {
    const anchor = elementFor(selection.anchorNode);
    const focus = elementFor(selection.focusNode);
    return Boolean(
      (anchor && anchor.closest(editableSelector)) ||
      (focus && focus.closest(editableSelector))
    );
  }

  function ensureHost() {
    if (!host) {
      host = document.createElement('canvas-selection-context');
      host.style.cssText =
        'position:fixed;left:0;top:0;z-index:2147483647;display:none;visibility:hidden;';
      shadow = host.attachShadow({ mode: 'open' });
      shadow.innerHTML = `
        <style>
          :host {
            color-scheme: dark;
            font: 13px/1.35 system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          * { box-sizing: border-box; }
          .box {
            min-width: 250px;
            max-width: min(420px, calc(100vw - 16px));
            padding: 7px;
            color: var(--text-primary, #cdd6f4);
            background: var(--bg-surface, #181825);
            border: 1px solid var(--border-bright, #585b70);
            border-radius: 9px;
            box-shadow: 0 10px 28px rgba(0, 0, 0, .38);
          }
          .actions {
            display: flex;
            gap: 5px;
          }
          button, input {
            font: inherit;
            color: inherit;
            background: var(--bg-elevated, #313244);
            border: 1px solid var(--border, #45475a);
            border-radius: 6px;
          }
          button {
            flex: 1;
            padding: 6px 9px;
            cursor: pointer;
          }
          button:hover, button:focus-visible {
            background: var(--border, #45475a);
            outline: none;
            border-color: var(--accent, #cba6f7);
          }
          button[data-intent="remove"]:hover,
          button[data-intent="remove"]:focus-visible {
            border-color: var(--status-blocked, #ef4444);
          }
          .comment-form {
            display: grid;
            grid-template-columns: 1fr auto;
            gap: 6px;
            align-items: center;
            margin-top: 7px;
          }
          .comment-form[hidden] { display: none; }
          input {
            width: 100%;
            min-width: 0;
            padding: 7px 8px;
            outline: none;
          }
          input:focus {
            border-color: var(--accent, #cba6f7);
            box-shadow: 0 0 0 2px rgba(203, 166, 247, .22);
          }
          .hint {
            color: var(--text-muted, #9399b2);
            white-space: nowrap;
            font-size: 11px;
          }
          .error {
            margin-top: 5px;
            color: var(--status-blocked, #ef4444);
            font-size: 11px;
          }
          .error:empty { display: none; }
        </style>
        <div class="box" role="toolbar" aria-label="Actions for selected canvas text">
          <div class="actions">
            <button type="button" data-intent="explain">Explain</button>
            <button type="button" data-intent="remove">Remove</button>
            <button type="button" data-comment>Comment</button>
          </div>
          <div class="comment-form" hidden>
            <input type="text" aria-label="Comment on selected text" placeholder="Type a comment...">
            <span class="hint">Enter to send</span>
          </div>
          <div class="error" role="status" aria-live="polite"></div>
        </div>`;

      box = shadow.querySelector('.box');
      commentForm = shadow.querySelector('.comment-form');
      commentInput = shadow.querySelector('input');
      errorText = shadow.querySelector('.error');

      shadow.addEventListener('pointerdown', function (event) {
        if (event.target.closest('button')) event.preventDefault();
      });

      shadow.querySelectorAll('[data-intent]').forEach(function (button) {
        button.addEventListener('click', function () {
          sendSelection(button.dataset.intent);
        });
      });

      shadow.querySelector('[data-comment]').addEventListener('click', function () {
        if (!state) return;
        state.mode = 'commenting';
        commentForm.hidden = false;
        box.setAttribute('role', 'dialog');
        errorText.textContent = '';
        position();
        queueMicrotask(function () {
          commentInput.focus();
        });
      });

      commentInput.addEventListener('keydown', function (event) {
        if (event.key !== 'Enter' || event.isComposing) return;
        event.preventDefault();
        const comment = commentInput.value.trim();
        if (comment) sendSelection('comment', comment);
      });
    }

    if (!host.isConnected) document.body.appendChild(host);
  }

  function ensureProcessingHost() {
    if (!processingHost) {
      processingHost = document.createElement('canvas-selection-processing');
      processingHost.style.cssText =
        'position:fixed;inset:0;z-index:2147483646;display:none;pointer-events:none;';
      processingShadow = processingHost.attachShadow({ mode: 'open' });
      processingShadow.innerHTML = `
        <style>
          .pulse {
            position: fixed;
            border-radius: 3px;
            background: rgba(203, 166, 247, .14);
            box-shadow: 0 0 0 0 rgba(203, 166, 247, .08);
            opacity: .5;
            animation: canvas-selection-processing-pulse .8s ease-in-out infinite alternate;
            will-change: background-color, box-shadow, opacity;
          }
          @keyframes canvas-selection-processing-pulse {
            from {
              background: rgba(203, 166, 247, .14);
              box-shadow: 0 0 0 0 rgba(203, 166, 247, .08);
              opacity: .5;
            }
            to {
              background: rgba(203, 166, 247, .58);
              box-shadow: 0 0 0 3px rgba(203, 166, 247, .24);
              opacity: 1;
            }
          }
        </style>
        <div class="layer" aria-hidden="true"></div>`;
    }

    if (!processingHost.isConnected) document.body.appendChild(processingHost);
  }

  function rangeRects(range) {
    try {
      return Array.from(range.getClientRects()).filter(function (rect) {
        return rect.width || rect.height;
      });
    } catch {
      return [];
    }
  }

  function rangeRect(range) {
    const rects = rangeRects(range);
    if (rects.length) return rects[rects.length - 1];
    try {
      const rect = range.getBoundingClientRect();
      return rect && (rect.width || rect.height) ? rect : null;
    } catch {
      return null;
    }
  }

  function renderProcessing() {
    if (!processingRange) return;
    cancelAnimationFrame(processingFrame);
    processingFrame = requestAnimationFrame(function () {
      ensureProcessingHost();
      const rects = rangeRects(processingRange);
      if (!rects.length) {
        clearProcessing();
        return;
      }

      const layer = processingShadow.querySelector('.layer');
      layer.replaceChildren();
      rects.forEach(function (rect) {
        const pulse = document.createElement('span');
        pulse.className = 'pulse';
        pulse.style.left = Math.round(rect.left - 1) + 'px';
        pulse.style.top = Math.round(rect.top) + 'px';
        pulse.style.width = Math.round(rect.width + 2) + 'px';
        pulse.style.height = Math.round(rect.height) + 'px';
        layer.appendChild(pulse);
      });
      processingHost.style.display = 'block';
    });
  }

  function startProcessing(range) {
    processingRange = range.cloneRange();
    renderProcessing();
  }

  function clearProcessing() {
    processingRange = null;
    if (processingHost) processingHost.style.display = 'none';
    if (processingShadow) processingShadow.querySelector('.layer').replaceChildren();
  }

  function surroundingContext(range) {
    try {
      const before = document.createRange();
      before.selectNodeContents(document.body);
      before.setEnd(range.startContainer, range.startOffset);

      const after = document.createRange();
      after.selectNodeContents(document.body);
      after.setStart(range.endContainer, range.endOffset);

      return {
        before: before.toString().slice(-contextLength),
        after: after.toString().slice(0, contextLength)
      };
    } catch {
      return { before: '', after: '' };
    }
  }

  function sectionHint(range) {
    const start = elementFor(range.startContainer);
    const section = start && start.closest('[data-section]');
    const identified = start && start.closest('[id]');
    const value =
      (section && section.getAttribute('data-section')) ||
      (identified && identified.id) ||
      '';
    return sectionPattern.test(value) ? value : null;
  }

  function sameRange(left, right) {
    return left &&
      right &&
      left.startContainer === right.startContainer &&
      left.startOffset === right.startOffset &&
      left.endContainer === right.endContainer &&
      left.endOffset === right.endOffset;
  }

  function captureSelection(selection) {
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
    if (isEditableSelection(selection)) return null;

    const selectedText = selection.toString();
    if (!selectedText.trim()) return null;

    const range = selection.getRangeAt(0).cloneRange();
    const rect = rangeRect(range);
    if (!rect) return null;

    const context = surroundingContext(range);
    return {
      mode: 'actions',
      range: range,
      rect: rect,
      selectedText: selectedText,
      contextBefore: context.before,
      contextAfter: context.after,
      section: sectionHint(range)
    };
  }

  function render() {
    if (!state) return;
    ensureHost();
    commentForm.hidden = state.mode !== 'commenting';
    box.setAttribute('role', state.mode === 'commenting' ? 'dialog' : 'toolbar');
    if (state.mode !== 'commenting') {
      commentInput.blur();
      commentInput.value = '';
    }
    errorText.textContent = '';
    host.style.display = 'block';
    position();
  }

  function position() {
    if (!state) return;
    cancelAnimationFrame(positionFrame);
    positionFrame = requestAnimationFrame(function () {
      if (!state) return;
      ensureHost();
      const rect = rangeRect(state.range) || state.rect;
      if (!rect) return;

      host.style.display = 'block';
      host.style.visibility = 'hidden';
      host.style.transform = 'translate(0, 0)';

      const gap = 8;
      const edge = 8;
      const width = host.offsetWidth;
      const height = host.offsetHeight;
      const left = Math.min(
        Math.max(rect.left + (rect.width - width) / 2, edge),
        Math.max(edge, window.innerWidth - width - edge)
      );
      const below = rect.bottom + gap;
      const top = below + height <= window.innerHeight - edge
        ? below
        : Math.max(edge, rect.top - height - gap);

      host.style.transform =
        'translate(' + Math.round(left) + 'px,' + Math.round(top) + 'px)';
      host.style.visibility = 'visible';
    });
  }

  function hide(clearSelection) {
    cancelAnimationFrame(selectionFrame);
    cancelAnimationFrame(positionFrame);
    selectionFrame = 0;
    positionFrame = 0;
    state = null;
    if (host) {
      host.style.display = 'none';
      host.style.visibility = 'hidden';
    }
    if (commentInput) commentInput.value = '';
    if (errorText) errorText.textContent = '';
    if (clearSelection) {
      const selection = window.getSelection();
      if (selection) selection.removeAllRanges();
    }
  }

  function documentName() {
    try {
      return decodeURIComponent(location.pathname.split('/').pop() || '');
    } catch {
      return location.pathname.split('/').pop() || '';
    }
  }

  function requestFor(intent, comment) {
    if (intent === 'explain') return 'User asked to explain/expand this';
    if (intent === 'remove') return 'User asked to remove this';
    return 'User commented: ' + comment;
  }

  function send(action, payload) {
    if (typeof window.canvasSend === 'function') return window.canvasSend(action, payload);
    if (window.parent !== window) {
      console.error('[canvas] selection action DROPPED: canvasSend is unavailable in a framed document');
      return false;
    }

    const message = Object.assign({}, payload, { action: action });
    const size = JSON.stringify(message).length;
    if (size > 64000) {
      console.error(
        '[canvas] selection action DROPPED: message too large (' +
        size +
        ' > 64000 UTF-16 code units); not sent'
      );
      return false;
    }
    window.parent.postMessage(message, '*');
    return true;
  }

  function sendSelection(intent, comment) {
    if (!state) return;

    const payload = {
      intent: intent,
      doc: documentName(),
      contextBefore: state.contextBefore,
      selectedText: state.selectedText,
      contextAfter: state.contextAfter,
      section: state.section || undefined,
      request: requestFor(intent, comment)
    };

    if (send('canvas-selection', payload)) {
      startProcessing(state.range);
      hide(true);
    } else {
      ensureHost();
      errorText.textContent = 'The selected text or comment is too large to send.';
      position();
    }
  }

  function handleSelectionChange() {
    cancelAnimationFrame(selectionFrame);
    selectionFrame = requestAnimationFrame(function () {
      const captured = captureSelection(window.getSelection());

      if (captured) {
        clearProcessing();
        if (state && state.mode === 'commenting' && sameRange(state.range, captured.range)) {
          position();
          return;
        }
        state = captured;
        render();
        return;
      }

      if (!state || state.mode !== 'commenting') hide(false);
    });
  }

  document.addEventListener('selectionchange', handleSelectionChange);
  document.addEventListener('keydown', function (event) {
    if (event.key !== 'Escape' || event.isComposing || !state) return;
    const wasCommenting = state.mode === 'commenting';
    hide(true);
    if (wasCommenting) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);
  window.addEventListener('resize', function () {
    position();
    renderProcessing();
  });
  window.addEventListener('scroll', function () {
    position();
    renderProcessing();
  }, true);
  window.addEventListener('blur', function () {
    if (state && state.mode === 'actions') hide(false);
  });
  window.addEventListener('message', function (event) {
    if (
      event.source === window.parent &&
      event.data &&
      event.data.action === 'content-updated'
    ) {
      hide(false);
      clearProcessing();
    }
  });
})();
