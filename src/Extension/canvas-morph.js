/**
 * Canvas doc morph controller.
 *
 * Listens for the pane's `content-updated` signal, re-fetches the doc, and morphs it into place
 * with idiomorph so scroll position, focus, and in-progress input survive an update.
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

    // After the morph, never before: idiomorph syncs attributes from the file and would strip a
    // class added ahead of it.
    return applyHighlight(selectTargets(records, root, previous), previous);
  }

  function install() {
    var highlighted = [];
    // Signals overlap (a tab re-select racing a poll delta, or several queued behind one slow
    // morph) and two fetches have no completion order, so only the newest response may morph —
    // otherwise a late older response re-morphs the doc back to stale content and tints the undo.
    var generation = 0;

    window.addEventListener('message', function (event) {
      if (event.source !== window.parent) return;
      if (!event.data || event.data.action !== 'content-updated') return;

      var mine = ++generation;
      fetch(location.href, { cache: 'no-store' })
        .then(function (response) {
          // fetch only rejects on network failure, so an error page would otherwise be morphed in
          // as the doc's entire content.
          if (!response.ok) throw new Error('Canvas refetch failed: ' + response.status);
          return response.text();
        })
        .then(function (html) {
          if (mine !== generation) return;
          var incoming = new DOMParser().parseFromString(html, 'text/html');
          highlighted = morphAndHighlight(document.body, incoming.body.innerHTML, highlighted);
          window.dispatchEvent(new Event('canvas-morph-complete'));
          parent.postMessage({ action: 'morph-complete' }, '*');
        })
        .catch(function (err) { console.error('Morph failed:', err); });
    });
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
  // runner instead. `export` is not an option: this file ships as a classic <script> injected into
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
      applyHighlight: applyHighlight
    };
  }
})();
