using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;

namespace Shumway.TopLevel;

/// <summary>What a highlighted span is.</summary>
public enum SpanKind
{
    /// <summary>Whitespace, or anything the highlighter has no opinion about.</summary>
    Plain,
    Comment,
    Variable,
    Atom,
    /// <summary>An atom the operator table currently knows as an operator.
    /// Which atoms these are depends on the program: a library that declares
    /// <c>:- op(700, xfx, #=)</c> makes <c>#=</c> one.</summary>
    Operator,
    Number,
    /// <summary>A quoted atom or a double-quoted string.</summary>
    Quoted,
    Punctuation,
    /// <summary>Text the lexer could not read. While someone is typing, an
    /// unterminated quote is the normal state of the buffer, not a failure.</summary>
    Error,
}

/// <summary>One run of source text that shares a <see cref="SpanKind"/>.</summary>
/// <param name="Start">Offset into the source, in UTF-16 code units.</param>
public readonly record struct HighlightSpan(int Start, int Length, SpanKind Kind);

/// <summary>
/// Syntax highlighting driven by the ENGINE'S OWN LEXER rather than a separate
/// pattern language. The two cannot drift: quoted atoms, <c>0'c</c> character
/// codes, block comments, the Arity <c>$…$</c> form, digit separators and
/// character conversion are read here exactly as the reader reads them. And
/// because operator-ness is a question for the live
/// <see cref="OperatorTable"/>, a program that declares its own operators gets
/// them coloured.
///
/// <para>The lexer skips whitespace and comments rather than emitting them, so
/// the gaps between tokens are recovered here: a gap can only be whitespace or
/// a comment, which makes classifying it a matter of looking at its first
/// characters — no change to the lexer required.</para>
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Spans covering <paramref name="source"/> completely and in order,
    /// so a renderer can emit them one after another and reproduce the text.
    /// <paramref name="operators"/> decides which atoms read as operators; pass
    /// the engine's table (<c>engine.Operators</c>) to match the program.</summary>
    public static IReadOnlyList<HighlightSpan> Highlight(string source, OperatorTable? operators = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var spans = new List<HighlightSpan>();
        if (source.Length == 0) return spans;

        int cursor = 0;   // everything before this is already covered

        void Cover(int upTo)
        {
            // The gap the lexer stepped over: whitespace and comments only.
            while (cursor < upTo)
            {
                int commentStart = FindComment(source, cursor, upTo);
                if (commentStart < 0)
                {
                    Add(spans, cursor, upTo - cursor, SpanKind.Plain);
                    cursor = upTo;
                    return;
                }
                Add(spans, cursor, commentStart - cursor, SpanKind.Plain);
                int end = CommentEnd(source, commentStart, upTo);
                Add(spans, commentStart, end - commentStart, SpanKind.Comment);
                cursor = end;
            }
        }

        try
        {
            var lexer = new Lexer(source);
            foreach (Token t in lexer.Tokenize())
            {
                if (t.Kind == TokenKind.Eof) break;
                int start = t.Position.Offset;
                if (start < cursor) continue;          // defensive: never go backwards
                Cover(start);
                int length = TokenLength(source, t, start);
                Add(spans, start, length, KindOf(t, operators));
                cursor = start + length;
            }
        }
        catch (LexerException ex)
        {
            // Half-typed input is the common case in an editor, not an error to
            // report: cover what was read, mark the rest, and let the next
            // keystroke re-run this.
            int at = Math.Clamp(ex.Position.Offset, cursor, source.Length);
            Cover(at);
            if (at < source.Length) Add(spans, at, source.Length - at, SpanKind.Error);
            cursor = source.Length;
        }
        catch (Exception)
        {
            if (cursor < source.Length)
                Add(spans, cursor, source.Length - cursor, SpanKind.Error);
            cursor = source.Length;
        }

        Cover(source.Length);
        return spans;
    }

    private static void Add(List<HighlightSpan> spans, int start, int length, SpanKind kind)
    {
        if (length <= 0) return;
        // Merge with the previous span when they agree, so the renderer emits
        // one element per run rather than one per token.
        if (spans.Count > 0)
        {
            var last = spans[^1];
            if (last.Kind == kind && last.Start + last.Length == start)
            {
                spans[^1] = last with { Length = last.Length + length };
                return;
            }
        }
        spans.Add(new HighlightSpan(start, length, kind));
    }

    private static SpanKind KindOf(Token t, OperatorTable? operators) => t.Kind switch
    {
        TokenKind.Variable => SpanKind.Variable,
        TokenKind.Integer or TokenKind.Float => SpanKind.Number,
        TokenKind.String => SpanKind.Quoted,
        TokenKind.LParen or TokenKind.RParen or TokenKind.LBracket or TokenKind.RBracket
            or TokenKind.LBrace or TokenKind.RBrace or TokenKind.Comma or TokenKind.Bar
            or TokenKind.Dot => SpanKind.Punctuation,
        TokenKind.Atom => AtomKind(t, operators),
        _ => SpanKind.Plain,
    };

    private static SpanKind AtomKind(Token t, OperatorTable? operators)
    {
        // A quoted atom reads as quoted text even when its name is an operator:
        // 'mod' is a name the reader will not treat as one.
        if (t.WasQuoted) return SpanKind.Quoted;
        if (operators is not null
            && (operators.TryGetInfix(t.Text, out _, out _)
                || operators.TryGetPrefix(t.Text, out _, out _)
                || operators.TryGetPostfix(t.Text, out _, out _)))
            return SpanKind.Operator;
        return SpanKind.Atom;
    }

    /// <summary>How much source a token occupies. The token carries its decoded
    /// text, which for a quoted atom or an escaped literal is shorter than what
    /// was written, so the length is measured on the SOURCE: from the token's
    /// offset to wherever the next one begins. That is what the caller does by
    /// covering gaps; here we only need the common case exactly right, and a
    /// conservative scan otherwise.</summary>
    private static int TokenLength(string source, Token t, int start)
    {
        switch (t.Kind)
        {
            case TokenKind.LParen or TokenKind.RParen or TokenKind.LBracket
                or TokenKind.RBracket or TokenKind.LBrace or TokenKind.RBrace
                or TokenKind.Comma or TokenKind.Bar or TokenKind.Dot:
                return 1;

            case TokenKind.String:
                return QuotedLength(source, start, source[start]);

            case TokenKind.Atom when t.WasQuoted:
                return QuotedLength(source, start, source[start]);

            default:
            {
                // Unquoted: the token's own text is what was written, unless the
                // reader decoded something (0'c, radix, digit separators). Take
                // the longer of the two readings, clamped to the buffer.
                int byText = t.Text.Length;
                int scanned = UnquotedLength(source, start);
                return Math.Min(source.Length - start, Math.Max(1, Math.Max(byText, scanned)));
            }
        }
    }

    private static int QuotedLength(string source, int start, char quote)
    {
        int i = start + 1;
        while (i < source.Length)
        {
            if (source[i] == '\\') { i += 2; continue; }          // escape
            if (source[i] == quote)
            {
                if (i + 1 < source.Length && source[i + 1] == quote) { i += 2; continue; }  // '' inside
                return i - start + 1;
            }
            i++;
        }
        return source.Length - start;   // unterminated: to end of buffer
    }

    private static int UnquotedLength(string source, int start)
    {
        int i = start;
        char c = source[i];
        if (char.IsLetter(c) || c == '_')
        {
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
            return i - start;
        }
        if (char.IsDigit(c))
        {
            while (i < source.Length
                   && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '.'))
            {
                if (source[i] == '.'
                    && (i + 1 >= source.Length || !char.IsDigit(source[i + 1])))
                    break;                                   // an end-of-clause dot
                i++;
            }
            // A quote here continues the number rather than opening an atom:
            // 0'a is a character code and 16'ff a radix literal, both one token.
            if (i < source.Length && source[i] == '\'')
            {
                i++;
                if (i < source.Length && source[i] == '\\')
                {
                    i++;                                     // 0'\n and friends
                    while (i < source.Length && char.IsLetterOrDigit(source[i])) i++;
                }
                else if (i < source.Length && char.IsLetterOrDigit(source[i]))
                    while (i < source.Length && char.IsLetterOrDigit(source[i])) i++;
                else if (i < source.Length)
                    i++;                                     // 0'  — a literal space, say
            }
            return i - start;
        }
        // Symbolic atom: a run of the symbol characters ISO allows in one.
        const string Symbolic = "+-*/\\^<>=~:.?@#&$";
        while (i < source.Length && Symbolic.IndexOf(source[i]) >= 0) i++;
        return Math.Max(1, i - start);
    }

    private static int FindComment(string source, int from, int upTo)
    {
        for (int i = from; i < upTo; i++)
        {
            if (source[i] == '%') return i;
            if (source[i] == '/' && i + 1 < upTo && source[i + 1] == '*') return i;
        }
        return -1;
    }

    private static int CommentEnd(string source, int start, int upTo)
    {
        if (source[start] == '%')
        {
            int nl = source.IndexOf('\n', start);
            return nl < 0 || nl > upTo ? upTo : nl;          // the newline is not the comment
        }
        int close = source.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return close < 0 ? upTo : Math.Min(upTo, close + 2);
    }
}
