// Prolog for the cell editor: a tokenizer for CodeMirror's stream parser.
// JupyterLab has no Prolog mode of its own.

import type { StreamParser, StringStream } from '@codemirror/language';

interface IState {
  /** Inside a block comment that a line left open. */
  comment: boolean;
  /** The quote of a quoted atom or a string that a line left open. */
  quote: string | null;
}

const SYMBOL = /[+\-*/\\^<>=~:.?@#&$]/;

/** Reads to the closing quote; a doubled quote and an escaped one stay in. */
function quoted(stream: StringStream, state: IState): string {
  const quote = state.quote!;
  while (!stream.eol()) {
    const c = stream.next();
    if (c === '\\') {
      stream.next();
    } else if (c === quote) {
      if (stream.peek() === quote) stream.next();
      else { state.quote = null; break; }
    }
  }
  return quote === "'" ? 'atom' : 'string';
}

export const prolog: StreamParser<IState> = {
  name: 'prolog',
  startState: () => ({ comment: false, quote: null }),

  token(stream, state) {
    if (state.comment) {
      if (stream.skipTo('*/')) { stream.match('*/'); state.comment = false; }
      else stream.skipToEnd();
      return 'comment';
    }
    if (state.quote) return quoted(stream, state);
    if (stream.eatSpace()) return null;

    // A cell magic names what the rest of the cell is, on its first line.
    if (stream.sol() && stream.match(/^%%[a-z]+\+?/)) return 'meta';
    if (stream.match('%')) { stream.skipToEnd(); return 'comment'; }
    if (stream.match('/*')) {
      state.comment = true;
      return this.token!(stream, state);
    }

    // 0'c is a character, and so is 0''' and 0'\n.
    if (stream.match(/^0'(\\.|''|.)/)) return 'number';
    if (stream.match(/^0[xob][0-9a-fA-F_]+/) || stream.match(/^\d[\d_]*(\.\d+)?([eE][+-]?\d+)?/)) return 'number';

    const c = stream.peek()!;
    if (c === '"' || c === "'" || c === '`') {
      stream.next();
      state.quote = c;
      return quoted(stream, state);
    }
    if (/[A-Z_]/.test(c)) { stream.match(/^[A-Za-z0-9_]+/); return 'variableName.special'; }
    if (/[a-z]/.test(c)) {
      const name = stream.match(/^[A-Za-z0-9_]+/) as RegExpMatchArray;
      return /^(is|mod|rem|div|xor|rdiv)$/.test(name[0]) ? 'operatorKeyword' : null;
    }
    if (stream.match(':-') || stream.match('-->') || stream.match('?-')) return 'keyword';
    if (c === '!') { stream.next(); return 'keyword'; }
    if (SYMBOL.test(c)) { stream.match(/^[+\-*/\\^<>=~:.?@#&$]+/); return 'operator'; }
    stream.next();
    return /[()[\]{}]/.test(c) ? 'bracket' : /[,;|]/.test(c) ? 'punctuation' : null;
  },

  languageData: {
    commentTokens: { line: '%', block: { open: '/*', close: '*/' } },
    closeBrackets: { brackets: ['(', '[', '{', "'", '"'] }
  }
};
