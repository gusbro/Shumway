// A Prolog editor with no editor dependency.
//
// A real <textarea> does the editing — caret, selection, undo, IME, spell-check
// off, accessibility, mobile keyboards — and a <pre> behind it, holding the same
// text split into coloured spans, does the looking. The textarea's own text is
// transparent, so what you see is the <pre> and what you type is the textarea.
// The two stay aligned because they share font, padding, border and wrapping.
//
// The colouring itself comes from the ENGINE's lexer (see SyntaxHighlighter), so
// it cannot drift from how the reader reads the file.

/**
 * @param {HTMLTextAreaElement} textarea
 * @param {HTMLElement} backdrop the <pre> behind it
 * @param {(src: string) => Promise<Int32Array|number[]>} highlight flat [start,len,kind,…]
 * @param {string[]} kindNames index -> CSS class
 * @param {(prefix: string) => Promise<string[]>} complete
 */
export function attach(textarea, backdrop, highlight, kindNames, complete) {
  let pendingRepaint = 0;

  async function repaint() {
    const src = textarea.value;
    const spans = await highlight(src);
    const frag = document.createDocumentFragment();
    for (let i = 0; i < spans.length; i += 3) {
      const [start, length, kind] = [spans[i], spans[i + 1], spans[i + 2]];
      const text = src.slice(start, start + length);
      const cls = kindNames[kind];
      if (!cls || cls === 'plain') {
        frag.appendChild(document.createTextNode(text));
      } else {
        const span = document.createElement('span');
        span.className = 'tok-' + cls;
        span.textContent = text;
        frag.appendChild(span);
      }
    }
    // A trailing newline would otherwise not produce a line box, and the last
    // line of the backdrop would sit one row above the caret.
    frag.appendChild(document.createTextNode('\n'));
    backdrop.replaceChildren(frag);
    syncScroll();
  }

  // Repaint on the next frame rather than per keystroke: typing fast should not
  // queue one full re-highlight per character.
  function scheduleRepaint() {
    if (pendingRepaint) return;
    pendingRepaint = requestAnimationFrame(() => { pendingRepaint = 0; repaint(); });
  }

  const syncScroll = () => {
    backdrop.scrollTop = textarea.scrollTop;
    backdrop.scrollLeft = textarea.scrollLeft;
  };

  textarea.addEventListener('input', scheduleRepaint);
  textarea.addEventListener('scroll', syncScroll);

  // --- completion --------------------------------------------------------
  // Tab completes the identifier before the caret. One match is inserted; more
  // than one inserts the longest common prefix and lists them, which is what a
  // shell does and what the console REPL does.

  const listEl = document.createElement('div');
  listEl.className = 'completions';
  listEl.hidden = true;
  backdrop.parentElement.appendChild(listEl);

  const hideList = () => { listEl.hidden = true; };
  textarea.addEventListener('blur', hideList);

  function wordBeforeCaret() {
    const at = textarea.selectionStart;
    const text = textarea.value.slice(0, at);
    const m = /[a-zA-Z_][a-zA-Z0-9_]*$/.exec(text);
    return m ? m[0] : '';
  }

  function longestCommonPrefix(items) {
    if (items.length === 0) return '';
    let prefix = items[0];
    for (const item of items) {
      while (!item.startsWith(prefix)) prefix = prefix.slice(0, -1);
    }
    return prefix;
  }

  function insert(completion, replacing) {
    const at = textarea.selectionStart;
    const before = textarea.value.slice(0, at - replacing.length);
    const after = textarea.value.slice(at);
    textarea.value = before + completion + after;
    const caret = before.length + completion.length;
    textarea.setSelectionRange(caret, caret);
    scheduleRepaint();
  }

  textarea.addEventListener('keydown', async (e) => {
    if (e.key !== 'Tab' || e.shiftKey) { hideList(); return; }
    const word = wordBeforeCaret();
    if (!word) return;                  // no identifier: let Tab move focus
    e.preventDefault();

    const matches = await complete(word);
    if (matches.length === 0) { hideList(); return; }
    if (matches.length === 1) { insert(matches[0], word); hideList(); return; }

    const common = longestCommonPrefix(matches);
    if (common.length > word.length) insert(common, word);
    listEl.textContent = matches.slice(0, 60).join('   ')
      + (matches.length > 60 ? `   … ${matches.length - 60} more` : '');
    listEl.hidden = false;
  });

  /** Marks a parse error at a 1-based line/column, or clears it with null. */
  function markError(line, column, message) {
    if (line == null) { textarea.removeAttribute('title'); textarea.classList.remove('has-error'); return; }
    textarea.classList.add('has-error');
    textarea.title = `${line}:${column}: ${message}`;
  }

  repaint();
  // repaintNow is awaitable and immediate; repaint coalesces onto a frame. A
  // caller that must SEE the result (a test) needs the former — under a
  // headless browser's virtual clock, frames do not necessarily come.
  return { repaint: scheduleRepaint, repaintNow: repaint, markError };
}
