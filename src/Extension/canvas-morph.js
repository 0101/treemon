/**
 * Canvas doc morph controller.
 *
 * Listens for the pane's `content-updated` signal and re-fetches the doc. Static documents morph in
 * place with idiomorph so scroll position and focus survive an update. Changes outside the morph
 * target — authored head elements or html/body attributes — reload so styles and document metadata
 * do not stay stale. Documents with authored browser-processed scripts reload too: parser-created
 * scripts do not execute after a body morph, and explicitly rerunning arbitrary author code could
 * duplicate listeners and side effects.
 * Dirty input and textarea values, plus checkbox/radio checked state, are snapshotted immediately
 * before a static morph and restored afterward; untouched controls still receive the new authored
 * state.
 *
 * It also marks what the update changed. Idiomorph is mutation-minimal — it writes a text node or
 * an attribute only when the value actually differs — so a MutationObserver wrapped around the
 * morph yields exactly the nodes the update touched. Those are promoted to their nearest block,
 * deduplicated, and tagged with `canvas-updated`.
 *
 * The tag needs no timer: idiomorph reconciles every matched element's attributes against the file
 * on the next morph, which strips the class by itself. That is also why a morph which mutates
 * nothing (the pane re-signals on tab switch) must re-apply the previous set — otherwise switching
 * tabs would silently clear a highlight no edit has superseded. A morph that *does* mutate
 * something but leaves nothing to tint (a pure deletion) clears instead, so the tint never points
 * at content the latest edit did not touch.
 *
 * Attribute mutations count as well: an update that only swaps an `src`, an `href`, or an input's
 * state produces no other kind of record, so ignoring them would make a real edit look like a no-op
 * and re-apply the previous edit's tint. Idiomorph stripping our own `canvas-updated` class is
 * itself an attribute mutation, so `class` records are compared with that class removed from both
 * sides; our own writes need no filter because they run after the observer is disconnected.
 */
(function () {
  'use strict';

  var HIGHLIGHT_CLASS = 'canvas-updated';
  var CONTENT_HASH_HEADER = 'X-Treemon-Canvas-Content-Hash';
  var CONTENT_HASH_META_NAME = 'treemon-canvas-content-hash';
  var SHELL_HASH_META_NAME = 'treemon-canvas-shell-hash';
  var RUNTIME_ATTRIBUTE = 'data-treemon-runtime';
  var JAVASCRIPT_MIME_TYPES = new Set([
    'application/ecmascript',
    'application/javascript',
    'application/x-ecmascript',
    'application/x-javascript',
    'text/ecmascript',
    'text/javascript',
    'text/javascript1.0',
    'text/javascript1.1',
    'text/javascript1.2',
    'text/javascript1.3',
    'text/javascript1.4',
    'text/javascript1.5',
    'text/jscript',
    'text/livescript',
    'text/x-ecmascript',
    'text/x-javascript'
  ]);

  /** Beyond this share of the doc's blocks the edit is a whole-file rewrite, and highlighting
   *  everything says nothing — so highlight nothing instead. */
  var FLOOD_RATIO = 0.6;

  var BLOCK_TAGS = new Set([
    'P', 'LI', 'UL', 'OL', 'DL', 'DT', 'DD',
    'H1', 'H2', 'H3', 'H4', 'H5', 'H6',
    'PRE', 'BLOCKQUOTE', 'TABLE', 'TR', 'FIGURE', 'FIGCAPTION',
    'SECTION', 'ARTICLE', 'HEADER', 'FOOTER', 'ASIDE', 'DETAILS', 'DIV'
  ]);

  var ELEMENT_NODE = 1;
  var TEXT_NODE = 3;
  var COMMENT_NODE = 8;
  var EXCLUDED_INPUT_TYPES = new Set([
    'button', 'file', 'hidden', 'image', 'reset', 'submit'
  ]);
  var CHECKABLE_INPUT_TYPES = new Set(['checkbox', 'radio']);
  var SELECTION_INPUT_TYPES = new Set(['text', 'search', 'url', 'tel', 'password']);

  function text(value) {
    return (value == null ? '' : String(value)).trim();
  }

  /** Re-indenting a whole file rewrites most of its text nodes without changing what they say. */
  function isWhitespaceOnlyChange(oldValue, newValue) {
    return text(oldValue) === text(newValue);
  }

  /** Comments and pure indentation carry no content: an agent's HTML comment or a reflow is not a
   *  change the reader can see, so neither may light a block up. */
  function isIgnorable(node) {
    if (!node) return true;
    if (node.nodeType === COMMENT_NODE) return true;
    return node.nodeType === TEXT_NODE && text(node.nodeValue) === '';
  }

  function elementFor(node) {
    if (!node) return null;
    return node.nodeType === ELEMENT_NODE ? node : node.parentElement || null;
  }

  function nodesOf(list) {
    return list ? Array.prototype.slice.call(list) : [];
  }

  function isBrowserProcessedScript(script) {
    var type = text(script.getAttribute('type')).toLowerCase().split(';')[0].trim();
    return type === '' ||
      type === 'module' ||
      type === 'importmap' ||
      type === 'speculationrules' ||
      JAVASCRIPT_MIME_TYPES.has(type);
  }

  function hasAuthoredProcessedScript(root) {
    return nodesOf(root.querySelectorAll('script')).some(function (script) {
      return !script.hasAttribute(RUNTIME_ATTRIBUTE) &&
        isBrowserProcessedScript(script);
    });
  }

  function documentMetaHash(root, name) {
    var meta = root.querySelector(
      'meta[' + RUNTIME_ATTRIBUTE + '][name="' + name + '"]'
    );
    var value = meta && meta.getAttribute('content');
    return isContentHash(value) ? value : null;
  }

  function requiresDocumentReload(current, incoming, loadedShellHash) {
    var incomingShellHash = documentMetaHash(incoming, SHELL_HASH_META_NAME);
    return hasAuthoredProcessedScript(current.body || current) ||
      hasAuthoredProcessedScript(incoming) ||
      !loadedShellHash ||
      !incomingShellHash ||
      loadedShellHash !== incomingShellHash;
  }

  function isEditableControl(control) {
    return control.tagName === 'TEXTAREA' ||
      (control.tagName === 'INPUT' && !EXCLUDED_INPUT_TYPES.has(control.type));
  }

  function editableControls(root) {
    return nodesOf(root.querySelectorAll('input, textarea')).filter(isEditableControl);
  }

  function isCheckableControl(control) {
    return control.tagName === 'INPUT' && CHECKABLE_INPUT_TYPES.has(control.type);
  }

  function controlIdentity(control) {
    return {
      tagName: control.tagName,
      type: control.tagName === 'INPUT' ? control.type : '',
      id: control.id || '',
      name: control.name || ''
    };
  }

  function sameControlType(control, identity) {
    return control &&
      control.tagName === identity.tagName &&
      (identity.tagName !== 'INPUT' || control.type === identity.type);
  }

  function sameControlIdentity(left, right) {
    return left.tagName === right.tagName &&
      left.type === right.type &&
      left.id === right.id &&
      left.name === right.name;
  }

  function uniqueIdentityValue(controls, control, field) {
    var value = control[field];
    return value && controls.filter(function (candidate) {
      return sameControlType(candidate, controlIdentity(control)) &&
        candidate[field] === value;
    }).length === 1 ? value : '';
  }

  function supportsSelection(control) {
    return control.tagName === 'TEXTAREA' ||
      (control.tagName === 'INPUT' && SELECTION_INPUT_TYPES.has(control.type));
  }

  function selectionState(control, activeElement) {
    if (control !== activeElement || !supportsSelection(control)) return null;
    return {
      start: control.selectionStart,
      end: control.selectionEnd,
      direction: control.selectionDirection || 'none'
    };
  }

  function captureEditableState(root, activeElement) {
    var controls = editableControls(root);
    var focused = activeElement ||
      (typeof document !== 'undefined' ? document.activeElement : null);

    return {
      layout: controls.map(controlIdentity),
      fields: controls.map(function (control, index) {
        var checkable = isCheckableControl(control);
        if (checkable
          ? control.checked === control.defaultChecked
          : control.value === control.defaultValue) return null;
        var state = {
          element: control,
          identity: controlIdentity(control),
          uniqueId: uniqueIdentityValue(controls, control, 'id'),
          uniqueName: uniqueIdentityValue(controls, control, 'name'),
          index: index,
          active: control === focused,
          selection: selectionState(control, focused)
        };
        if (checkable) state.checked = control.checked;
        else state.value = control.value;
        return state;
      }).filter(Boolean)
    };
  }

  function sameControlLayout(controls, layout) {
    return controls.length === layout.length &&
      controls.every(function (control, index) {
        return sameControlIdentity(controlIdentity(control), layout[index]);
      });
  }

  function matchingControl(root, controls, state, layoutMatches) {
    if (state.element &&
        state.element.isConnected &&
        root.contains(state.element) &&
        sameControlIdentity(controlIdentity(state.element), state.identity)) {
      return state.element;
    }

    var byId = state.uniqueId && controls.filter(function (control) {
      return sameControlType(control, state.identity) && control.id === state.uniqueId;
    });
    if (byId && byId.length === 1) return byId[0];

    var byName = state.uniqueName && controls.filter(function (control) {
      return sameControlType(control, state.identity) && control.name === state.uniqueName;
    });
    if (byName && byName.length === 1) return byName[0];

    return layoutMatches ? controls[state.index] || null : null;
  }

  function restoreEditableState(root, snapshot) {
    var controls = editableControls(root);
    var layoutMatches = sameControlLayout(controls, snapshot.layout);

    snapshot.fields.forEach(function (state) {
      var control = matchingControl(root, controls, state, layoutMatches);
      if (!control) return;

      if (Object.prototype.hasOwnProperty.call(state, 'checked')) {
        control.checked = state.checked;
      } else {
        control.value = state.value;
      }

      if (state.active && !control.disabled) {
        control.focus({ preventScroll: true });
        if (state.selection && supportsSelection(control)) {
          control.setSelectionRange(
            state.selection.start,
            state.selection.end,
            state.selection.direction
          );
        }
      }
    });
  }

  function isContentHash(value) {
    return /^[0-9a-f]{64}$/.test(value || '');
  }

  function servedContentHash(response) {
    var contentHash = response.headers.get(CONTENT_HASH_HEADER);
    if (!isContentHash(contentHash)) {
      throw new Error('Canvas refetch returned no valid content hash');
    }
    return contentHash;
  }

  function isVisibleTextChange(record) {
    return !isIgnorable(record.target) &&
      !isWhitespaceOnlyChange(record.oldValue, record.target && record.target.nodeValue);
  }

  /** Class names with our own tag removed and the rest ordered, so neither idiomorph stripping the
   *  highlight nor a reshuffled attribute reads as an edit. */
  function classSignature(value) {
    return (value == null ? '' : String(value)).split(/\s+/)
      .filter(function (name) { return name && name !== HIGHLIGHT_CLASS; })
      .sort()
      .join(' ');
  }

  function isVisibleAttributeChange(record) {
    return record.attributeName !== 'class' ||
      classSignature(record.oldValue) !== classSignature(record.target.getAttribute('class'));
  }

  /** Whether a record that names a single target reports something the reader can see. */
  function isVisibleChange(record) {
    if (record.type === 'characterData') return isVisibleTextChange(record);
    if (record.type === 'attributes') return isVisibleAttributeChange(record);
    return false;
  }

  /**
   * Whether the morph changed the document at all — including changes with nothing left to
   * highlight, such as a pure deletion. Distinguishing this from "produced no targets" is what
   * stops a deletion from re-applying the previous edit's tint.
   */
  function mutatedContent(records) {
    return records.some(function (record) {
      if (record.type === 'childList') {
        return nodesOf(record.addedNodes).concat(nodesOf(record.removedNodes))
          .some(function (node) { return !isIgnorable(node); });
      }
      return isVisibleChange(record);
    });
  }

  function changedElementsFrom(records) {
    return records.flatMap(function (record) {
      if (record.type === 'childList') {
        return nodesOf(record.addedNodes)
          .filter(function (node) { return !isIgnorable(node); })
          .map(elementFor)
          .filter(Boolean);
      }
      return isVisibleChange(record) ? [elementFor(record.target)].filter(Boolean) : [];
    });
  }

  /** An edit inside a sentence should tint the paragraph, not a ragged inline fragment. */
  function promoteToBlock(element, root) {
    var node = element;
    while (node && node !== root && !BLOCK_TAGS.has(node.tagName)) node = node.parentElement;
    return node && node !== root ? node : element;
  }

  function dropNested(elements, root) {
    var unique = Array.from(new Set(elements));
    var candidates = new Set(unique);
    return unique.filter(function (element) {
      var ancestor = element.parentElement;
      while (ancestor && ancestor !== root) {
        if (candidates.has(ancestor)) return false;
        ancestor = ancestor.parentElement;
      }
      return true;
    });
  }

  /**
   * Every block in the doc, at any depth — the same population the hits are drawn from. Counting
   * only `root`'s direct children would compare a whole-tree numerator against a top-level-only
   * denominator: the ratio can exceed 1, which fires the guard on ordinary two-block edits and
   * permanently disables the highlight for a doc wrapped in a single container.
   */
  function countBlocks(node) {
    return Array.prototype.slice.call(node.children || []).reduce(function (total, child) {
      return total + (BLOCK_TAGS.has(child.tagName) ? 1 : 0) + countBlocks(child);
    }, 0);
  }

  function isFlood(coveredBlocks, blockCount) {
    return blockCount > 0 && coveredBlocks / blockCount > FLOOD_RATIO;
  }

  /**
   * How many of the doc's blocks one hit accounts for: itself, plus every block it contains. A
   * freshly populated wrapper arrives as a *single* record — its descendants were assembled while
   * detached — so counting hits instead would let a whole-doc rewrite slip under the flood
   * threshold and tint the entire document through that one wrapper.
   */
  function blockCoverage(element) {
    return (BLOCK_TAGS.has(element.tagName) ? 1 : 0) + countBlocks(element);
  }

  /**
   * The blocks to highlight for one morph. `previous` is the still-connected set from the last
   * morph, re-used only when this morph mutated nothing at all.
   */
  function selectTargets(records, root, previous) {
    var hits = dropNested(
      changedElementsFrom(records)
        .filter(function (element) { return element !== root && root.contains(element); })
        .map(function (element) { return promoteToBlock(element, root); }),
      root
    );
    var covered = hits.reduce(function (total, hit) { return total + blockCoverage(hit); }, 0);
    if (isFlood(covered, countBlocks(root))) return [];
    if (hits.length > 0) return hits;
    if (mutatedContent(records)) return [];
    return (previous || []).filter(function (element) { return element.isConnected; });
  }

  function applyHighlight(targets, previous) {
    (previous || []).forEach(function (element) { element.classList.remove(HIGHLIGHT_CLASS); });
    targets.forEach(function (element) { element.classList.add(HIGHLIGHT_CLASS); });
    return targets;
  }

  function morphAndHighlight(root, html, previous) {
    var editableState = captureEditableState(root);
    var observer = new MutationObserver(function () {});
    observer.observe(root, {
      childList: true,
      subtree: true,
      characterData: true,
      characterDataOldValue: true,
      attributes: true,
      attributeOldValue: true
    });

    Idiomorph.morph(root, html, { morphStyle: 'innerHTML' });

    var records = observer.takeRecords();
    observer.disconnect();
    restoreEditableState(root, editableState);

    // After the morph, never before: idiomorph syncs attributes from the file and would strip a
    // class added ahead of it.
    return applyHighlight(selectTargets(records, root, previous), previous);
  }

  function postMorphComplete(morph, contentHash) {
    parent.postMessage({
      action: 'morph-complete',
      scopedKey: morph.scopedKey,
      filename: morph.filename,
      contentHash: contentHash
    }, '*');
  }

  function loadedDocumentState() {
    var contentHash = documentMetaHash(document, CONTENT_HASH_META_NAME);
    var shellHash = documentMetaHash(document, SHELL_HASH_META_NAME);
    if (!contentHash || !shellHash) return null;

    var lastSlash = location.pathname.lastIndexOf('/');
    return {
      scopedKey: decodeURIComponent(location.pathname.substring(1, lastSlash)),
      filename: decodeURIComponent(location.pathname.substring(lastSlash + 1)),
      contentHash: contentHash,
      shellHash: shellHash
    };
  }

  function install() {
    var loadedDocument = loadedDocumentState();
    var loadedContentHash = loadedDocument && loadedDocument.contentHash;
    var loadedShellHash = loadedDocument && loadedDocument.shellHash;
    var loadedCompletionSent = false;
    var reloading = false;
    var highlighted = [];
    // Signals overlap (a tab re-select racing a poll delta, or several queued behind one slow
    // morph) and two fetches have no completion order, so only the newest response may morph —
    // otherwise a late older response re-morphs the doc back to stale content and tints the undo.
    var generation = 0;
    /** @type {{ key: string, generation: number } | null} */
    var pendingMorph = null;

    window.addEventListener('message', function (event) {
      if (event.source !== window.parent) return;
      if (!event.data || event.data.action !== 'content-updated') return;

      var morph = {
        scopedKey: event.data.scopedKey,
        filename: event.data.filename,
        contentHash: event.data.contentHash
      };
      if (reloading) return;
      if (loadedContentHash && morph.contentHash === loadedContentHash) {
        loadedCompletionSent = true;
        postMorphComplete(morph, loadedContentHash);
        return;
      }

      var key = [morph.scopedKey, morph.filename, morph.contentHash].join('\u0000');
      if (pendingMorph && pendingMorph.key === key) return;

      var mine = ++generation;
      pendingMorph = { key: key, generation: mine };
      fetch(location.href, { cache: 'no-store' })
        .then(function (response) {
          // fetch only rejects on network failure, so an error page would otherwise be morphed in
          // as the doc's entire content.
          if (!response.ok) throw new Error('Canvas refetch failed: ' + response.status);
          var contentHash = servedContentHash(response);
          return response.text().then(function (html) {
            return { html: html, contentHash: contentHash };
          });
        })
        .then(function (refetched) {
          if (mine !== generation) return;
          pendingMorph = null;
          if (refetched.contentHash === loadedContentHash) {
            loadedCompletionSent = true;
            postMorphComplete(morph, refetched.contentHash);
            return;
          }
          var incoming = new DOMParser().parseFromString(refetched.html, 'text/html');
          if (requiresDocumentReload(document, incoming, loadedShellHash)) {
            reloading = true;
            location.reload();
            return;
          }
          highlighted = morphAndHighlight(document.body, incoming.body.innerHTML, highlighted);
          loadedContentHash = refetched.contentHash;
          loadedShellHash = documentMetaHash(incoming, SHELL_HASH_META_NAME);
          loadedCompletionSent = true;
          window.dispatchEvent(new Event('canvas-morph-complete'));
          postMorphComplete(morph, refetched.contentHash);
        })
        .catch(function (err) {
          if (mine === generation) {
            pendingMorph = null;
            reloading = false;
          }
          console.error('Morph failed:', err);
        });
    });

    function completeLoadedDocument() {
      if (!loadedDocument || !loadedContentHash || loadedCompletionSent) return;
      loadedCompletionSent = true;
      postMorphComplete(loadedDocument, loadedContentHash);
    }

    if (document.readyState === 'loading') {
      window.addEventListener('DOMContentLoaded', completeLoadedDocument, { once: true });
    } else {
      completeLoadedDocument();
    }
  }

  if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    // The controller owns per-doc state, so a second copy would morph twice per signal and race
    // its own highlight bookkeeping.
    if (!window.canvasMorphInstalled) {
      window.canvasMorphInstalled = true;
      install();
    }
  }

  // Outside a browser there is nothing to install, so hand the pure selection logic to the test
  // runner instead. `export` is not an option: this file ships as a classic script injected into
  // every agent doc, where an export statement is a syntax error.
  if (typeof document === 'undefined') {
    globalThis.canvasMorphInternals = {
      HIGHLIGHT_CLASS: HIGHLIGHT_CLASS,
      FLOOD_RATIO: FLOOD_RATIO,
      isWhitespaceOnlyChange: isWhitespaceOnlyChange,
      isVisibleAttributeChange: isVisibleAttributeChange,
      mutatedContent: mutatedContent,
      changedElementsFrom: changedElementsFrom,
      promoteToBlock: promoteToBlock,
      dropNested: dropNested,
      countBlocks: countBlocks,
      blockCoverage: blockCoverage,
      isFlood: isFlood,
      selectTargets: selectTargets,
      applyHighlight: applyHighlight,
      captureEditableState: captureEditableState,
      restoreEditableState: restoreEditableState,
      servedContentHash: servedContentHash,
      isBrowserProcessedScript: isBrowserProcessedScript,
      hasAuthoredProcessedScript: hasAuthoredProcessedScript,
      documentMetaHash: documentMetaHash,
      requiresDocumentReload: requiresDocumentReload
    };
  }
})();
