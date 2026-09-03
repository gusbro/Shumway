namespace Shumway.Embedding;

/// <summary>How a sentence scan ended: at its terminating solo dot, at end
/// of input mid-sentence, or at a character that PROVES no completion can
/// ever lex (see <see cref="SentenceScanner.ReadSentenceText"/>).</summary>
public enum SentenceEnd
{
    Complete,
    EndOfInput,
    Poisoned,
}

/// <summary>Consumes exactly ONE Prolog sentence — through its terminating
/// solo <c>.</c> — from a <see cref="System.IO.TextReader"/>, tracking just
/// enough lexical state to find the real end dot: quoted contexts
/// (<c>' " `</c>, with <c>''</c> doubling and <c>\</c> escapes), <c>%</c>
/// line comments, <c>/* */</c> block comments, <c>0'c</c> char literals
/// (with the lexer's fallbacks), and symbol runs (a <c>.</c> glued to a
/// preceding graphic char is part of that atom, not the terminator).
///
/// <para>Shared by <c>read/1</c>'s stream reader and the REPL's top level,
/// which must agree on where a sentence ends: the text a query leaves
/// unconsumed is the next query — or the next <c>read/1</c>'s input.</para></summary>
public static class SentenceScanner
{
    /// <summary>ISO graphic (symbol) chars — a run of these forms one symbol
    /// atom token, so a '.' preceded by one is part of that token
    /// (<c>=..</c>) and never ends the clause.</summary>
    private static bool IsGraphicChar(char c) => c is '#' or '$' or '&' or '*' or '+'
        or '-' or '.' or '/' or ':' or '<' or '=' or '>' or '?' or '@' or '^' or '~' or '\\';

    /// <summary>A raw control character, which no quoted token admits
    /// unescaped (the lexer's ISO 6.3.7 / 6.4.2.1 rule).</summary>
    private static bool IsRawControl(char c) => c < ' ' || c == '\x7f';

    /// <summary>True when <paramref name="c"/> glues onto a preceding token
    /// (letter, digit, underscore): a <c>0</c> after one is a token TAIL
    /// (<c>10</c>, <c>a0</c>, <c>_0</c>), never the standalone integer that
    /// opens a <c>0'c</c> char literal.</summary>
    private static bool IsTokenGlue(char c) => c == '_' || char.IsLetterOrDigit(c);

    /// <summary>Reads one sentence's source text, including its end dot.
    /// Returns <c>null</c> when only layout/comments remained before
    /// end-of-file (the <c>end_of_file</c> case). <paramref name="end"/> is
    /// <see cref="SentenceEnd.Complete"/> when a terminating end dot was
    /// found; <see cref="SentenceEnd.EndOfInput"/> when the reader ran out
    /// mid-sentence — the partial text is still returned so a parse can
    /// report the syntax error.
    ///
    /// <para><see cref="SentenceEnd.Poisoned"/>: a raw control character
    /// arrived inside a quoted token (or as a <c>0'</c> literal), which no
    /// completion can ever turn into a valid token — ISO §8.14.1.1 reads as
    /// if character by character, and the §8.14.1.3 error condition is
    /// already satisfied, so <c>read/1</c> on an interactive stream must
    /// raise NOW instead of prompting for more input that cannot help
    /// (conformity s#2: <c>'</c> + newline held the prompt hostage until the
    /// user donated a closing quote and a dot). The scan stops at the
    /// poisoning character; parsing the returned text reports the exact
    /// lexer error. The <c>\</c>-newline line continuation is the legal
    /// wait and stays one. <paramref name="allowRawControls"/> disables
    /// poisoning — Arity-era sources embed literal control bytes in quoted
    /// text, mirroring the lexer's <c>ArityCompat</c> exemption.</para></summary>
    public static string? ReadSentenceText(
        System.IO.TextReader reader, out SentenceEnd end,
        bool allowRawControls = false)
    {
        // NOT "stop at any '.' followed by whitespace" — that would slice
        // `?X =.. ?Y` in half at univ's second dot, and equally mis-split a
        // dot inside a quoted atom, a string, or a comment. The end-of-clause
        // token is a SOLO '.' followed by layout/EOF.
        end = SentenceEnd.EndOfInput;
        var sb = new System.Text.StringBuilder();
        char quote = '\0';            // inside 'x' / "x" / `x` when non-zero
        bool escNext = false;         // just saw '\' inside a quoted token
        bool escNumeric = false;      // inside \<octal>\ / \x<hex>\ (to its terminator)
        bool lineComment = false, blockComment = false;
        char prev = '\0';             // previous char in Normal state
        char prev2 = '\0';            // the char before prev (token-glue test)
        while (true)
        {
            int ci = reader.Read();
            if (ci < 0)
            {
                // §6.4.1: an UNTERMINATED block comment is a syntax error, so
                // hand the text to the parser (which reports it) rather than
                // treating it as layout.
                if (blockComment)
                    return sb.ToString();
                // end_of_file when nothing but layout/comments accumulated —
                // a trailing-whitespace file must yield end_of_file, not a
                // syntax error.
                if (IsLayoutOnly(sb))
                    return null;
                return sb.ToString();
            }
            char c = (char)ci;
            sb.Append(c);

            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (c == '/' && prev == '*') { blockComment = false; prev = '\0'; }
                else prev = c;
                continue;
            }
            if (quote != '\0')
            {
                if (escNumeric)
                {
                    // Inside \<octal>\ / \x<hex>\ the terminating backslash
                    // ENDS the escape — it does not escape what follows, so
                    // `"\0\"` closes at the quote after it (a plain
                    // \-escapes-next model absorbed that quote and the REPL
                    // consumed input forever).
                    if (c == '\\') escNumeric = false;
                    else if (IsRawControl(c) && !allowRawControls)
                    {
                        // A control char inside \x…\ is neither a digit nor
                        // the terminator: unlexable whatever follows.
                        end = SentenceEnd.Poisoned;
                        return sb.ToString();
                    }
                    continue;
                }
                if (escNext)
                {
                    escNext = false;
                    // An octal digit or 'x' starts a numeric escape running
                    // to its terminating backslash; anything else (mnemonic,
                    // \', \\, line continuation) is one escaped char. The
                    // \<newline> continuation is the LEGAL raw control here.
                    if ((c >= '0' && c <= '7') || c == 'x') escNumeric = true;
                    else if (IsRawControl(c) && c != '\n' && c != '\r'
                             && !allowRawControls)
                    {
                        end = SentenceEnd.Poisoned;
                        return sb.ToString();
                    }
                    continue;
                }
                if (c == '\\') { escNext = true; continue; }
                if (c == quote)
                {
                    // '' doubling: peek — a second quote continues the token.
                    if (reader.Peek() == quote) { sb.Append((char)reader.Read()); continue; }
                    quote = '\0';
                    prev2 = prev; prev = c;
                    continue;
                }
                if (IsRawControl(c) && !allowRawControls)
                {
                    end = SentenceEnd.Poisoned;
                    return sb.ToString();
                }
                continue;
            }

            switch (c)
            {
                case '%':
                    lineComment = true;
                    prev = '\0';
                    continue;
                case '*' when prev == '/':
                    blockComment = true;
                    prev = '\0';
                    continue;
                case '\'':
                    // 0'c char literal: consume the (possibly escaped) char
                    // raw — mirroring the lexer's fallbacks (Neumerkel
                    // #213/#259): 0'\<newline> is NOT a char literal (the
                    // quote opens a quoted token whose first content is a
                    // line continuation), and 0''' is the doubled-quote
                    // literal while 0'' + other closes an empty atom.
                    // ONLY a standalone 0 introduces one: `16'mod'2` and
                    // `00'+'1` are an integer followed by a QUOTED-ATOM
                    // operator (s#122/#127/#130/#280), so the quote there
                    // opens a quoted token, not a char literal.
                    if (prev == '0' && !IsTokenGlue(prev2))
                    {
                        int lit = reader.Read();
                        if (lit >= 0)
                        {
                            sb.Append((char)lit);
                            if ((char)lit == '\\')
                            {
                                int esc = reader.Read();   // escape body head
                                if (esc >= 0)
                                {
                                    sb.Append((char)esc);
                                    if ((char)esc == '\n' || (char)esc == '\r')
                                    {
                                        quote = '\'';
                                        prev = '\0';
                                        continue;
                                    }
                                }
                            }
                            else if ((char)lit == '\'')
                            {
                                if (reader.Peek() == '\'') sb.Append((char)reader.Read());
                                prev = '\0';
                                continue;
                            }
                            else if (IsRawControl((char)lit) && !allowRawControls)
                            {
                                // `0'` + raw newline/tab can never lex (only
                                // 0'<space> admits raw layout).
                                end = SentenceEnd.Poisoned;
                                return sb.ToString();
                            }
                        }
                        prev = '\0';
                        continue;
                    }
                    quote = '\'';
                    prev = '\0';
                    continue;
                case '"':
                case '`':
                    quote = c;
                    prev = '\0';
                    continue;
                case '.':
                    // Solo dot + following layout/EOF = end of clause.
                    if (!IsGraphicChar(prev))
                    {
                        int next = reader.Peek();
                        if (next < 0 || char.IsWhiteSpace((char)next) || next == '%')
                        {
                            end = SentenceEnd.Complete;
                            return sb.ToString();
                        }
                    }
                    prev2 = prev; prev = c;
                    continue;
                default:
                    prev2 = prev; prev = c;
                    continue;
            }
        }
    }

    /// <summary>True when the text holds no term — only whitespace, <c>%</c>
    /// line comments, and <c>/* */</c> block comments.</summary>
    private static bool IsLayoutOnly(System.Text.StringBuilder sb)
    {
        int i = 0, n = sb.Length;
        while (i < n)
        {
            char ch = sb[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '%')
            {
                while (i < n && sb[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < n && sb[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sb[i] == '*' && sb[i + 1] == '/')) i++;
                i = System.Math.Min(n, i + 2);
                continue;
            }
            return false;
        }
        return true;
    }
}
