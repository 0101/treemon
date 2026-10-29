import { relative } from "node:path";
import { isSystemViewFilename } from "./canvas-doc-kinds.mjs";
import { canvasFilenamesForTool } from "./canvas-ownership.mjs";

/** @param {string} html */
function wordCount(html) {
  return (html
    .replace(/<[^>]*>/g, " ")
    .replace(/&(?:#x[\da-f]+|#\d+|[a-z][a-z\d]*);/gi, " ")
    .match(/[\p{L}\p{N}]+/gu) ?? []).length;
}

/** @param {string} html */
export function measureCanvasProse(html) {
  const source = html.replace(
    /<!--[\s\S]*?-->|<(head|script|style|template|pre|code|title|desc|defs)\b[^>]*>[\s\S]*?<\/\1\s*>/gi,
    " ",
  );
  let disclosureDepth = 0;
  const overview = source
    .split(/(<\/?details\b[^>]*>)/gi)
    .filter((part) => {
      if (/^<details\b/i.test(part)) {
        disclosureDepth += 1;
        return false;
      }
      if (/^<\/details\b/i.test(part)) {
        disclosureDepth = Math.max(0, disclosureDepth - 1);
        return false;
      }
      return disclosureDepth === 0;
    })
    .join(" ");
  const blocks = overview.split(/<\/(?:p|li|div|section|h[1-6]|td|th|blockquote|text|svg|figure|figcaption)\s*>|<br\b[^>]*>/gi);
  return {
    words: wordCount(overview),
    longestBlock: blocks.reduce((longest, block) => Math.max(longest, wordCount(block)), 0),
  };
}

/**
 * @param {{
 *   worktreePath: string,
 *   readCanvasFile: (filename: string) => Promise<string>,
 *   log: (message: string) => void
 * }} options
 * @returns {import("@github/copilot-sdk").SessionHooks}
 */
export function createCanvasReadabilityHooks({ worktreePath, readCanvasFile, log }) {
  /** @type {Map<string, { measurements: Map<string, ReturnType<typeof measureCanvasProse>>, warned: Set<string> }>} */
  const sessions = new Map();
  return {
    onUserPromptSubmitted: ({ sessionId }) => {
      sessions.get(sessionId)?.warned.clear();
    },
    onSessionEnd: ({ sessionId }) => {
      sessions.delete(sessionId);
    },
    onPostToolUse: async ({ sessionId, workingDirectory, toolName, toolArgs, toolResult }) => {
      if (toolResult.resultType !== "success" || relative(worktreePath, workingDirectory) !== "") return;
      const filenames = canvasFilenamesForTool(toolName, toolArgs, worktreePath)
        .filter((filename) => !isSystemViewFilename(filename));
      if (filenames.length === 0) return;

      const state = sessions.get(sessionId) ?? { measurements: new Map(), warned: new Set() };
      sessions.set(sessionId, state);
      const warnings = [];
      for (const filename of filenames) {
        if (warnings.length === 3) break;
        let html;
        try {
          html = await readCanvasFile(filename);
        } catch (error) {
          log(`canvas readability: read failed for ${JSON.stringify(filename)} (${error.code ?? error.name})`);
          continue;
        }
        const current = measureCanvasProse(html);
        const previous = state.measurements.get(filename);
        state.measurements.set(filename, current);
        if (state.warned.has(filename)) continue;
        const facts = [
          current.words > 450 && (!previous || current.words > previous.words)
            ? `rough main-view text is ${current.words} words (guide: 450)` : null,
          current.longestBlock > 70 && (!previous || current.longestBlock > previous.longestBlock)
            ? `longest prose block is ${current.longestBlock} words (guide: 70)` : null,
        ].filter(Boolean);
        if (facts.length === 0) continue;
        state.warned.add(filename);
        warnings.push(`${JSON.stringify(filename)}: ${facts.join("; ")}.`);
      }
      if (warnings.length === 0) return;
      log(`canvas readability: flagged ${warnings.length} document(s)`);
      return {
        additionalContext:
          `Canvas readability: ${warnings.join(" ")} ` +
          "Keep the outcome, essential evidence, caveats, and useful diagrams. " +
          "Use short, labeled single-sentence points; replace repetition and put background in named <details>. " +
          "Preserve requested depth and unrelated sections. Do not just turn long prose into long bullets.",
      };
    },
  };
}
