(function () {
  const hasBrowserRuntime =
    typeof window !== 'undefined' && typeof document !== 'undefined';
  if (hasBrowserRuntime && window.__canvasSelectionContextInstalled) return;
  if (hasBrowserRuntime) window.__canvasSelectionContextInstalled = true;

  const editableSelector = 'input,textarea,select,[contenteditable]:not([contenteditable="false"])';
  const sectionPattern = /^[A-Za-z0-9_-]+$/;
  const contextLength = 160;
  const maxProcessingRects = 200;
  const maxSelectionMetadataChars = 64000;
  const maxSelectionMetadataDepth = 64;
  const invalidMetadataMessage =
    'Selection source context must be a plain serializable JSON object.';
  const metadataExceptionMessage =
    'Selection source context could not be created.';
  const metadataLimitMessage =
    'Selection source context is too large or deeply nested.';
  const oversizedSelectionMessage =
    'The selected text or comment is too large to send.';
  class SelectionMetadataLimitError extends TypeError {}
  /**
   * @typedef SelectionState
   * @property {'actions' | 'commenting'} mode
   * @property {Range} range
   * @property {DOMRect} rect
   * @property {string} selectedText
   * @property {string} contextBefore
   * @property {string} contextAfter
   * @property {string | null} section
   */
  /**
   * @typedef SelectionUi
   * @property {HTMLElement} host
   * @property {HTMLElement} box
   * @property {HTMLElement} commentForm
   * @property {HTMLInputElement} commentInput
   * @property {HTMLElement} errorText
   */
  /**
   * @typedef ProcessingUi
   * @property {HTMLElement} host
   * @property {ShadowRoot} shadow
   */
  /** @typedef {null | string | number | boolean | JsonValue[] | { [key: string]: JsonValue }} JsonValue */
  /** @type {SelectionState | null} */
  let state = null;
  /** @type {SelectionUi | null} */
  let selectionUi = null;
  /** @type {ProcessingUi | null} */
  let processingUi = null;
  /** @type {Range | null} */
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

  function rangeIntersectsEditable(range) {
    const common = elementFor(range.commonAncestorContainer);
    if (common && common.closest(editableSelector)) return true;
    return Array.from(document.querySelectorAll(editableSelector)).some(function (element) {
      try {
        return range.intersectsNode(element);
      } catch {
        return false;
      }
    });
  }

  function requiredElement(root, selector) {
    const element = root.querySelector(selector);
    if (!element) throw new Error('Canvas selection UI is incomplete: ' + selector);
    return element;
  }

  function ensureHost() {
    if (!selectionUi) {
      const host = document.createElement('canvas-selection-context');
      host.style.cssText =
        'position:fixed;left:0;top:0;z-index:2147483647;display:none;visibility:hidden;';
      const shadow = host.attachShadow({ mode: 'open' });
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

      const box = /** @type {HTMLElement} */ (requiredElement(shadow, '.box'));
      const commentForm =
        /** @type {HTMLElement} */ (requiredElement(shadow, '.comment-form'));
      const commentInput =
        /** @type {HTMLInputElement} */ (requiredElement(shadow, 'input'));
      const errorText =
        /** @type {HTMLElement} */ (requiredElement(shadow, '.error'));

      shadow.addEventListener('pointerdown', function (event) {
        const target = event.target;
        if (target instanceof Element && target.closest('button')) event.preventDefault();
      });

      shadow.querySelectorAll('[data-intent]').forEach(function (button) {
        button.addEventListener('click', function () {
          if (!(button instanceof HTMLElement)) return;
          const intent = button.dataset.intent;
          if (intent === 'explain' || intent === 'remove') sendSelection(intent);
        });
      });

      requiredElement(shadow, '[data-comment]').addEventListener('click', function () {
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

      selectionUi = { host, box, commentForm, commentInput, errorText };
    }
    const ui = selectionUi;
    if (!ui.host.isConnected) document.body.appendChild(ui.host);
    return ui;
  }

  function ensureProcessingHost() {
    if (!processingUi) {
      const host = document.createElement('canvas-selection-processing');
      host.style.cssText =
        'position:fixed;inset:0;z-index:2147483646;display:none;pointer-events:none;';
      const shadow = host.attachShadow({ mode: 'open' });
      shadow.innerHTML = `
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
      processingUi = { host, shadow };
    }
    const ui = processingUi;
    if (!ui.host.isConnected) document.body.appendChild(ui.host);
    return ui;
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
      const ui = ensureProcessingHost();
      const allRects = rangeRects(processingRange);
      if (!allRects.length) {
        clearProcessing();
        return;
      }

      const layer = requiredElement(ui.shadow, '.layer');
      layer.replaceChildren();
      const rects = allRects.filter(function (rect) {
        return (
          rect.bottom >= 0 &&
          rect.right >= 0 &&
          rect.top <= window.innerHeight &&
          rect.left <= window.innerWidth
        );
      }).slice(0, maxProcessingRects);
      if (!rects.length) {
        ui.host.style.display = 'none';
        return;
      }
      rects.forEach(function (rect) {
        const pulse = document.createElement('span');
        pulse.className = 'pulse';
        pulse.style.left = Math.round(rect.left - 1) + 'px';
        pulse.style.top = Math.round(rect.top) + 'px';
        pulse.style.width = Math.round(rect.width + 2) + 'px';
        pulse.style.height = Math.round(rect.height) + 'px';
        layer.appendChild(pulse);
      });
      ui.host.style.display = 'block';
    });
  }

  function startProcessing(range) {
    processingRange = range.cloneRange();
    renderProcessing();
  }

  function clearProcessing() {
    processingRange = null;
    if (processingUi) {
      processingUi.host.style.display = 'none';
      requiredElement(processingUi.shadow, '.layer').replaceChildren();
    }
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
    const sectionValue = section && section.getAttribute('data-section');
    if (sectionPattern.test(sectionValue || '')) return sectionValue;
    const idValue = identified && identified.id;
    return sectionPattern.test(idValue || '') ? idValue : null;
  }

  /**
   * @param {Range | null} left
   * @param {Range | null} right
   */
  function sameRange(left, right) {
    return left &&
      right &&
      left.startContainer === right.startContainer &&
      left.startOffset === right.startOffset &&
      left.endContainer === right.endContainer &&
      left.endOffset === right.endOffset;
  }

  /**
   * @param {Selection | null} selection
   * @returns {SelectionState | null}
   */
  function captureSelection(selection) {
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
    if (isEditableSelection(selection)) return null;
    const range = selection.getRangeAt(0).cloneRange();
    if (rangeIntersectsEditable(range)) return null;

    const selectedText = selection.toString();
    if (!selectedText.trim()) return null;

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
    const ui = ensureHost();
    ui.commentForm.hidden = state.mode !== 'commenting';
    ui.box.setAttribute('role', state.mode === 'commenting' ? 'dialog' : 'toolbar');
    if (state.mode !== 'commenting') {
      ui.commentInput.blur();
      ui.commentInput.value = '';
    }
    ui.errorText.textContent = '';
    ui.host.style.display = 'block';
    position();
  }

  function position() {
    if (!state) return;
    cancelAnimationFrame(positionFrame);
    positionFrame = requestAnimationFrame(function () {
      if (!state) return;
      const ui = ensureHost();
      const rect = rangeRect(state.range) || state.rect;
      if (!rect) return;

      ui.host.style.display = 'block';
      ui.host.style.visibility = 'hidden';
      ui.host.style.transform = 'translate(0, 0)';

      const gap = 8;
      const edge = 8;
      const width = ui.host.offsetWidth;
      const height = ui.host.offsetHeight;
      const left = Math.min(
        Math.max(rect.left + (rect.width - width) / 2, edge),
        Math.max(edge, window.innerWidth - width - edge)
      );
      const below = rect.bottom + gap;
      const top = below + height <= window.innerHeight - edge
        ? below
        : Math.max(edge, rect.top - height - gap);

      ui.host.style.transform =
        'translate(' + Math.round(left) + 'px,' + Math.round(top) + 'px)';
      ui.host.style.visibility = 'visible';
    });
  }

  function hide(clearSelection) {
    cancelAnimationFrame(selectionFrame);
    cancelAnimationFrame(positionFrame);
    selectionFrame = 0;
    positionFrame = 0;
    state = null;
    if (selectionUi) {
      selectionUi.host.style.display = 'none';
      selectionUi.host.style.visibility = 'hidden';
      selectionUi.commentInput.value = '';
      selectionUi.errorText.textContent = '';
    }
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

  /**
   * @param {'explain' | 'remove' | 'comment'} intent
   * @param {string} [comment]
   */
  function requestFor(intent, comment) {
    if (intent === 'explain') return 'User asked to explain/expand this';
    if (intent === 'remove') return 'User asked to remove this';
    return 'User commented: ' + (comment ?? '');
  }

  /**
   * @param {unknown} value
   * @returns {value is Record<string, unknown>}
   */
  function isPlainObject(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
    const prototype = Object.getPrototypeOf(value);
    return prototype === Object.prototype || prototype === null;
  }

  /**
   * @param {{ remaining: number }} budget
   * @param {number} size
   */
  function spendMetadataBudget(budget, size) {
    budget.remaining -= size;
    if (budget.remaining < 0) {
      throw new SelectionMetadataLimitError('Selection source context is too large');
    }
  }

  /**
   * @param {unknown} value
   * @param {Set<object>} ancestors
   * @param {{ remaining: number }} budget
   * @param {number} depth
   * @returns {JsonValue}
   */
  function cloneJsonValue(value, ancestors, budget, depth) {
    if (depth > maxSelectionMetadataDepth) {
      throw new SelectionMetadataLimitError('Selection source context is too deeply nested');
    }
    if (value === null) {
      spendMetadataBudget(budget, 4);
      return null;
    }

    if (typeof value === 'string') {
      if (value.length > budget.remaining) {
        throw new SelectionMetadataLimitError('Selection source context is too large');
      }
      spendMetadataBudget(budget, JSON.stringify(value).length);
      return value;
    }
    if (typeof value === 'boolean') {
      spendMetadataBudget(budget, value ? 4 : 5);
      return value;
    }
    if (typeof value === 'number' && Number.isFinite(value)) {
      spendMetadataBudget(budget, JSON.stringify(value).length);
      return value;
    }
    if (typeof value !== 'object') throw new TypeError('Unsupported JSON value');
    if (ancestors.has(value)) throw new TypeError('Cyclic JSON value');

    ancestors.add(value);
    try {
      if (Array.isArray(value)) {
        const minimumSize = value.length === 0 ? 2 : value.length * 2 + 1;
        if (minimumSize > budget.remaining) {
          throw new SelectionMetadataLimitError('Selection source context is too large');
        }
        if (Object.getOwnPropertySymbols(value).length) {
          throw new TypeError('Symbol keys are not supported');
        }
        const keys = Object.keys(value);
        if (
          keys.length !== value.length ||
          keys.some(function (key, index) { return key !== String(index); })
        ) {
          throw new TypeError('Sparse arrays and named properties are not supported');
        }
        spendMetadataBudget(budget, 2 + Math.max(0, value.length - 1));
        return value.map(function (item) {
          return cloneJsonValue(item, ancestors, budget, depth + 1);
        });
      }
      if (!isPlainObject(value)) throw new TypeError('Non-plain JSON object');
      if (Object.getOwnPropertySymbols(value).length) {
        throw new TypeError('Symbol keys are not supported');
      }

      const keys = Object.keys(value);
      spendMetadataBudget(budget, 2 + Math.max(0, keys.length - 1));
      return keys.reduce(function (copy, key) {
        spendMetadataBudget(budget, JSON.stringify(key).length + 1);
        copy[key] = cloneJsonValue(value[key], ancestors, budget, depth + 1);
        return copy;
      }, Object.create(null));
    } finally {
      ancestors.delete(value);
    }
  }

  /** @param {SelectionState} current */
  function selectionMetadataContext(current) {
    return {
      range: current.range.cloneRange(),
      rect: {
        left: current.rect.left,
        top: current.rect.top,
        right: current.rect.right,
        bottom: current.rect.bottom,
        width: current.rect.width,
        height: current.rect.height
      },
      selectedText: current.selectedText,
      contextBefore: current.contextBefore,
      contextAfter: current.contextAfter,
      section: current.section
    };
  }

  /**
   * @param {unknown} metadata
   * @returns {{ status: 'invalid' | 'too-large' } | { status: 'valid', value: JsonValue }}
   */
  function validateSelectionMetadata(metadata) {
    try {
      if (!isPlainObject(metadata)) return { status: 'invalid' };
      return {
        status: 'valid',
        value: cloneJsonValue(
          metadata,
          new Set(),
          { remaining: maxSelectionMetadataChars },
          0
        )
      };
    } catch (error) {
      return {
        status: error instanceof SelectionMetadataLimitError ? 'too-large' : 'invalid'
      };
    }
  }

  /**
   * @param {SelectionState} current
   * @returns {{ status: 'absent' | 'invalid' | 'too-large' | 'exception' } | { status: 'valid', value: JsonValue }}
   */
  function selectionMetadata(current) {
    const hook = window.canvasSelectionMetadata;
    if (hook == null) return { status: 'absent' };
    if (typeof hook !== 'function') return { status: 'invalid' };

    let metadata;
    try {
      metadata = hook(selectionMetadataContext(current));
    } catch (error) {
      console.error('[canvas] selection action DROPPED: canvasSelectionMetadata threw', error);
      return { status: 'exception' };
    }

    return validateSelectionMetadata(metadata);
  }

  /** @param {string} message */
  function showError(message) {
    ensureHost().errorText.textContent = message;
    position();
  }

  /**
   * @param {string} action
   * @param {Record<string, unknown>} payload
   */
  function send(action, payload) {
    if (
      typeof window.canvasSend !== 'function' ||
      (
        window.parent === window &&
        window.__canvasTopLevelTransportAvailable !== true
      )
    ) {
      console.error('[canvas] selection action DROPPED: canvasSend is unavailable');
      return 'transport-unavailable';
    }
    return window.canvasSend(action, payload) ? 'sent' : 'too-large';
  }

  function includeSurroundingContext() {
    const config = window.canvasSelectionConfig;
    return !(config && config.includeSurroundingContext === false);
  }

  /**
   * @param {'explain' | 'remove' | 'comment'} intent
   * @param {string} [comment]
   */
  function sendSelection(intent, comment) {
    if (!state) return;

    /** @type {Record<string, unknown>} */
    const payload = {
      intent: intent,
      doc: documentName()
    };
    const includesContext = includeSurroundingContext();
    if (includesContext) payload.contextBefore = state.contextBefore;
    payload.selectedText = state.selectedText;
    if (includesContext) payload.contextAfter = state.contextAfter;
    payload.section = state.section || undefined;
    payload.request = requestFor(intent, comment);

    const metadata = selectionMetadata(state);
    if (metadata.status === 'invalid') {
      showError(invalidMetadataMessage);
      return;
    }
    if (metadata.status === 'exception') {
      showError(metadataExceptionMessage);
      return;
    }
    if (metadata.status === 'too-large') {
      showError(metadataLimitMessage);
      return;
    }
    if (metadata.status === 'valid') payload.sourceContext = metadata.value;

    const result = send('canvas-selection', payload);
    if (result === 'sent') {
      startProcessing(state.range);
      hide(true);
    } else {
      showError(
        result === 'transport-unavailable'
          ? 'Canvas messaging is unavailable in this document.'
          : oversizedSelectionMessage
      );
    }
  }

  function handleSelectionChange() {
    cancelAnimationFrame(selectionFrame);
    selectionFrame = requestAnimationFrame(function () {
      const selection = window.getSelection();
      if (selection && selection.rangeCount > 0 && !selection.isCollapsed) clearProcessing();
      const captured = captureSelection(selection);

      if (captured) {
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

  if (!hasBrowserRuntime) {
    globalThis.canvasSelectionContextInternals = {
      validateSelectionMetadata: validateSelectionMetadata
    };
    return;
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
    }
  });
  window.addEventListener('canvas-morph-complete', clearProcessing);
})();
