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
    // Chunk 152 — ISO §6.4.2 character conversion. Non-null + non-empty
    // means a `:- char_conversion(In, Out)` directive (or runtime
    // builtin) has populated the map; the lexer maps the start-of-token
    // character (and identifier continuations) through it before
    // tokenizing. Quoted contexts bypass the conversion.
    private readonly IReadOnlyDictionary<char, char>? _charConversion;

    /// <summary>Phase 30 — Arity/Prolog32 compatibility (the
    /// <c>arity_compat</c> flag). Mutable so a
    /// <c>:- set_prolog_flag(arity_compat, true)</c> directive can flip
    /// it mid-stream (the ClauseReader writes it when it applies the
    /// directive). Enables <c>$...$</c> quoted atoms (escape a
    /// <c>$</c> by doubling; everything else, including <c>'</c> and
    /// backslash, is literal) and <c>#line N "file"</c> markers at the
    /// start of a line (consumed; the lexer adopts N as the next
    /// line's number so positions track the preprocessor's original
    /// source).</summary>
    public bool ArityCompat { get; set; }

    public Lexer(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Chunk 152 — constructs a lexer that honours the given
    /// character-conversion table (typically the one on
    /// <c>PrologFlags.CharConversion</c>). Pass <c>null</c> or an
    /// empty map to disable conversion.</summary>
    public Lexer(string source, IReadOnlyDictionary<char, char>? charConversion)
        : this(source)
    {
        if (charConversion is not null && charConversion.Count > 0)
            _charConversion = charConversion;
    }

    /// <summary>Chunk 152 — maps <paramref name="c"/> through the
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

        // Chunk 152: convert the start-of-token character before
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

    private SourcePosition CurrentPosition() => new(_line, _column, _offset);

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

    private void SkipWhitespaceAndComments()
    {
        while (_offset < _source.Length)
        {
            char c = _source[_offset];
            if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else if (c == '%')
            {
                while (_offset < _source.Length && _source[_offset] != '\n') Advance();
            }
            else if (c == '/' && Peek(1) == '*')
            {
                Advance(); Advance();
                while (_offset < _source.Length)
                {
                    if (_source[_offset] == '*' && Peek(1) == '/')
                    {
                        Advance(); Advance();
                        break;
                    }
                    Advance();
                }
            }
            else if (ArityCompat && c == '#' && _column == 1
                     && string.CompareOrdinal(_source, _offset, "#line", 0, 5) == 0)
            {
                // Phase 30 — C-preprocessor line marker: `#line N "file"`.
                // Consume the whole line; adopt N as the NEXT line's number
                // so token positions (and therefore parse-error positions)
                // track the preprocessor's original source rather than the
                // expanded .i file.
                int scan = _offset + 5;
                while (scan < _source.Length && _source[scan] == ' ') scan++;
                int numStart = scan;
                while (scan < _source.Length && char.IsDigit(_source[scan])) scan++;
                int lineNo = 0;
                bool haveNum = scan > numStart
                    && int.TryParse(_source.AsSpan(numStart, scan - numStart), out lineNo);
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
        while (_offset < _source.Length && IsSymbolChar(_source[_offset])) Advance();
        return new Token(TokenKind.Atom, pos, BuildText(start, _offset));
    }

    /// <summary>Chunk 152 — extracts the substring of <c>_source</c>
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

        // 0'<char>: character code literal.
        if (c == '0' && Peek(1) == '\'')
        {
            Advance(); Advance();
            int code = ReadCharCodeLiteral(pos);
            return new Token(TokenKind.Integer, pos, _source[start.._offset])
                { IntValue = code };
        }

        // 0x...: hexadecimal literal.
        if (c == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance(); Advance();
            int hexStart = _offset;
            while (_offset < _source.Length && IsHexDigit(_source[_offset])) Advance();
            if (_offset == hexStart)
                throw new LexerException(
                    $"Expected hex digits after 0x at {pos}.", pos);
            long hex = long.Parse(_source[hexStart.._offset], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Token(TokenKind.Integer, pos, _source[start.._offset])
                { IntValue = hex };
        }

        // Decimal integer part.
        while (_offset < _source.Length && char.IsDigit(_source[_offset])) Advance();

        // Float continuation: '.' followed by a digit.
        if (_offset < _source.Length
            && _source[_offset] == '.'
            && _offset + 1 < _source.Length
            && char.IsDigit(_source[_offset + 1]))
        {
            Advance();   // '.'
            while (_offset < _source.Length && char.IsDigit(_source[_offset])) Advance();

            if (_offset < _source.Length && (_source[_offset] == 'e' || _source[_offset] == 'E'))
            {
                Advance();
                if (_offset < _source.Length && (_source[_offset] == '+' || _source[_offset] == '-'))
                    Advance();
                int expStart = _offset;
                while (_offset < _source.Length && char.IsDigit(_source[_offset])) Advance();
                if (_offset == expStart)
                    throw new LexerException(
                        $"Expected exponent digits at {CurrentPosition()}.", CurrentPosition());
            }

            string floatSource = _source[start.._offset];
            double f = double.Parse(floatSource, CultureInfo.InvariantCulture);
            return new Token(TokenKind.Float, pos, floatSource) { FloatValue = f };
        }

        string intSource = _source[start.._offset];
        // Try the narrow path first (the overwhelming common case); fall back
        // to BigInteger only when the literal genuinely exceeds long range.
        if (long.TryParse(intSource, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
            return new Token(TokenKind.Integer, pos, intSource) { IntValue = i };
        var big = System.Numerics.BigInteger.Parse(intSource, CultureInfo.InvariantCulture);
        return new Token(TokenKind.Integer, pos, intSource) { BigValue = big, HasBigValue = true };
    }

    private int ReadCharCodeLiteral(SourcePosition pos)
    {
        if (_offset >= _source.Length)
            throw new LexerException($"Unterminated 0' literal at {pos}.", pos);
        char c = _source[_offset];
        if (c == '\\')
        {
            Advance();
            return ReadEscapeSequence(pos);
        }
        Advance();
        return c;
    }

    private int ReadEscapeSequence(SourcePosition pos)
    {
        if (_offset >= _source.Length)
            throw new LexerException($"Unterminated escape sequence at {pos}.", pos);
        char c = _source[_offset];
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
            '0' => 0,
            's' => 32,    // space — Quintus extension
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '`' => '`',
            _ => throw new LexerException(
                $"Unknown escape sequence '\\{c}' at {pos}.", pos),
        };
    }

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
                    return new Token(TokenKind.Atom, pos, sb.ToString());
                }
            }
            else if (c == '\\')
            {
                Advance();
                sb.Append((char)ReadEscapeSequence(pos));
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
    }

    /// <summary>Phase 30 — Arity <c>$...$</c> quoted atom. Mirrors
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
                    return new Token(TokenKind.Atom, pos, sb.ToString());
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
                sb.Append((char)ReadEscapeSequence(pos));
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }
    }
}
