using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Token-stream → <see cref="Term"/> parser with full Prolog operator-precedence
/// handling. The algorithm is the standard one: <see cref="ReadTerm"/> recursively
/// reads a prefix term followed by zero or more infix/postfix operators, gated by a
/// maximum-allowed-precedence parameter that flows from the calling context (callers
/// inside compound argument lists, list elements and braced groups all pass 999/1200
/// as appropriate). Each call tracks the precedence of the term it just built so it
/// can reject precedence-clashing operator combinations like <c>a = b = c</c>.
///
/// <para>The parser keeps a one-token lookahead via <see cref="PeekToken"/>; reading
/// a token off the buffer either replays the peeked value or pulls from the
/// <see cref="Lexer.Lexer"/>.</para>
///
/// <para>Two pieces of Prolog syntax are desugared at parse time:</para>
/// <list type="bullet">
/// <item>List syntax: <c>[a, b | T]</c> becomes the cons compound
///   <c>'.'(a, '.'(b, T))</c>. The empty list <c>[]</c> is an atom.</item>
/// <item>Brace syntax: <c>{X}</c> becomes <c>'{}'(X)</c>; the empty <c>{}</c> is
///   an atom.</item>
/// </list>
/// </summary>
public sealed class Parser
{
    private readonly Lexer.Lexer _lexer;
    private readonly OperatorTable _operators;
    private readonly List<Token> _lookahead = new();

    public Parser(Lexer.Lexer lexer) : this(lexer, OperatorTable.Default())
    {
    }

    public Parser(Lexer.Lexer lexer, OperatorTable operators)
    {
        ArgumentNullException.ThrowIfNull(lexer);
        ArgumentNullException.ThrowIfNull(operators);
        _lexer = lexer;
        _operators = operators;
    }

    /// <summary>Reads a single term (no trailing dot expected). Returns when an
    /// operator or token at the top of the stream cannot be consumed at the current
    /// precedence ceiling.</summary>
    public Term ReadTerm() => ReadTermInternal(1200, out _);

    /// <summary>Reads a term followed by the clause-terminator dot. Throws if the
    /// dot is missing.</summary>
    public Term ReadClauseTerm()
    {
        Term t = ReadTermInternal(1200, out _);
        Token tok = NextToken();
        if (tok.Kind != TokenKind.Dot)
            throw new ParseException(
                $"Expected '.' after clause, got {DescribeToken(tok)}.", tok.Position);
        return t;
    }

    /// <summary>Reports whether the lookahead position is at end of input. Useful
    /// to a clause-stream reader that wants to stop without trying (and failing) to
    /// parse another clause past the end of file.</summary>
    public bool IsAtEnd() => PeekToken().Kind == TokenKind.Eof;

    // ---------- Core recursive reader ----------

    private Term ReadTermInternal(int maxPrec, out int builtPrec)
    {
        Term left = ReadPrefixOrPrimary(maxPrec, out builtPrec);

        while (true)
        {
            Token tok = PeekToken();

            // Comma and bar surface as their own token kinds but act as infix
            // operators when their precedence fits — treat them as named atoms
            // for operator-lookup purposes.
            if (tok.Kind == TokenKind.Comma)
            {
                if (!TryApplyInfix(",", 1000, OperatorType.Xfy, maxPrec, ref left, ref builtPrec)) break;
                continue;
            }
            if (tok.Kind == TokenKind.Bar)
            {
                if (!TryApplyInfix("|", 1100, OperatorType.Xfy, maxPrec, ref left, ref builtPrec)) break;
                continue;
            }

            if (tok.Kind == TokenKind.Atom)
            {
                bool applied = false;
                if (_operators.TryGetInfix(tok.Text, out int iPrec, out OperatorType iType))
                    applied = TryApplyInfix(tok.Text, iPrec, iType, maxPrec, ref left, ref builtPrec);
                if (!applied && _operators.TryGetPostfix(tok.Text, out int pPrec, out OperatorType pType))
                    applied = TryApplyPostfix(tok.Text, pPrec, pType, maxPrec, ref left, ref builtPrec);
                if (applied) continue;
            }

            break;
        }

        return left;
    }

    private bool TryApplyInfix(
        string name, int opPrec, OperatorType opType,
        int maxPrec, ref Term left, ref int builtPrec)
    {
        if (opPrec > maxPrec) return false;
        // Left-arg constraint: x = strictly lower, y = same or lower
        int leftMax = opType == OperatorType.Yfx ? opPrec : opPrec - 1;
        if (builtPrec > leftMax) return false;

        SourcePosition pos = PeekToken().Position;
        NextToken();
        int rightMax = opType == OperatorType.Xfy ? opPrec : opPrec - 1;
        Term right = ReadTermInternal(rightMax, out _);
        left = new CompoundTerm(name, new[] { left, right }) { Position = pos };
        builtPrec = opPrec;
        return true;
    }

    private bool TryApplyPostfix(
        string name, int opPrec, OperatorType opType,
        int maxPrec, ref Term left, ref int builtPrec)
    {
        if (opPrec > maxPrec) return false;
        int leftMax = opType == OperatorType.Yf ? opPrec : opPrec - 1;
        if (builtPrec > leftMax) return false;

        SourcePosition pos = PeekToken().Position;
        NextToken();
        left = new CompoundTerm(name, new[] { left }) { Position = pos };
        builtPrec = opPrec;
        return true;
    }

    // ---------- Prefix-or-primary ----------

    private Term ReadPrefixOrPrimary(int maxPrec, out int builtPrec)
    {
        Token tok = PeekToken();
        SourcePosition pos = tok.Position;

        // Negative numeric literals — collapse `-` followed by an Integer
        // or Float token into a single numeric term. This is what callers
        // mean by `-1` / `-3.14` in source: an actual negative number,
        // not the compound `-/1`. Explicit `- (3)` (with parens) still
        // produces the compound via the standard prefix-op path below.
        if (tok.Kind == TokenKind.Atom && tok.Text == "-")
        {
            Token next = PeekTokenAt(1);
            if (next.Kind == TokenKind.Integer)
            {
                NextToken();   // consume '-'
                Token numTok = NextToken();
                builtPrec = 0;
                if (numTok.HasBigValue)
                    return new BigIntTerm(-numTok.BigValue) { Position = pos };
                return new IntTerm(-numTok.IntValue) { Position = pos };
            }
            if (next.Kind == TokenKind.Float)
            {
                NextToken();
                Token numTok = NextToken();
                builtPrec = 0;
                return new FloatTerm(-numTok.FloatValue) { Position = pos };
            }
        }

        if (tok.Kind == TokenKind.Atom
            && _operators.TryGetPrefix(tok.Text, out int opPrec, out OperatorType opType)
            && opPrec <= maxPrec)
        {
            // Disambiguation: an atom that is both a prefix operator and a valid
            // standalone atom is treated as a prefix op only when followed by a
            // token that can itself start a term, AND that token is not the
            // open-paren that would turn the atom into a compound term.
            Token next = PeekTokenAt(1);
            bool followedByCompoundParen = next.Kind == TokenKind.LParen;
            bool nextCanStart = CanStartTerm(next);

            if (nextCanStart && !followedByCompoundParen)
            {
                NextToken();
                int rightMax = opType == OperatorType.Fy ? opPrec : opPrec - 1;
                Term operand = ReadTermInternal(rightMax, out _);
                builtPrec = opPrec;
                return new CompoundTerm(tok.Text, new[] { operand }) { Position = pos };
            }
        }

        builtPrec = 0;
        return ReadPrimary();
    }

    // ---------- Primaries (atom, var, number, string, list, brace, paren, compound) ----------

    private Term ReadPrimary()
    {
        Token tok = NextToken();
        SourcePosition pos = tok.Position;

        switch (tok.Kind)
        {
            case TokenKind.Variable:
                return new VarTerm(tok.Text) { Position = pos };

            case TokenKind.Integer:
                return tok.HasBigValue
                    ? new BigIntTerm(tok.BigValue) { Position = pos }
                    : new IntTerm(tok.IntValue) { Position = pos };

            case TokenKind.Float:
                return new FloatTerm(tok.FloatValue) { Position = pos };

            case TokenKind.String:
                return new StringTerm(tok.Text) { Position = pos };

            case TokenKind.Atom:
                // foo(arg1, arg2, ...) — only when '(' immediately follows, no operator
                // form is parsed (we consume the atom as the head of a compound).
                if (PeekToken().Kind == TokenKind.LParen)
                {
                    NextToken();   // consume '('
                    var args = ReadCommaSeparatedArgs(closing: TokenKind.RParen);
                    if (args.Count == 0)
                        throw new ParseException(
                            $"Compound term '{tok.Text}' requires at least one argument; "
                            + "for the zero-arity case use the bare atom.", pos);
                    return new CompoundTerm(tok.Text, args.ToArray()) { Position = pos };
                }
                return new AtomTerm(tok.Text) { Position = pos };

            case TokenKind.LParen:
            {
                Term inner = ReadTermInternal(1200, out _);
                ExpectKind(TokenKind.RParen);
                // Keep the inner term's own position (more informative than the
                // paren's) — Position is excluded from value equality anyway.
                return inner;
            }

            case TokenKind.LBracket:
                return ReadList(pos);

            case TokenKind.LBrace:
                return ReadBrace(pos);

            default:
                throw new ParseException(
                    $"Unexpected {DescribeToken(tok)} when a term was expected.", pos);
        }
    }

    private List<Term> ReadCommaSeparatedArgs(TokenKind closing)
    {
        var args = new List<Term>();
        if (PeekToken().Kind == closing)
        {
            NextToken();
            return args;
        }
        args.Add(ReadTermInternal(999, out _));
        while (PeekToken().Kind == TokenKind.Comma)
        {
            NextToken();
            args.Add(ReadTermInternal(999, out _));
        }
        ExpectKind(closing);
        return args;
    }

    private Term ReadList(SourcePosition pos)
    {
        if (PeekToken().Kind == TokenKind.RBracket)
        {
            NextToken();
            return new AtomTerm("[]") { Position = pos };
        }

        var elements = new List<Term>();
        elements.Add(ReadTermInternal(999, out _));
        while (PeekToken().Kind == TokenKind.Comma)
        {
            NextToken();
            elements.Add(ReadTermInternal(999, out _));
        }

        Term tail;
        if (PeekToken().Kind == TokenKind.Bar)
        {
            NextToken();
            tail = ReadTermInternal(999, out _);
        }
        else
        {
            tail = new AtomTerm("[]") { Position = pos };
        }
        ExpectKind(TokenKind.RBracket);

        // Fold right: a,b,c | T → '.'(a, '.'(b, '.'(c, T)))
        Term result = tail;
        for (int i = elements.Count - 1; i >= 0; i--)
            result = new CompoundTerm(".", new[] { elements[i], result }) { Position = elements[i].Position };
        return result;
    }

    private Term ReadBrace(SourcePosition pos)
    {
        if (PeekToken().Kind == TokenKind.RBrace)
        {
            NextToken();
            return new AtomTerm("{}") { Position = pos };
        }
        Term inner = ReadTermInternal(1200, out _);
        ExpectKind(TokenKind.RBrace);
        return new CompoundTerm("{}", new[] { inner }) { Position = pos };
    }

    // ---------- Token helpers ----------

    private Token NextToken()
    {
        if (_lookahead.Count > 0)
        {
            Token t = _lookahead[0];
            _lookahead.RemoveAt(0);
            return t;
        }
        return _lexer.NextToken();
    }

    private Token PeekToken() => PeekTokenAt(0);

    /// <summary>Look ahead at the token <paramref name="offset"/> positions past the
    /// current one without consuming any. <c>offset=0</c> is the next token
    /// <see cref="NextToken"/> would return; <c>offset=1</c> is the one after.
    /// Used by <see cref="ReadPrefixOrPrimary"/> to decide whether an atom in
    /// operator position is acting as a prefix operator or as a compound-term
    /// head.</summary>
    private Token PeekTokenAt(int offset)
    {
        while (_lookahead.Count <= offset)
            _lookahead.Add(_lexer.NextToken());
        return _lookahead[offset];
    }

    private void ExpectKind(TokenKind kind)
    {
        Token tok = NextToken();
        if (tok.Kind != kind)
            throw new ParseException(
                $"Expected {kind}, got {DescribeToken(tok)}.", tok.Position);
    }

    private static bool CanStartTerm(Token tok) => tok.Kind switch
    {
        TokenKind.Atom or TokenKind.Variable
            or TokenKind.Integer or TokenKind.Float or TokenKind.String
            or TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace => true,
        _ => false,
    };

    private static string DescribeToken(Token tok) => tok.Kind switch
    {
        TokenKind.Eof => "end of input",
        TokenKind.Atom or TokenKind.Variable or TokenKind.String => $"{tok.Kind} '{tok.Text}'",
        TokenKind.Integer => $"integer {tok.IntValue}",
        TokenKind.Float => $"float {tok.FloatValue}",
        _ => $"{tok.Kind} '{tok.Text}'",
    };
}
