import test from "node:test";
import assert from "node:assert/strict";

// canvas-morph.js ships as a classic <script> injected into agent docs, so it cannot use `export`.
// Importing it for its side effect publishes the pure selection logic for the test runner.
import "../../Extension/canvas-morph.js";

const morph = globalThis.canvasMorphInternals;

const {
  HIGHLIGHT_CLASS,
  isWhitespaceOnlyChange,
  mutatedContent,
  promoteToBlock,
  dropNested,
  countBlocks,
  blockCoverage,
  isFlood,
  selectTargets,
  applyHighlight,
  captureEditableState,
  restoreEditableState,
  servedContentHash,
  isBrowserProcessedScript,
  hasAuthoredProcessedScript,
  documentShellState,
  requiresDocumentReload,
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
  node.getAttribute = (name) =>
    name === "class" ? Array.from(node.classes).join(" ") : null;
  children.forEach((child) => {
    child.parentElement = node;
    node.children.push(child);
  });
  return node;
}

function textIn(parent, value) {
  return { nodeType: 3, nodeValue: value, parentElement: parent };
}

function commentIn(parent, value) {
  return { nodeType: 8, nodeValue: value, parentElement: parent };
}

const textChange = (target, oldValue) => ({ type: "characterData", target, oldValue });
const attributeChange = (target, attributeName, oldValue) => ({
  type: "attributes",
  target,
  attributeName,
  oldValue,
});
const added = (...nodes) => ({ type: "childList", addedNodes: nodes });
const removed = (...nodes) => ({ type: "childList", addedNodes: [], removedNodes: nodes });

/** A root with enough unchanged siblings that a single hit stays well under the flood threshold. */
function docAround(...blocks) {
  const filler = [element("P"), element("P"), element("P"), element("P")];
  return element("BODY", ...blocks, ...filler);
}

function editableControl(tagName, {
  type = tagName === "INPUT" ? "text" : "",
  id = "",
  name = "",
  value = "",
  defaultValue = "",
  checked = false,
  defaultChecked = false,
  selectionStart = 0,
  selectionEnd = 0,
  selectionDirection = "none",
} = {}) {
  const node = element(tagName);
  Object.assign(node, {
    type,
    id,
    name,
    value,
    defaultValue,
    checked,
    defaultChecked,
    disabled: false,
    selectionStart,
    selectionEnd,
    selectionDirection,
    focused: false,
  });
  node.focus = () => { node.focused = true; };
  node.setSelectionRange = (start, end, direction) => {
    node.selectionStart = start;
    node.selectionEnd = end;
    node.selectionDirection = direction;
  };
  return node;
}

function editableRoot(...controls) {
  const root = element("BODY", ...controls);
  root.querySelectorAll = (selector) => selector === "input, textarea" ? controls : [];
  return root;
}

function script(type = null, runtime = false) {
  return {
    getAttribute: (name) => name === "type" ? type : null,
    hasAttribute: (name) => name === "data-treemon-runtime" && runtime,
  };
}

function scriptDocument(...scripts) {
  return {
    querySelectorAll: (selector) => selector === "script" ? scripts : [],
  };
}

function headElement(outerHTML, runtime = false) {
  return {
    outerHTML,
    hasAttribute: (name) => name === "data-treemon-runtime" && runtime,
  };
}

function attribute(name, value) {
  return { name, value };
}

function shellDocument({
  scripts = [],
  head = [],
  htmlAttributes = [],
  bodyAttributes = [],
} = {}) {
  return {
    querySelectorAll: (selector) => selector === "script" ? scripts : [],
    head: { children: head },
    documentElement: { attributes: htmlAttributes },
    body: { attributes: bodyAttributes },
  };
}

test("only authored browser-processed scripts require document reload", () => {
  const classic = script();
  const module = script("module");
  const legacyMime = script(" TEXT/JAVASCRIPT ; charset=utf-8");
  const importMap = script("importmap");
  const speculationRules = script("speculationrules");
  const json = script("application/json");
  const template = script("text/plain");
  const runtime = script(null, true);

  assert.equal(isBrowserProcessedScript(classic), true);
  assert.equal(isBrowserProcessedScript(module), true);
  assert.equal(isBrowserProcessedScript(legacyMime), true);
  assert.equal(isBrowserProcessedScript(importMap), true);
  assert.equal(isBrowserProcessedScript(speculationRules), true);
  assert.equal(isBrowserProcessedScript(json), false);
  assert.equal(isBrowserProcessedScript(template), false);
  assert.equal(hasAuthoredProcessedScript(scriptDocument(runtime, json)), false);
  assert.equal(hasAuthoredProcessedScript(scriptDocument(runtime, classic)), true);
});

test("script removal and addition both select reload while static documents stay morphable", () => {
  const staticDoc = scriptDocument(script("application/json"), script(null, true));
  const scriptedDoc = scriptDocument(script());

  assert.equal(requiresDocumentReload(staticDoc, staticDoc), false);
  assert.equal(requiresDocumentReload(scriptedDoc, staticDoc), true);
  assert.equal(requiresDocumentReload(staticDoc, scriptedDoc), true);
});

test("authored head and root-attribute changes reload while runtime metadata does not", () => {
  const redStyle = headElement("<style>#target{color:red}</style>");
  const blueStyle = headElement("<style>#target{color:blue}</style>");
  const current = shellDocument({
    head: [
      redStyle,
      headElement("<meta data-treemon-runtime content=\"old\">", true),
    ],
    htmlAttributes: [attribute("lang", "en")],
    bodyAttributes: [attribute("class", "compact")],
  });
  const sameSource = shellDocument({
    head: [
      redStyle,
      headElement("<meta data-treemon-runtime content=\"new\">", true),
    ],
    htmlAttributes: [attribute("lang", "en")],
    bodyAttributes: [attribute("class", "compact")],
  });

  assert.deepEqual(documentShellState(current), documentShellState(sameSource));
  assert.equal(requiresDocumentReload(current, sameSource), false);
  assert.equal(
    requiresDocumentReload(current, shellDocument({
      head: [blueStyle],
      htmlAttributes: [attribute("lang", "en")],
      bodyAttributes: [attribute("class", "compact")],
    })),
    true
  );
  assert.equal(
    requiresDocumentReload(current, shellDocument({
      head: [redStyle],
      htmlAttributes: [attribute("lang", "fr")],
      bodyAttributes: [attribute("class", "compact")],
    })),
    true
  );
  assert.equal(
    requiresDocumentReload(current, shellDocument({
      head: [redStyle],
      htmlAttributes: [attribute("lang", "en")],
      bodyAttributes: [attribute("class", "wide")],
    })),
    true
  );
});

test("live root mutations do not look like authored shell changes", () => {
  const authored = shellDocument({
    bodyAttributes: [attribute("class", "compact")],
  });
  const live = shellDocument({
    bodyAttributes: [attribute("class", "user-expanded")],
  });

  assert.equal(
    requiresDocumentReload(live, authored, documentShellState(authored)),
    false
  );
});

test("head mutations after the source snapshot do not force reload", () => {
  const authoredStyle = headElement("<style>#target{color:red}</style>");
  const authored = shellDocument({ head: [authoredStyle] });
  const live = shellDocument({
    head: [
      authoredStyle,
      headElement("<meta name=\"early-head-injection\">"),
    ],
  });

  assert.equal(
    requiresDocumentReload(live, authored, documentShellState(authored)),
    false
  );
});

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

test("an HTML comment is not visible content", () => {
  const paragraph = element("P");
  const root = docAround(paragraph);
  const note = commentIn(paragraph, " generated by the build ");

  assert.deepEqual(selectTargets([added(note)], root, []), []);
  assert.equal(mutatedContent([added(note)]), false);
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
  const root = element("BODY", span);

  assert.equal(promoteToBlock(span, root), span);
});

test("a hit inside another hit is dropped so nothing is tinted twice", () => {
  const inner = element("LI");
  const list = element("UL", inner);
  const root = element("BODY", list);

  assert.deepEqual(dropNested([list, inner], root), [list]);
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

test("countBlocks counts blocks at every depth, not just the root's children", () => {
  const items = [element("LI"), element("LI"), element("LI")];
  const root = element("BODY", element("H1"), element("UL", ...items));

  assert.equal(countBlocks(root), 5, "h1 + ul + 3 li");
});

test("a hit's coverage includes every block it contains", () => {
  const wrapper = element("DIV", element("P"), element("UL", element("LI")));

  assert.equal(blockCoverage(wrapper), 4, "the div itself + p + ul + li");
});

test("the flood guard trips only past the threshold", () => {
  assert.equal(isFlood(3, 4), true);
  assert.equal(isFlood(1, 4), false);
  assert.equal(isFlood(0, 0), false, "an empty doc must not be treated as a flood");
});

test("editing two list items still highlights them", () => {
  // Regression: the numerator counts blocks at any depth, so the denominator must too. Counting
  // only the root's direct children made this ordinary edit (2 hits, 3 top-level children) read
  // as a 67% rewrite and suppressed the highlight entirely.
  const items = [0, 1, 2, 3, 4].map(() => element("LI"));
  const root = element("BODY", element("H1"), element("P"), element("UL", ...items));
  const edits = [textChange(textIn(items[1], "b"), "x"), textChange(textIn(items[3], "d"), "y")];

  assert.deepEqual(selectTargets(edits, root, []), [items[1], items[3]]);
});

test("a doc wrapped in a single container still highlights", () => {
  // Regression: with one top-level child, any hit was >60% of the root's children, so the feature
  // was permanently dead for every wrapper-shaped document.
  const paragraphs = [0, 1, 2, 3, 4].map(() => element("P"));
  const root = element("BODY", element("MAIN", ...paragraphs));
  const edit = textChange(textIn(paragraphs[2], "changed"), "before");

  assert.deepEqual(selectTargets([edit], root, []), [paragraphs[2]]);
});

test("a whole-file rewrite highlights nothing rather than everything", () => {
  const blocks = [element("P"), element("P"), element("P"), element("P")];
  const root = element("BODY", ...blocks);

  const targets = selectTargets(blocks.map((block) => added(block)), root, []);

  assert.deepEqual(targets, [], "4 of 4 blocks changed — highlighting all of them says nothing");
});

test("replacing a wrapper that holds most of the doc highlights nothing", () => {
  // Regression: a fully populated wrapper arrives as ONE childList record — its descendants were
  // assembled while detached — so counting hits scored a whole-doc rewrite as a single hit, slipped
  // under the flood threshold, and tinted the entire document through that one wrapper.
  const wrapper = element("DIV", ...[0, 1, 2, 3, 4, 5].map(() => element("P")));
  const root = element("BODY", element("H1"), wrapper);

  assert.deepEqual(selectTargets([added(wrapper)], root, []), []);
});

test("swapping only an image source highlights its block", () => {
  // Regression: attributes were unobserved, so an update touching only an src/href/input state
  // produced no records at all — indistinguishable from a no-op, which re-applied the *previous*
  // edit's tint while leaving the real change unmarked.
  const image = element("IMG");
  const figure = element("FIGURE", image);
  const root = docAround(figure);
  const stale = element("P");

  const targets = selectTargets([attributeChange(image, "src", "before.png")], root, [stale]);

  assert.deepEqual(targets, [figure]);
});

test("idiomorph stripping our own highlight class is not a change", () => {
  // The previous morph's tint is removed by idiomorph's attribute sync *inside* the observed
  // window. Counting that as an edit would clear a highlight no edit has superseded.
  const paragraph = element("P");
  paragraph.classes.add("note");
  const root = docAround(paragraph);
  const record = attributeChange(paragraph, "class", `note ${HIGHLIGHT_CLASS}`);

  assert.equal(mutatedContent([record]), false);
  assert.deepEqual(selectTargets([record], root, [paragraph]), [paragraph]);
});

test("a class the file genuinely changed still counts", () => {
  const paragraph = element("P");
  paragraph.classes.add("warning");
  const root = docAround(paragraph);
  const record = attributeChange(paragraph, "class", `note ${HIGHLIGHT_CLASS}`);

  assert.deepEqual(selectTargets([record], root, []), [paragraph]);
});

test("a morph that changes nothing keeps the previous highlight", () => {
  const paragraph = element("P");
  const root = docAround(paragraph);

  assert.deepEqual(selectTargets([], root, [paragraph]), [paragraph]);
});

test("a deletion clears the previous highlight instead of re-applying it", () => {
  // Regression: a removal produces no *added* nodes, which used to look identical to "nothing
  // happened" and re-tinted the previous edit's block — pointing at content this edit never
  // touched, and never clearing for an agent that only ever deletes.
  const stale = element("P");
  const dropped = element("SECTION");
  const root = docAround(stale);

  assert.deepEqual(selectTargets([removed(dropped)], root, [stale]), []);
});

test("a previous highlight on a node the morph removed is dropped", () => {
  const gone = element("P");
  gone.isConnected = false;
  const root = element("BODY");

  assert.deepEqual(selectTargets([], root, [gone]), []);
});

test("applying a highlight clears the blocks that are no longer changed", () => {
  const stale = element("P");
  const fresh = element("P");
  stale.classList.add(HIGHLIGHT_CLASS);

  applyHighlight([fresh], [stale]);

  assert.equal(stale.classes.has(HIGHLIGHT_CLASS), false);
  assert.equal(fresh.classes.has(HIGHLIGHT_CLASS), true);
});

test("dirty inputs and textareas survive while untouched controls accept authored values", () => {
  const title = editableControl("INPUT", {
    id: "title",
    value: "User title",
    defaultValue: "Agent title",
  });
  const notes = editableControl("TEXTAREA", {
    id: "notes",
    value: "User notes",
    defaultValue: "Agent notes",
    selectionStart: 2,
    selectionEnd: 6,
    selectionDirection: "forward",
  });
  const untouched = editableControl("INPUT", {
    id: "untouched",
    value: "Before",
    defaultValue: "Before",
  });
  const root = editableRoot(title, notes, untouched);
  const snapshot = captureEditableState(root, notes);

  title.value = title.defaultValue = "Revised title";
  notes.value = notes.defaultValue = "Revised notes";
  untouched.value = untouched.defaultValue = "After";

  restoreEditableState(root, snapshot);

  assert.equal(title.value, "User title");
  assert.equal(title.defaultValue, "Revised title");
  assert.equal(notes.value, "User notes");
  assert.equal(notes.defaultValue, "Revised notes");
  assert.equal(notes.focused, true);
  assert.deepEqual(
    [notes.selectionStart, notes.selectionEnd, notes.selectionDirection],
    [2, 6, "forward"]
  );
  assert.equal(untouched.value, "After");
});

test("dirty checkbox and radio state survives while untouched controls accept authored state", () => {
  const alerts = editableControl("INPUT", {
    type: "checkbox",
    id: "alerts",
    checked: false,
    defaultChecked: true,
  });
  const modeA = editableControl("INPUT", {
    type: "radio",
    id: "mode-a",
    name: "mode",
    checked: false,
    defaultChecked: true,
  });
  const modeB = editableControl("INPUT", {
    type: "radio",
    id: "mode-b",
    name: "mode",
    checked: true,
    defaultChecked: false,
  });
  const untouched = editableControl("INPUT", {
    type: "checkbox",
    id: "untouched-check",
    checked: false,
    defaultChecked: false,
  });
  const root = editableRoot(alerts, modeA, modeB, untouched);
  const snapshot = captureEditableState(root, modeB);

  alerts.checked = alerts.defaultChecked = true;
  modeA.checked = modeA.defaultChecked = true;
  modeB.checked = modeB.defaultChecked = false;
  untouched.checked = untouched.defaultChecked = true;

  restoreEditableState(root, snapshot);

  assert.equal(alerts.checked, false);
  assert.equal(alerts.defaultChecked, true);
  assert.equal(modeA.checked, false);
  assert.equal(modeA.defaultChecked, true);
  assert.equal(modeB.checked, true);
  assert.equal(modeB.defaultChecked, false);
  assert.equal(modeB.focused, true);
  assert.equal(untouched.checked, true);
});

test("a replaced unnamed control is restored only when the control layout still matches", () => {
  const original = editableControl("INPUT", {
    value: "User value",
    defaultValue: "",
  });
  const snapshot = captureEditableState(editableRoot(original), null);
  original.isConnected = false;

  const replacement = editableControl("INPUT", {
    value: "Authored value",
    defaultValue: "Authored value",
  });
  restoreEditableState(editableRoot(replacement), snapshot);
  assert.equal(replacement.value, "User value");

  const shifted = editableControl("INPUT", {
    value: "Different field",
    defaultValue: "Different field",
  });
  const authored = editableControl("INPUT", {
    value: "Authored value",
    defaultValue: "Authored value",
  });
  restoreEditableState(editableRoot(shifted, authored), snapshot);
  assert.equal(authored.value, "Authored value");
});

test("a retained control is not restored after its id or name changes", () => {
  const control = editableControl("INPUT", {
    id: "old-id",
    name: "old-name",
    value: "User value",
    defaultValue: "Agent value",
  });
  const root = editableRoot(control);
  const snapshot = captureEditableState(root, null);

  control.id = "new-id";
  control.name = "new-name";
  control.value = control.defaultValue = "Different field";

  restoreEditableState(root, snapshot);

  assert.equal(control.value, "Different field");
});

test("the refetch content hash comes from the served response header", () => {
  const servedHash = "b".repeat(64);
  const response = {
    headers: {
      get: (name) => name === "X-Treemon-Canvas-Content-Hash" ? servedHash : null,
    },
  };

  assert.equal(servedContentHash(response), servedHash);
  assert.throws(
    () => servedContentHash({ headers: { get: () => null } }),
    /no valid content hash/
  );
});
