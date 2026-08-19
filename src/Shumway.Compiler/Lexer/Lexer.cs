using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shumway.Compiler.Lexer;

/// <summary>
/// Tokenizer for ISO Prolog source. Consumes a <see cref="string"/> and yields a
/// stream of <see cref="Token"/>s ending with <see cref="TokenKind.Eof"/>. The lexer
/// is deliberately minimal: it classifies lexemes (atom vs variable vs number vs
/// punctuation) and decodes the literal payload, but does not attempt to disambiguate
/// operator precedence or to recognise prefix/infix/postfix forms — that's the
/// parser's job.
///
/// <para>Highlights of the surface accepted:</para>
/// <list type="bullet">
/// <item>Unquoted atoms (<c>foo_bar</c>, lowercase + alnum/underscore tail).</item>
/// <item>Quoted atoms (<c>'hello world'</c>) with C-style escape sequences and
///   doubled-quote escaping (<c>'don''t'</c>).</item>
/// <item>Symbolic atoms — maximal runs of graphic chars (<c>:-</c>, <c>=..</c>,
///   <c>-&gt;</c>, etc.).</item>
/// <item>The reserved single-character atoms <c>!</c> (cut) and <c>;</c>
///   (disjunction).</item>
/// <item>Named variables (<c>X</c>, <c>_var</c>) and the anonymous variable
///   <c>_</c>.</item>
/// <item>Integers in decimal, hexadecimal (<c>0xff</c>) and character-code form
///   (<c>0'a</c>, with escape sequences).</item>
/// <item>Floats with optional exponent (<c>1.5e10</c>).</item>
/// <item>Double-quoted strings (<c>"hello"</c>) with the same escape sequences as
///   quoted atoms.</item>
/// <item>Punctuation: <c>( ) [ ] { } , | .</c></item>
/// <item>Line comments (<c>% …</c>) and block comments (<c>/* … */</c>).</item>
/// </list>
///
/// <para>The clause-terminating <c>.</c> is detected by lookahead: a period followed
/// by whitespace, end-of-input or the start of a comment is <see cref="TokenKind.Dot"/>;
/// anything else makes it part of a symbolic atom (or, after a digit, of a float).</para>
/// </summary>
public sealed class Lexer
{
    private readonly string _source;
    private int _offset;
    private int _line = 1;
    private int _column = 1;
    // ISO §6.4.2 character conversion. Non-null + non-empty
    // means a `:- char_conversion(In, Out)` directive (or runtime
    // builtin) has populated the map; the lexer maps the start-of-token
    // character (and identifier continuations) through it before
    // tokenizing. Quoted contexts bypass the conversion.
    private readonly IReadOnlyDictionary<char, char>? _charConversion;

    /// <summary>Arity/Prolog32 compatibility (the
    /// <c>arity_compat</c> flag). Mutable so a
    /// <c>:- set_prolog_flag(arity_compat, true)</c> directive can flip
    /// it mid-stream (the ClauseReader writes it when it applies the
    /// directive). Enables <c>$...$</c> quoted atoms (escape a
    /// <c>$</c> by doubling; everything else, including <c>'</c> and
    /// backslash, is literal), <c>#line N "file"</c> markers at the
    /// start of a line (consumed; the lexer adopts N as the next
    /// line's number so positions track the preprocessor's original
    /// source), backquote char-code literals (<c>`x</c>,
    /// same semantics as <c>0'x</c> including escapes), and literal
    /// backslash inside <c>'...'</c> quoted atoms (Arity
    /// has no backslash escapes there; <c>''</c> doubling still
    /// applies), and <c>$</c> terminating symbol-atom runs
    /// (<c>X=$texto$</c> is <c>=</c> + the atom <c>texto</c>, not the
    /// atom <c>=$</c>).</summary>
    public bool ArityCompat { get; set; }

    /// <summary>SWI digit-group separators (<c>10_000</c>): accept <c>_</c>
    /// inside a number when surrounded by digits, in decimal / float / radix
    /// literals. Off by default — ISO tokenizes <c>10_000</c> as the integer
    /// <c>10</c> followed by the variable <c>_000</c> — and enabled by the
    /// swi dialect load scope (library sources use it; <c>10_000</c> in a term
    /// context is a syntax error under ISO, so this only accepts programs ISO
    /// rejects).</summary>
    public bool DigitSeparators { get; set; }

    /// <summary>SWI leniency: <c>0''</c> not followed by a third quote is the
    /// quote character (ISO requires <c>0'''</c> or <c>0'\'</c>). Enabled by
    /// the swi dialect load scope.</summary>
    public bool LenientQuoteCharLiteral { get; set; }

    /// <summary>SWI-extension escapes in quoted tokens (<c>\e \s \c \uXXXX
    /// \UXXXXXXXX</c>): accepted only under the swi dialect scope. Strict ISO
    /// rejects them as unknown escapes — the conformance suite checks it.
    /// See <c>PrologFlags.LenientEscapes</c>.</summary>
    public bool LenientEscapes { get; set; }

    public Lexer(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Constructs a lexer that honours the given
    /// character-conversion table (typically the one on
    /// <c>PrologFlags.CharConversion</c>). Pass <c>null</c> or an
    /// empty map to disable conversion.</summary>
    public Lexer(string source, IReadOnlyDictionary<char, char>? charConversion)
        : this(source)
    {
        if (charConversion is not null && charConversion.Count > 0)
            _charConversion = charConversion;
    }

    /// <summary>Maps <paramref name="c"/> through the
    /// char-conversion table when one is active and contains an
    /// entry; otherwise returns <paramref name="c"/> unchanged. Hot
    /// path: the common case is a null table, branch-predicted away.
    /// </summary>
    private char Convert(char c)
    {
        if (_charConversion is null) return c;
        return _charConversion.TryGetValue(c, out char r) ? r : c;
    }

    /// <summary>Reads and returns the next token, advancing the lexer. After EOF has
    /// been returned the lexer will keep returning EOF on subsequent calls — useful
    /// for parser lookahead.</summary>
    public Token NextToken()
    {
        int beforeWs = _offset;
        SkipWhitespaceAndComments();
        bool hadWs = _offset > beforeWs;
        Token tok = NextTokenInner();
        return hadWs ? tok with { HasLeadingWhitespace = true } : tok;
    }

    private Token NextTokenInner()
    {
        if (_offset >= _source.Length)
            return new Token(TokenKind.Eof, CurrentPosition(), "");

        // Convert the start-of-token character before
        // dispatch. Quoted contexts (' " 0') retain the raw char and
        // skip the conversion explicitly.
        char raw = _source[_offset];
        char c = (raw == '\'' || raw == '"') ? raw : Convert(raw);
        SourcePosition pos = CurrentPosition();

        if (char.IsDigit(c)) return ParseNumber(pos);
        if (c == '_' || (c >= 'A' && c <= 'Z')) return ParseVariable(pos);
        if (c >= 'a' && c <= 'z') return ParseUnquotedAtom(pos);
        if (raw == '\'') return ParseQuotedAtom(pos);
        if (raw == '"') return ParseString(pos);
        if (raw == '$' && ArityCompat) return ParseDollarAtom(pos);
        if (raw == '`' && ArityCompat) return ParseBackquoteCharLiteral(pos);

        switch (c)
        {
            case '(': Advance(); return new Token(TokenKind.LParen, pos, "(");
            case ')': Advance(); return new Token(TokenKind.RParen, pos, ")");
            case '[': Advance(); return new Token(TokenKind.LBracket, pos, "[");
            case ']': Advance(); return new Token(TokenKind.RBracket, pos, "]");
            case '{': Advance(); return new Token(TokenKind.LBrace, pos, "{");
            case '}': Advance(); return new Token(TokenKind.RBrace, pos, "}");
            case ',': Advance(); return new Token(TokenKind.Comma, pos, ",");
            case '|': Advance(); return new Token(TokenKind.Bar, pos, "|");
            case '!': Advance(); return new Token(TokenKind.Atom, pos, "!");
            case ';': Advance(); return new Token(TokenKind.Atom, pos, ";");
        }

        if (c == '.')
        {
            char next = Peek(1);
            if (next == '\0' || char.IsWhiteSpace(next) || next == '%')
            {
                Advance();
                return new Token(TokenKind.Dot, pos, ".");
            }
            // Otherwise fall through into the symbol-atom parser; '.' is a graphic
            // char that can be part of longer atoms (e.g. =..).
        }

        if (IsSymbolChar(c)) return ParseSymbolAtom(pos);

        throw new LexerException(
            $"Unexpected character '{c}' (U+{(int)c:X4}) at {pos}.",
            pos);
    }

    /// <summary>Error-recovery escape hatch. When
    /// <see cref="NextToken"/> throws a <see cref="LexerException"/>
    /// (e.g. a character the tokenizer has no lexeme for, like Arity's
    /// backquote char literals), the resync loop calls this to step
    /// past the offending character so scanning can make progress —
    /// otherwise the same character would throw again forever. No-op
    /// at end of input (the unterminated-quote exceptions leave the
    /// cursor there).</summary>
    public void SkipInvalidCharacter()
    {
        if (_offset < _source.Length) Advance();
    }

    /// <summary>Arity <c>:- c.</c> native-code
    /// sections. Called by the ClauseReader right after it consumed a
    /// <c>:- c.</c> directive (arity_compat only): the text that
    /// follows is C source, not Prolog, so it must be skipped RAW —
    /// it would otherwise hit the tokenizer. Scans physical lines for
    /// one whose start (after optional blanks) is the directive
    /// <c>:- prolog.</c> (blanks allowed between <c>:-</c> and
    /// <c>prolog</c> and before the <c>.</c>), consumes through that
    /// directive's dot, and returns — normal clause reading resumes
    /// after it. EOF inside the section ends the source normally.
    /// Line/column tracking is maintained (every character goes
    /// through <see cref="Advance"/>); <c>#line</c> markers inside the
    /// C text are deliberately NOT interpreted — positions continue
    /// the current numbering, which stays monotonic and sane.</summary>
    public string SkipNativeCodeSection()
    {
        // ADR-022 — return the RAW C declaration text of the
        // region (everything between `:- c.` and the terminating `:- prolog.`,
        // including C on the `:- c.` line itself), so a later stage can hand it
        // to the C-subset parser instead of discarding it. The `:- prolog.`
        // line is excluded from the returned span.
        int start = _offset;
        // Skip the remainder of the line carrying the `:- c.` itself —
        // Arity allows C code on the same line after the dot.
        while (_offset < _source.Length && _source[_offset] != '\n') Advance();
        while (_offset < _source.Length)
        {
            Advance();   // consume the newline; cursor is at a line start
            int lineStart = _offset;
            if (TryConsumePrologDirective())
                return _source.Substring(start, lineStart - start);
            while (_offset < _source.Length && _source[_offset] != '\n') Advance();
        }
        // EOF inside the section — the C text runs to end of source.
        return _source.Substring(start, _offset - start);
    }

    /// <summary>Arity embedded native goals
    /// (arity_compat only). In Arity a body goal can be raw native code
    /// between braces: <c>p :- goal, { C statements; }, otra.</c> The
    /// parser calls this immediately after it consumed the opening
    /// <c>{</c> token (with no further token prefetched — see the
    /// lookahead invariant documented at the call site), and the brace
    /// content is skipped RAW: it is C, not Prolog, so it must never
    /// reach the tokenizer. Skipping uses naive brace counting so
    /// nested native blocks (<c>{ if (x) { y(); } }</c>) balance;
    /// braces inside C string literals or comments are NOT understood
    /// and could unbalance the count — acceptable for now, the corpus
    /// doesn't exhibit it. Stops with the cursor just past the matching
    /// <c>}</c>. EOF before balance throws (an error diagnostic in the
    /// collecting reader, never a crash). Line/column tracking is
    /// maintained (every character goes through <see cref="Advance"/>).</summary>
    public string SkipNativeGoalBlock(SourcePosition openBracePos)
    {
        // ADR-022 — return the RAW C statement text BETWEEN the
        // braces (the parser already consumed the opening `{`; the closing `}` is
        // excluded), so a later stage can hand it to the C-subset parser instead
        // of substituting a no-op.
        int start = _offset;
        int depth = 1;
        while (_offset < _source.Length)
        {
            char c = _source[_offset];
            Advance();
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return _source.Substring(start, _offset - 1 - start);   // exclude closing }
        }
        throw new LexerException(
            $"Unterminated native code goal '{{' starting at {openBracePos}.",
            openBracePos);
    }

    /// <summary>Matches the <c>:- prolog.</c> end-of-C-section shape at
    /// the current cursor (a line start): optional blanks, <c>:-</c>,
    /// optional blanks, <c>prolog</c>, optional blanks, <c>.</c>, then
    /// end of token (whitespace / EOF / a <c>%</c> comment). On a match
    /// the cursor advances past the dot and the method returns true;
    /// otherwise the cursor is untouched.</summary>
    private bool TryConsumePrologDirective()
    {
        int scan = _offset;
        while (scan < _source.Length && (_source[scan] == ' ' || _source[scan] == '\t')) scan++;
        if (scan + 1 >= _source.Length || _source[scan] != ':' || _source[scan + 1] != '-')
            return false;
        scan += 2;
        while (scan < _source.Length && (_source[scan] == ' ' || _source[scan] == '\t')) scan++;
        if (scan + 6 > _source.Length
            || string.CompareOrdinal(_source, scan, "prolog", 0, 6) != 0)
            return false;
        scan += 6;
        while (scan < _source.Length && (_source[scan] == ' ' || _source[scan] == '\t')) scan++;
        if (scan >= _source.Length || _source[scan] != '.') return false;
        scan++;
        // The dot must terminate the clause (mirrors the tokenizer's
        // end-dot rule): EOF, whitespace, or a % comment.
        if (scan < _source.Length && !char.IsWhiteSpace(_source[scan]) && _source[scan] != '%')
            return false;
        while (_offset < scan) Advance();
        return true;
    }

    /// <summary>Convenience wrapper that yields all tokens up to and including the
    /// final <see cref="TokenKind.Eof"/>.</summary>
    public IEnumerable<Token> Tokenize()
    {
        while (true)
        {
            Token t = NextToken();
            yield return t;
            if (t.Kind == TokenKind.Eof) yield break;
        }
    }

    // ---------- Position / advance helpers ----------

    /// <summary>ADR-035 — the file this source came from (a
    /// <c>Shumway.Core.DebugSiteTable</c> id; 0 = unknown). Every position the lexer makes
    /// carries it, so every term, every clause and every transform's rebuilt copy knows
    /// which file it is from without anyone having to remember.</summary>
    public int FileId { get; set; }

    private SourcePosition CurrentPosition() => new(_line, _column, _offset, FileId);

    private char Peek(int ahead = 0)
    {
        int idx = _offset + ahead;
        return idx >= 0 && idx < _source.Length ? _source[idx] : '\0';
    }

    private void Advance()
    {
        if (_offset >= _source.Length) return;
        if (_source[_offset] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _offset++;
    }

    /// <summary>True when the current offset is the first non-blank character of
    /// its line — only spaces / tabs (or nothing) precede it back to the previous
    /// newline. A C-preprocessor <c>#line</c> marker is recognized at the line start
    /// regardless of leading indentation (the generated .i files indent them).</summary>
    private bool AtLineStart()
    {
        int i = _offset - 1;
        while (i >= 0 && (_source[i] == ' ' || _source[i] == '\t')) i--;
        return i < 0 || _source[i] == '\n';
    }

    private void SkipWhitespaceAndComments()
    {
        while (_offset < _source.Length)
        {
            char c = _source[_offset];
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == '﻿')
            {
                // A byte-order mark. Not whitespace as far as char.IsWhiteSpace is
                // concerned, and not anything else either — so it used to be a syntax error
                // on the FIRST character of the file, which is the only place it appears:
                // a .pl saved as UTF-8-with-BOM (the default in more than one Windows
                // editor) would not consult, and a goal piped in from a shell that writes
                // one would not parse. Skipped, like the nothing it is.
                Advance();
            }
            else if (c == '%')
            {
                while (_offset < _source.Length && _source[_offset] != '\n') Advance();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                var openPos = CurrentPosition();
                Advance(); Advance();
                bool closed = false;
                while (_offset < _source.Length)
                {
                    if (_source[_offset] == '*' && Peek(1) == '/')
                    {
                        Advance(); Advance();
                        closed = true;
                        break;
                    }
                    Advance();
                }
                // §6.4.1: a block comment must be closed — running off the end
                // of input is a syntax error, not an implicit close.
                if (!closed)
                    throw new LexerException(
                        $"Unterminated block comment opened at {openPos}.", openPos);
            }
            else if (ArityCompat && c == '#' && AtLineStart()
                     && string.CompareOrdinal(_source, _offset, "#line", 0, 5) == 0)
            {
                // C-preprocessor line marker: `#line N "file"`.
                // Consume the whole line; adopt N as the NEXT line's number
                // so token positions (and therefore parse-error positions)
                // track the preprocessor's original source rather than the
                // expanded .i file.
                int scan = _offset + 5;
                while (scan < _source.Length && _source[scan] == ' ') scan++;
                int numStart = scan;
                while (scan < _source.Length && char.IsDigit(_source[scan])) scan++;
                int lineNo = 0;
                // Substring rather than AsSpan: net48 has no span TryParse, and
                // this is the `#line` directive path — parsed once per directive,
                // not per token, so the allocation is noise.
                bool haveNum = scan > numStart
                    && int.TryParse(_source.Substring(numStart, scan - numStart), out lineNo);
                while (_offset < _source.Length && _source[_offset] != '\n') Advance();
                if (_offset < _source.Length) Advance();   // the newline itself
                if (haveNum) _line = lineNo;
            }
            else
            {
                break;
            }
        }
    }

    private static bool IsSymbolChar(char c) => c switch
    {
        '+' or '-' or '*' or '/' or '\\' or '^' or '<' or '>' or '=' or '~' or
        ':' or '?' or '@' or '#' or '&' or '$' or '.' => true,
        _ => false,
    };

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Digit value for radix literals (0x / 0o / 0b): 0-15 for a
    /// valid hex digit, -1 otherwise. Callers bound the accepted range by
    /// their radix.</summary>
    private static int RadixDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ---------- Atom / variable parsers ----------

    private Token ParseUnquotedAtom(SourcePosition pos)
    {
        int start = _offset;
        while (_offset < _source.Length)
        {
            char c = _source[_offset];
            if (char.IsLetterOrDigit(c) || c == '_') Advance();
            else break;
        }
        return new Token(TokenKind.Atom, pos, BuildText(start, _offset));
    }

    private Token ParseVariable(SourcePosition pos)
    {
        int start = _offset;
        while (_offset < _source.Length)
        {
            char c = _source[_offset];
            if (char.IsLetterOrDigit(c) || c == '_') Advance();
            else break;
        }
        return new Token(TokenKind.Variable, pos, BuildText(start, _offset));
    }

    private Token ParseSymbolAtom(SourcePosition pos)
    {
        int start = _offset;
        // arity_compat: `$` terminates a symbol-atom
        // run instead of joining it, so `X=$texto$` lexes as `=` followed
        // by the $-quoted atom `texto` rather than the maximal-munch atom
        // `=$`. A LEADING `$` never reaches here under the flag — the
        // dispatcher routes it to ParseDollarAtom — so this only affects
        // `$` appearing mid-run. None of Shumway's own vocabulary forms a
        // symbolic atom containing `$`: internal names like '$call' are
        // $-LEADING and always written quoted (TermRenderer quotes any
        // atom mixing graphic and alphanumeric chars), and no operator
        // contains `$`. Flag off: ISO maximal munch unchanged (`=$` is
        // one atom).
        while (_offset < _source.Length && IsSymbolChar(_source[_offset])
               && !(ArityCompat && _source[_offset] == '$'))
            Advance();
        return new Token(TokenKind.Atom, pos, BuildText(start, _offset));
    }

    /// <summary>Extracts the substring of <c>_source</c>
    /// between <paramref name="start"/> and <paramref name="end"/>,
    /// applying char conversion when active. Fast-paths the slice
    /// when no conversion is active.</summary>
    private string BuildText(int start, int end)
    {
        if (_charConversion is null) return _source[start..end];
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++) sb.Append(Convert(_source[i]));
        return sb.ToString();
    }

    // ---------- Number parser ----------

    private Token ParseNumber(SourcePosition pos)
    {
        int start = _offset;
        char c = _source[_offset];

        // 0'<char>: character code literal. When what follows `0'` cannot
        // complete one (a lone quote, a line-continuation escape), the
        // longest-VALID token is the integer `0` and the quote starts a fresh
        // token — `0''` is `0` + `''` (Neumerkel #120/#197), `0'\<NL>+'` is
        // `0` + the atom `'+'` (#213/#259).
        if (c == '0' && Peek(1) == '\'')
        {
            int markOffset = _offset, markLine = _line, markColumn = _column;
            Advance(); Advance();
            int code = ReadCharCodeLiteral(pos);
            if (code == NotACharCode)
            {
                _offset = markOffset; _line = markLine; _column = markColumn;
                Advance();   // just the '0'
                return new Token(TokenKind.Integer, pos, "0") { IntValue = 0 };
            }
            return new Token(TokenKind.Integer, pos, _source[start.._offset])
                { IntValue = code };
        }

        // 0x / 0o / 0b: radix literals (ISO 6.4.4). Accumulate through
        // BigInteger — `long.Parse(NumberStyles.HexNumber)` interpreted 16
        // F's as two's-complement (-1: the Logtalk random library's
        // mask64(0xFFFFFFFFFFFFFFFF) became -1, so `/\ Mask` stopped
        // masking and the 64-bit generators returned floats like 3e114)
        // and threw OverflowException past 16 digits. A value that fits
        // long stays a plain Integer token; larger values carry BigValue.
        if (c == '0')
        {
            // ISO §6.4.4: radix markers are lowercase only. `0X` / `0B` / `0O`
            // are NOT radix literals (0 followed by an uppercase token).
            int radix = Peek(1) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0,
            };
            // Require a valid digit after the marker; otherwise fall through
            // and lex the '0' as a plain decimal zero (the 'x'/'o'/'b' becomes
            // a separate token) — ISO greedy-longest-VALID: `0xor 2` is
            // `xor(0, 2)` under `op(9, yfx, xor)` (Neumerkel #255), not an
            // error.
            if (radix != 0)
            {
                int d0 = RadixDigitValue(Peek(2));
                if (d0 >= 0 && d0 < radix)
                {
                    Advance(); Advance();
                    System.Numerics.BigInteger acc = 0;
                    while (_offset < _source.Length)
                    {
                        char rc = _source[_offset];
                        if (rc == '_' && DigitSeparators
                            && IsRadixDigit(Peek(1), radix)
                            && IsRadixDigit(_source[_offset - 1], radix))
                        {
                            Advance();
                            continue;
                        }
                        int d = RadixDigitValue(rc);
                        if (d < 0 || d >= radix) break;
                        acc = acc * radix + d;
                        Advance();
                    }
                    string text = _source[start.._offset];
                    if (acc <= long.MaxValue)
                        return new Token(TokenKind.Integer, pos, text)
                            { IntValue = (long)acc };
                    return new Token(TokenKind.Integer, pos, text)
                        { BigValue = acc, HasBigValue = true };
                }
            }
        }

        // Decimal integer part.
        ScanDecimalDigits();

        // Float continuation: '.' followed by a digit.
        if (_offset < _source.Length
            && _source[_offset] == '.'
            && _offset + 1 < _source.Length
            && char.IsDigit(_source[_offset + 1]))
        {
            Advance();   // '.'
            ScanDecimalDigits();

            if (_offset < _source.Length && (_source[_offset] == 'e' || _source[_offset] == 'E'))
            {
                int expMark = _offset;
                Advance();
                if (_offset < _source.Length && (_source[_offset] == '+' || _source[_offset] == '-'))
                    Advance();
                int expStart = _offset;
                ScanDecimalDigits();
                if (_offset == expStart)
                {
                    // ISO greedy-longest-VALID token: `1.0e` / `1.0e-` are the
                    // float `1.0` followed by the atom `e` (Neumerkel #51/#52/
                    // #220 lean on `op(9, xf, e)`), not a lexing error. Rewind
                    // over the consumed 'e'/sign (never newlines).
                    _column -= _offset - expMark;
                    _offset = expMark;
                }
            }

            string floatSource = StripSeparators(_source[start.._offset]);
            double f = double.Parse(floatSource, CultureInfo.InvariantCulture);
            return new Token(TokenKind.Float, pos, floatSource) { FloatValue = f };
        }

        string intSource = StripSeparators(_source[start.._offset]);
        // Try the narrow path first (the overwhelming common case); fall back
        // to BigInteger only when the literal genuinely exceeds long range.
        if (long.TryParse(intSource, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
            return new Token(TokenKind.Integer, pos, intSource) { IntValue = i };
        var big = System.Numerics.BigInteger.Parse(intSource, CultureInfo.InvariantCulture);
        return new Token(TokenKind.Integer, pos, intSource) { BigValue = big, HasBigValue = true };
    }

    /// <summary>Advances over decimal digits; with <see cref="DigitSeparators"/>
    /// also over a <c>_</c> that sits strictly between two digits.</summary>
    private void ScanDecimalDigits()
    {
        while (_offset < _source.Length)
        {
            char ch = _source[_offset];
            if (char.IsDigit(ch)) { Advance(); continue; }
            if (ch == '_' && DigitSeparators
                && char.IsDigit(_source[_offset - 1])
                && _offset + 1 < _source.Length && char.IsDigit(_source[_offset + 1]))
            {
                Advance();
                continue;
            }
            break;
        }
    }

    private string StripSeparators(string text) =>
        DigitSeparators && text.Contains('_') ? text.Replace("_", "") : text;

    private static bool IsRadixDigit(char c, int radix)
    {
        int d = RadixDigitValue(c);
        return d >= 0 && d < radix;
    }

    /// <summary>Arity backquote char-code
    /// literal (<c>`x</c>), arity_compat only. Arity writes character
    /// codes as a backquote followed by one character; the corpus uses
    /// them in list and argument positions (<c>[_, `x|_]</c>). Tokenizes
    /// to the same INTEGER token the ISO <c>0'x</c> form produces — but
    /// unlike <c>0'</c>, Arity does NOT process escape sequences after
    /// the backquote (consistent with the
    /// literal-backslash rule for <c>'...'</c> under the flag): the NEXT
    /// character is taken literally, whatever it is — <c>`\</c> is 92,
    /// <c>`)</c> is 41, <c>`'</c> is 39, a backquote followed by a
    /// space is 32. A backquote at end of input or immediately followed
    /// by a line break is an error diagnostic (a code-of-newline is not
    /// a shape the corpus writes; far more likely a stray backquote).
    /// Without the flag the backquote stays an unlexable character
    /// (recovered as a diagnostic).</summary>
    private Token ParseBackquoteCharLiteral(SourcePosition pos)
    {
        int start = _offset;
        Advance();   // the backquote
        if (_offset >= _source.Length)
            throw new LexerException(
                $"Unterminated ` character-code literal at {pos}.", pos);
        char c = _source[_offset];
        if (c == '\n' || c == '\r')
            throw new LexerException(
                $"` character-code literal followed by a line break at {pos}.", pos);
        Advance();
        return new Token(TokenKind.Integer, pos, _source[start.._offset])
            { IntValue = c };
    }

    private int ReadCharCodeLiteral(SourcePosition pos)
    {
        if (_offset >= _source.Length)
            throw new LexerException($"Unterminated 0' literal at {pos}.", pos);
        char c = _source[_offset];
        if (c == '\\')
        {
            Advance();
            int code = ReadEscapeSequence(pos);
            if (code == EscapeContinuation)
                // A continuation stands for NO character, so `0'\<NL>` is not
                // a char literal — fall back to the integer 0 (the caller
                // rewinds; `0'\<NL>+'` reads as `0` + the atom '+').
                return NotACharCode;
            return code;
        }
        if (c == '\'')
        {
            // ISO 6.3.7: a quote inside a 0' character-code constant must be
            // written doubled (0''') or escaped (0'\'). A lone quote is not a
            // valid single-quoted character — `0''` is `0` followed by the
            // empty atom `''` (the caller rewinds).
            if (Peek(1) == '\'') { Advance(); Advance(); return '\''; }
            // SWI leniency (swi dialect loads only): `0''` NOT followed by a
            // third quote is the quote character itself — url.pl's
            // `sub_delim(0'').` — where ISO requires 0''' or 0'\'.
            if (LenientQuoteCharLiteral) { Advance(); return '\''; }
            return NotACharCode;
        }
        if ((c < ' ' || c == '\x7f') && !ArityCompat)
        {
            // ISO 6.3.7: only space (0' ) is a valid layout char after 0';
            // a raw tab / newline / other control char must be escaped.
            throw new LexerException(
                $"Raw control character (0x{(int)c:x2}) after 0' at {pos} "
                + "— use an escape sequence.", pos);
        }
        Advance();
        return c;
    }

    /// <summary>The sentinel <see cref="ReadEscapeSequence"/> returns for a
    /// line-continuation escape (<c>\</c> immediately before a newline): it
    /// stands for NO character. Callers building a string skip it.</summary>
    internal const int EscapeContinuation = -1;

    /// <summary>The sentinel <see cref="ReadCharCodeLiteral"/> returns when
    /// what follows <c>0'</c> cannot complete a character-code literal; the
    /// caller rewinds and lexes the plain integer <c>0</c> instead.</summary>
    internal const int NotACharCode = -2;

    private int ReadEscapeSequence(SourcePosition pos)
    {
        if (_offset >= _source.Length)
            throw new LexerException($"Unterminated escape sequence at {pos}.", pos);
        char c = _source[_offset];

        // ISO 6.4.2.1 line continuation: a backslash immediately followed by a
        // newline is elided (yields no character). Handles LF, CR, and CRLF.
        if (c == '\n' || c == '\r')
        {
            Advance();
            if (c == '\r' && _offset < _source.Length && _source[_offset] == '\n')
                Advance();
            return EscapeContinuation;
        }

        // SWI `\c` — line continuation that removes ALL following layout (spaces,
        // tabs, newlines) up to the next non-layout character. Not ISO — strict
        // reading rejects it as an unknown escape (the conformance suite
        // checks) — so it is gated on the swi dialect scope, which is where
        // the `\c`-joined library strings live (ADR-040 SWI triage).
        if (c == 'c' && LenientEscapes)
        {
            Advance();   // consume 'c'
            while (_offset < _source.Length
                   && _source[_offset] is ' ' or '\t' or '\n' or '\r')
                Advance();
            return EscapeContinuation;
        }

        // Hex escape (ISO / SWI / Scryer): `\x` followed by one or more hex
        // digits and a terminating backslash, e.g. `\x1b\`. The terminator is
        // mandatory — it disambiguates `"\x1b\["` (ESC then `[`).
        if (c == 'x')
        {
            Advance();   // consume 'x'
            return ReadNumericEscape(pos, radix: 16, name: "hexadecimal");
        }
        // \uXXXX (4 hex) / \UXXXXXXXX (8 hex) — a Unicode code point, SWI/Java
        // style: a FIXED-width hex escape with NO terminating backslash. Not
        // ISO — strict reading rejects it (the conformance suite checks) — so
        // it is gated on the swi dialect scope, where the `\u`-using library
        // sources live (ADR-040).
        if (c == 'u' && LenientEscapes) { Advance(); return ReadFixedHexEscape(pos, 4); }
        if (c == 'U' && LenientEscapes) { Advance(); return ReadFixedHexEscape(pos, 8); }
        // Octal escape (ISO): `\` followed by octal digits and a terminating
        // backslash, e.g. `\33\` or `\0\`. There is NO bare-`\0` NUL
        // shorthand — ISO requires the terminator (Neumerkel #300/#301
        // `'\0\'`, #18 `'\33\'`).
        if (c >= '0' && c <= '7')
            return ReadNumericEscape(pos, radix: 8, name: "octal");

        Advance();
        return c switch
        {
            'a' => 7,
            'b' => 8,
            'f' => 12,
            'n' => 10,
            'r' => 13,
            't' => 9,
            'v' => 11,
            // ESC / space — SWI/GNU/Quintus extensions, not ISO: strict
            // reading rejects them (the conformance suite checks); the swi
            // dialect scope accepts them.
            'e' when LenientEscapes => 27,
            's' when LenientEscapes => 32,
            'd' when LenientEscapes => 127,
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '`' => '`',
            _ => throw new LexerException(
                $"Unknown escape sequence '\\{c}' at {pos}.", pos),
        };
    }

    /// <summary>Reads the digits of a numeric character escape (hex after
    /// <c>\x</c>, or octal after <c>\</c>) and its mandatory terminating
    /// backslash. The offset is positioned at the first digit on entry.</summary>
    // Reads exactly <paramref name="digits"/> hex digits (no terminator) — the
    // \uXXXX / \UXXXXXXXX Unicode escapes.
    private int ReadFixedHexEscape(SourcePosition pos, int digits)
    {
        long value = 0;
        for (int i = 0; i < digits; i++)
        {
            if (_offset >= _source.Length)
                throw new LexerException($"Unterminated \\u/\\U escape at {pos}.", pos);
            int d = DigitValue(_source[_offset]);
            if (d < 0 || d >= 16)
                throw new LexerException(
                    $"\\u/\\U escape needs {digits} hex digits at {pos}.", pos);
            value = value * 16 + d;
            Advance();
        }
        if (value > 0x10FFFF)
            throw new LexerException($"code point out of range at {pos}.", pos);
        return (int)value;
    }

    private int ReadNumericEscape(SourcePosition pos, int radix, string name)
    {
        int start = _offset;
        long value = 0;
        while (_offset < _source.Length)
        {
            int d = DigitValue(_source[_offset]);
            if (d < 0 || d >= radix) break;
            value = value * radix + d;
            if (value > 0x10FFFF)
                throw new LexerException(
                    $"{name} character escape out of range at {pos}.", pos);
            Advance();
        }
        if (_offset == start)
            throw new LexerException(
                $"Empty {name} character escape at {pos}.", pos);
        if (_offset >= _source.Length || _source[_offset] != '\\')
            throw new LexerException(
                $"{name} character escape must end with '\\' at {pos}.", pos);
        Advance();   // consume the terminating backslash
        return (int)value;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ---------- Quoted atom / string parsers ----------

    private Token ParseQuotedAtom(SourcePosition pos)
    {
        Advance();   // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_offset >= _source.Length)
                throw new LexerException(
                    $"Unterminated quoted atom starting at {pos}.", pos);
            char c = _source[_offset];
            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    sb.Append('\'');
                    Advance(); Advance();
                }
                else
                {
                    Advance();
                    return new Token(TokenKind.Atom, pos, sb.ToString())
                        { WasQuoted = true };
                }
            }
            else if (c == '\\' && !ArityCompat)
            {
                // Arity does NOT interpret backslash escapes
                // inside '...' quoted atoms — '\' is the one-character
                // backslash atom (Arity-era sources put Windows paths in
                // quoted atoms). Under arity_compat the backslash falls
                // through to the literal-character branch below; the
                // doubled-quote escape ('') above applies in both modes.
                Advance();
                int e = ReadEscapeSequence(pos);
                if (e != EscapeContinuation) sb.Append((char)e);
            }
            else if ((c < ' ' || c == '\x7f') && !ArityCompat)
            {
                // ISO 6.3.7: a raw control character (tab, newline, …) is not
                // a valid quoted-token char — it must be written as an escape
                // (\t, \n, …) or, for a newline, the \<newline> continuation.
                // Arity-era sources are exempt (they embed literal bytes).
                throw new LexerException(
                    $"Raw control character (0x{(int)c:x2}) in quoted atom "
                    + $"at {pos} — use an escape sequence.", pos);
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
    }

    /// <summary>Arity <c>$...$</c> quoted atom. Mirrors
    /// <see cref="ParseQuotedAtom"/> with the delimiter swapped: a
    /// <c>$</c> inside is escaped by doubling (<c>$$</c>), so the
    /// standalone token <c>$$</c> is the empty atom (like <c>''</c>).
    /// Unlike <c>'...'</c> there are NO backslash escapes — Arity-era
    /// sources put Windows paths inside <c>$...$</c>, so every
    /// non-delimiter character is literal.</summary>
    private Token ParseDollarAtom(SourcePosition pos)
    {
        Advance();   // opening $
        var sb = new StringBuilder();
        while (true)
        {
            if (_offset >= _source.Length)
                throw new LexerException(
                    $"Unterminated $-quoted atom starting at {pos}.", pos);
            char c = _source[_offset];
            if (c == '$')
            {
                if (Peek(1) == '$')
                {
                    sb.Append('$');
                    Advance(); Advance();
                }
                else
                {
                    Advance();
                    return new Token(TokenKind.Atom, pos, sb.ToString())
                        { WasQuoted = true };
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
    }

    private Token ParseString(SourcePosition pos)
    {
        Advance();   // opening "
        var sb = new StringBuilder();
        while (true)
        {
            if (_offset >= _source.Length)
                throw new LexerException(
                    $"Unterminated string starting at {pos}.", pos);
            char c = _source[_offset];
            if (c == '"')
            {
                if (Peek(1) == '"')
                {
                    sb.Append('"');
                    Advance(); Advance();
                }
                else
                {
                    Advance();
                    return new Token(TokenKind.String, pos, sb.ToString());
                }
            }
            else if (c == '\\')
            {
                Advance();
                int e = ReadEscapeSequence(pos);
                if (e != EscapeContinuation) sb.Append((char)e);
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
    }
}
