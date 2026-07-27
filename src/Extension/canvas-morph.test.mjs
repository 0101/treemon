import test from "node:test";
import assert from "node:assert/strict";

// canvas-morph.js ships as a classic <script> injected into agent docs, so it cannot use `export`.
// Importing it for its side effect publishes the pure selection logic for the test runner.
import "./canvas-morph.js";

const morph = globalThis.canvasMorphInternals;

const {
  HIGHLIGHT_CLASS,
  isWhitespaceOnlyChange,
  promoteToBlock,
  dropNested,
  isFlood,
  selectTargets,
  applyHighlight,
} = morph;

/** Minimal node stand-ins — the selection logic only needs tree shape, tag names, and classes. */
function element(tagName, ...children) {
  const node = {
    nodeType: 1,
    tagName,
    children: [],
    parentElement: null,
    isConnected: true,
    classes: new Set(),
    contains(other) {
      let cursor = other;
      while (cursor) {
        if (cursor === node) return true;
        cursor = cursor.parentElement;
      }
      return false;
    },
  };
  node.classList = {
    add: (name) => node.classes.add(name),
    remove: (name) => node.classes.delete(name),
  };
  children.forEach((child) => {
    child.parentElement = node;
    node.children.push(child);
  });
  return node;
}

function textIn(parent, value) {
  return { nodeType: 3, nodeValue: value, parentElement: parent };
}

const textChange = (target, oldValue) => ({ type: "characterData", target, oldValue });
const added = (...nodes) => ({ type: "childList", addedNodes: nodes });

/** A root with enough unchanged siblings that a single hit stays well under the flood threshold. */
function docAround(...blocks) {
  const filler = [element("P"), element("P"), element("P"), element("P")];
  return element("DIV", ...blocks, ...filler);
}

test("a whitespace-only text change is not a change", () => {
  assert.equal(isWhitespaceOnlyChange("  hello  ", "hello"), true);
  assert.equal(isWhitespaceOnlyChange("hello", "hello world"), false);
  assert.equal(isWhitespaceOnlyChange(null, ""), true);
});

test("reindenting a file yields no highlight targets", () => {
  const paragraph = element("P");
  const root = docAround(paragraph);
  const body = textIn(paragraph, "\n     Two passes make up the migration.\n   ");

  const targets = selectTargets([textChange(body, "Two passes make up the migration.")], root, []);

  assert.deepEqual(targets, []);
});

test("whitespace inserted between blocks by reindenting is not a change", () => {
  // Reindenting doesn't only rewrite existing text — it *inserts* whitespace-only text nodes
  // between elements, which arrive as childList additions rather than characterData edits.
  const list = element("UL");
  const root = docAround(list);
  const indent = textIn(list, "\n     ");

  assert.deepEqual(selectTargets([added(indent)], root, []), []);
});

test("text genuinely appended to a block still counts", () => {
  const paragraph = element("P");
  const root = docAround(paragraph);
  const sentence = textIn(paragraph, "One more caveat.");

  assert.deepEqual(selectTargets([added(sentence)], root, []), [paragraph]);
});

test("an edited text node highlights its nearest block, not the inline element", () => {
  const code = element("CODE");
  const paragraph = element("P", code);
  const root = docAround(paragraph);
  const value = textIn(code, "90s");

  const targets = selectTargets([textChange(value, "30s")], root, []);

  assert.deepEqual(targets, [paragraph]);
});

test("promoteToBlock stops at the root rather than escaping it", () => {
  const span = element("SPAN");
  const root = element("DIV", span);

  assert.equal(promoteToBlock(span, root), span);
});

test("a hit inside another hit is dropped so nothing is tinted twice", () => {
  const inner = element("LI");
  const list = element("UL", inner);
  const root = element("DIV", list);

  assert.deepEqual(dropNested([list, inner]), [list]);
});

test("the same block reported by several records is highlighted once", () => {
  const paragraph = element("P");
  const root = docAround(paragraph);
  const first = textIn(paragraph, "one");
  const second = textIn(paragraph, "two");

  const targets = selectTargets(
    [textChange(first, "1"), textChange(second, "2")],
    root,
    []
  );

  assert.deepEqual(targets, [paragraph]);
});

test("an appended block is highlighted", () => {
  const heading = element("H4");
  const root = docAround(heading);

  assert.deepEqual(selectTargets([added(heading)], root, []), [heading]);
});

test("the flood guard trips only past the threshold", () => {
  assert.equal(isFlood(3, 4), true);
  assert.equal(isFlood(1, 4), false);
  assert.equal(isFlood(0, 0), false, "an empty doc must not be treated as a flood");
});

test("a whole-file rewrite highlights nothing rather than everything", () => {
  const blocks = [element("P"), element("P"), element("P"), element("P")];
  const root = element("DIV", ...blocks);

  const targets = selectTargets(blocks.map((block) => added(block)), root, []);

  assert.deepEqual(targets, [], "4 of 4 blocks changed — highlighting all of them says nothing");
});

test("a morph that changes nothing keeps the previous highlight", () => {
  const paragraph = element("P");
  const root = element("DIV", paragraph);

  assert.deepEqual(selectTargets([], root, [paragraph]), [paragraph]);
});

test("a previous highlight on a node the morph removed is dropped", () => {
  const removed = element("P");
  removed.isConnected = false;
  const root = element("DIV");

  assert.deepEqual(selectTargets([], root, [removed]), []);
});

test("applying a highlight clears the blocks that are no longer changed", () => {
  const stale = element("P");
  const fresh = element("P");
  stale.classList.add(HIGHLIGHT_CLASS);

  applyHighlight([fresh], [stale]);

  assert.equal(stale.classes.has(HIGHLIGHT_CLASS), false);
  assert.equal(fresh.classes.has(HIGHLIGHT_CLASS), true);
});
