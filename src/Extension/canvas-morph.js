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
 * on the next morph, which strips the class by itself. That is also why a morph which changes
 * nothing (the pane re-signals on tab switch) must re-apply the previous set — otherwise switching
 * tabs would silently clear a highlight that no edit has superseded.
 *
 * Attribute mutations are deliberately NOT observed: our own class writes, and idiomorph stripping
 * them, are attribute mutations — excluding them removes all self-interference without a filter.
 */
(function () {
  'use strict';

  var HIGHLIGHT_CLASS = 'canvas-updated';

  /** Beyond this share of the doc's top-level blocks the edit is a whole-file rewrite, and
   *  highlighting everything says nothing — so highlight nothing instead. */
  var FLOOD_RATIO = 0.6;

  var BLOCK_TAGS = new Set([
    'P', 'LI', 'UL', 'OL', 'DL', 'DT', 'DD',
    'H1', 'H2', 'H3', 'H4', 'H5', 'H6',
    'PRE', 'BLOCKQUOTE', 'TABLE', 'TR', 'FIGURE', 'FIGCAPTION',
    'SECTION', 'ARTICLE', 'HEADER', 'FOOTER', 'ASIDE', 'DETAILS', 'DIV'
  ]);

  function text(value) {
    return (value == null ? '' : String(value)).trim();
  }

  /** Re-indenting a whole file rewrites most of its text nodes without changing what they say. */
  function isWhitespaceOnlyChange(oldValue, newValue) {
    return text(oldValue) === text(newValue);
  }

  function elementFor(node) {
    if (!node) return null;
    return node.nodeType === 1 ? node : node.parentElement || null;
  }

  function isWhitespaceOnlyNode(node) {
    return node.nodeType === 3 && text(node.nodeValue) === '';
  }

  function changedElementsFrom(records) {
    return records.reduce(function (found, record) {
      if (record.type === 'childList') {
        // Re-indenting a file inserts whitespace-only text nodes between blocks; they are
        // additions, but nothing was said that wasn't said before.
        return found.concat(
          Array.prototype.slice.call(record.addedNodes)
            .filter(function (node) { return !isWhitespaceOnlyNode(node); })
            .map(elementFor)
            .filter(Boolean)
        );
      }
      if (record.type === 'characterData') {
        var target = record.target;
        if (isWhitespaceOnlyChange(record.oldValue, target && target.nodeValue)) return found;
        var element = elementFor(target);
        return element ? found.concat([element]) : found;
      }
      return found;
    }, []);
  }

  /** An edit inside a sentence should tint the paragraph, not a ragged inline fragment. */
  function promoteToBlock(element, root) {
    var node = element;
    while (node && node !== root && !BLOCK_TAGS.has(node.tagName)) node = node.parentElement;
    return node && node !== root ? node : element;
  }

  function dropNested(elements) {
    var unique = elements.filter(function (element, index) {
      return elements.indexOf(element) === index;
    });
    return unique.filter(function (element) {
      return !unique.some(function (other) {
        return other !== element && typeof other.contains === 'function' && other.contains(element);
      });
    });
  }

  function isFlood(hitCount, blockCount) {
    return blockCount > 0 && hitCount / blockCount > FLOOD_RATIO;
  }

  /**
   * The nodes to highlight for one morph. `previous` is the still-connected set from the last
   * morph, re-used when this morph changed nothing so a tab switch cannot clear a live highlight.
   */
  function selectTargets(records, root, previous) {
    var hits = dropNested(
      changedElementsFrom(records)
        .filter(function (element) {
          return element !== root && typeof root.contains === 'function' && root.contains(element);
        })
        .map(function (element) { return promoteToBlock(element, root); })
    );
    if (isFlood(hits.length, root.children ? root.children.length : 0)) return [];
    if (hits.length > 0) return hits;
    return (previous || []).filter(function (element) { return element.isConnected; });
  }

  function applyHighlight(targets, previous) {
    (previous || []).forEach(function (element) {
      if (element.classList) element.classList.remove(HIGHLIGHT_CLASS);
    });
    targets.forEach(function (element) {
      if (element.classList) element.classList.add(HIGHLIGHT_CLASS);
    });
    return targets;
  }

  function morphAndHighlight(root, html, previous) {
    var observer = new MutationObserver(function () {});
    observer.observe(root, {
      childList: true,
      subtree: true,
      characterData: true,
      characterDataOldValue: true
    });

    Idiomorph.morph(root, html, { morphStyle: 'innerHTML' });

    var records = observer.takeRecords();
    observer.disconnect();

    // After the morph, never before: idiomorph syncs attributes from the file and would strip
    // a class added ahead of it.
    return applyHighlight(selectTargets(records, root, previous), previous);
  }

  function install() {
    var highlighted = [];

    window.addEventListener('message', function (event) {
      if (event.source !== window.parent) return;
      if (!event.data || event.data.action !== 'content-updated') return;

      fetch(location.href, { cache: 'no-store' })
        .then(function (response) { return response.text(); })
        .then(function (html) {
          var incoming = new DOMParser().parseFromString(html, 'text/html');
          highlighted = morphAndHighlight(document.body, incoming.body.innerHTML, highlighted);
          window.dispatchEvent(new Event('canvas-morph-complete'));
          parent.postMessage({ action: 'morph-complete' }, '*');
        })
        .catch(function (err) { console.error('Morph failed:', err); });
    });
  }

  if (typeof window !== 'undefined' && typeof document !== 'undefined') install();

  // Outside a browser there is nothing to install, so hand the pure selection logic to the test
  // runner instead. `export` is not an option: this file ships as a classic <script> injected into
  // every agent doc, where an export statement is a syntax error.
  if (typeof document === 'undefined') {
    globalThis.canvasMorphInternals = {
      HIGHLIGHT_CLASS: HIGHLIGHT_CLASS,
      FLOOD_RATIO: FLOOD_RATIO,
      isWhitespaceOnlyChange: isWhitespaceOnlyChange,
      changedElementsFrom: changedElementsFrom,
      promoteToBlock: promoteToBlock,
      dropNested: dropNested,
      isFlood: isFlood,
      selectTargets: selectTargets,
      applyHighlight: applyHighlight
    };
  }
})();
