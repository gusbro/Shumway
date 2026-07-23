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
    private readonly PrologFlags _flags;
    private readonly List<Token> _lookahead = new();

    /// <summary>arity_compat — true once the
    /// current clause has consumed a <c>--&gt;</c> atom token. Embedded
    /// native goals (<c>{ raw C }</c> substituted by <c>true</c>) apply
    /// only to NON-DCG clauses: inside a DCG rule braces keep their
    /// standard Prolog <c>{}/1</c> meaning. "Is this a DCG clause?" is
    /// decided by linear token order — the <c>--&gt;</c> token appears
    /// before any body <c>{</c> — which token consumption preserves
    /// (tokens are consumed strictly in stream order regardless of
    /// prefetch depth). Reset whenever a new top-level term read starts
    /// (<see cref="ReadClauseTerm"/> / <see cref="ReadTerm"/>).</summary>
    private bool _sawDcgArrow;

    /// <summary>The priority of <paramref name="name"/> as a bare operator-atom
    /// term — the maximum of its prefix / infix / postfix priorities, or 0 when
    /// it is not an operator.</summary>
    private int BareOperatorAtomPriority(string name)
    {
        int p = 0;
        if (_operators.TryGetPrefix(name, out int pre, out _)) p = System.Math.Max(p, pre);
        if (_operators.TryGetInfix(name, out int inf, out _)) p = System.Math.Max(p, inf);
        if (_operators.TryGetPostfix(name, out int post, out _)) p = System.Math.Max(p, post);
        return p;
    }

    public Parser(Lexer.Lexer lexer) : this(lexer, OperatorTable.Default(), new PrologFlags())
    {
    }

    public Parser(Lexer.Lexer lexer, OperatorTable operators)
        : this(lexer, operators, new PrologFlags())
    {
    }

    public Parser(Lexer.Lexer lexer, OperatorTable operators, PrologFlags flags)
    {
        ArgumentNullException.ThrowIfNull(lexer);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(flags);
        _lexer = lexer;
        _operators = operators;
        _flags = flags;
    }

    /// <summary>Reads a single term (no trailing dot expected). Returns when an
    /// operator or token at the top of the stream cannot be consumed at the current
    /// precedence ceiling.</summary>
    public Term ReadTerm()
    {
        _sawDcgArrow = false;   // new top-level term read
        return ReadTermInternal(1200, out _);
    }

    /// <summary>Reads a term followed by the clause-terminator dot. Throws if the
    /// dot is missing.</summary>
    public Term ReadClauseTerm()
    {
        _sawDcgArrow = false;   // new clause starts here
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

    /// <summary>Consumes tokens until the next clause-terminator
    /// <c>.</c> (or end of input). Used by <see cref="ClauseReader"/>
    /// to resync after a <see cref="ParseException"/> so the next
    /// clause can still be parsed — error-recovery in C-compiler
    /// style. The terminator dot itself is consumed; the next call
    /// to <see cref="ReadClauseTerm"/> starts on a clean clause.
    /// <para>The resync itself must never throw — a
    /// character the lexer can't tokenize (the very thing that may
    /// have caused the error being recovered from) is stepped over
    /// raw via <see cref="Lexer.Lexer.SkipInvalidCharacter"/>, so any
    /// malformed input yields diagnostics rather than a crash.</para>
    /// </summary>
    public void SkipToClauseTerminator()
    {
        while (true)
        {
            Token tok;
            try
            {
                tok = NextToken();
            }
            catch (LexerException)
            {
                _lexer.SkipInvalidCharacter();
                continue;
            }
            if (tok.Kind == TokenKind.Dot || tok.Kind == TokenKind.Eof) return;
        }
    }

    /// <summary>Drops any buffered lookahead tokens.
    /// Called by the ClauseReader before handing the raw character
    /// stream to <see cref="Lexer.Lexer.SkipNativeCodeSection"/>
    /// (Arity <c>:- c.</c> sections): a stale peeked token would
    /// otherwise replay ahead of the post-section input.</summary>
    public void DiscardLookahead() => _lookahead.Clear();

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
                bool knownOp = false;
                if (_operators.TryGetInfix(tok.Text, out int iPrec, out OperatorType iType))
                {
                    knownOp = true;
                    applied = TryApplyInfix(tok.Text, iPrec, iType, maxPrec, ref left, ref builtPrec);
                }
                if (!applied && _operators.TryGetPostfix(tok.Text, out int pPrec, out OperatorType pType))
                {
                    knownOp = true;
                    applied = TryApplyPostfix(tok.Text, pPrec, pType, maxPrec, ref left, ref builtPrec);
                }
                // The lexer reads a maximal run of graphic
                // chars, so '1+-2' tokenises as Int(1), Atom('+-'),
                // Int(2). If '+-' isn't a registered infix, try
                // splitting it into a known infix prefix + a known
                // unary-prefix suffix (e.g. '+' as binary + '-' as
                // unary -). Matches SWI's reader.
                //
                // BUT only when the whole token is NOT itself a registered
                // infix/postfix operator: a known operator that failed only
                // the precedence check is a precedence boundary, not a glued
                // run. Splitting e.g. ':-' (a real xfx 1200 operator) into
                // ':' (xfy 200) + '-' when it doesn't fit the current max
                // priority would wrongly consume it — turning
                // `H :- Body` read at a sub-1200 max (an operator-headed
                // clause like `a = b :- c` or Logtalk's `X::Y :- Body`) into
                // `=`/`::`-rooted garbage instead of stopping at the ':-'.
                if (!applied && !knownOp && tok.Text.Length > 1
                    && TrySplitInfixUnary(tok, maxPrec, ref left, ref builtPrec))
                    applied = true;
                if (applied) continue;
            }

            break;
        }

        return left;
    }

    private bool TrySplitInfixUnary(Token tok, int maxPrec, ref Term left, ref int builtPrec)
    {
        // Try every prefix length from longest down — longer match
        // wins so '1+--2' goes '+' / '--' if '--' is a known prefix
        // op, rather than '+-' / '-'.
        for (int len = tok.Text.Length - 1; len > 0; len--)
        {
            string prefix = tok.Text.Substring(0, len);
            string suffix = tok.Text.Substring(len);
            if (!_operators.TryGetInfix(prefix, out int piPrec, out OperatorType piType))
                continue;
            if (!_operators.TryGetPrefix(suffix, out _, out _))
                continue;
            // Replace the current lookahead with the prefix, then
            // queue the suffix as the next token so the right-operand
            // read picks it up as a prefix op.
            _lookahead[0] = new Token(TokenKind.Atom, tok.Position, prefix);
            int sufCol = tok.Position.Column + len;
            _lookahead.Insert(1, new Token(TokenKind.Atom,
                new SourcePosition(tok.Position.Line, sufCol,
                    tok.Position.Offset + len, tok.Position.FileId), suffix));
            if (TryApplyInfix(prefix, piPrec, piType, maxPrec, ref left, ref builtPrec))
                return true;
            // The infix didn't fit (precedence constraint) — undo the
            // split so other tokenisers can have a go.
            _lookahead.RemoveAt(1);
            _lookahead[0] = tok;
            return false;
        }
        return false;
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
            //
            // ISO §6.4.7 — a '(' immediately after the atom (no
            // intervening whitespace) is the function-call '(', binding the
            // atom as the compound head. A '(' with whitespace before it is
            // a grouping paren and the atom acts as a prefix op. So
            // '\+(a, b)' is '\+'/2 (function-call shape — but '\+'/2 isn't
            // defined, so this would fail later), while '\+ (a, b)' is
            // '\+'/1 applied to the conjunction (a, b). This matches SWI's
            // reader.
            Token next = PeekTokenAt(1);
            bool followedByCompoundParen =
                next.Kind == TokenKind.LParen && IsAdjacent(tok, next);
            bool nextCanStart = CanStartTerm(next);

            // ISO disambiguation for `op/N` (predicate-indicator
            // notation). An atom like `not` that is both a prefix
            // operator (fy 900) AND a valid plain atom collides with
            // `not/1` inside a list / argument position: the prefix-
            // form parse would commit to `not('/')` and strand the
            // arity integer behind it.
            //
            // Narrow rule: when `tok` is a prefix operator AND the
            // very next tokens form `/ <integer>`, the user clearly
            // means the indicator `tok/<integer>` and we should let
            // the outer infix loop apply `/` to `tok` as its left
            // operand. This catches `[not/1, catch/3]` etc. without
            // disturbing real prefix uses like `not member(X, L)` or
            // `:- public '#='/2` (where the next token is a quoted
            // atom, not the bare `/`). Matches SWI / GNU behaviour
            // for the common case.
            bool nextIsPredicateIndicatorSlash =
                next.Kind == TokenKind.Atom && next.Text == "/"
                && PeekTokenAt(2).Kind == TokenKind.Integer;

            if (nextCanStart && !followedByCompoundParen
                && !nextIsPredicateIndicatorSlash)
            {
                NextToken();
                int rightMax = opType == OperatorType.Fy ? opPrec : opPrec - 1;
                Term operand = ReadTermInternal(rightMax, out _);
                builtPrec = opPrec;
                return new CompoundTerm(tok.Text, new[] { operand }) { Position = pos };
            }
        }

        // ISO §6.3.1.3: a bare operator-atom used as the OPERAND of an operator
        // has the operator's own priority, so it cannot sit where a
        // lower-priority term is required — `- -` (the operand `-` has priority
        // 500, but a prefix `-` admits ≤ 200) is a syntax error, as is `a * *`
        // (the right operand `*` needs ≤ 399). This is checked only in an
        // operator-operand position (maxPrec < 999): a bare operator-atom used
        // as a delimited ARGUMENT or list element (`f(:-)`, `[:-,-]`, read at
        // 999) or at the top level is a complete atom term and stays valid. A
        // parenthesised `(*)` / compound `f(*)` reads as a non-atom (exempt),
        // and a QUOTED atom (`'<'`) is a plain priority-0 atom (exempt).
        Token pk = PeekToken();
        Term prim = ReadPrimary();
        builtPrec = 0;
        if (maxPrec < 999 && pk.Kind == TokenKind.Atom && !pk.WasQuoted
            && prim is AtomTerm bareAt && bareAt.Name == pk.Text)
        {
            int p = BareOperatorAtomPriority(bareAt.Name);
            if (p > maxPrec)
                throw new ParseException(
                    $"Operator '{bareAt.Name}' (priority {p}) needs parentheses "
                    + $"to be an operand here (maximum priority {maxPrec}).", pk.Position);
            builtPrec = p;
        }
        return prim;
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
                return BuildStringLiteral(tok.Text, pos);

            case TokenKind.Atom:
                // foo(arg1, arg2, ...) — only when '(' immediately follows
                // (no whitespace between, per ISO §6.4.7), no operator form
                // is parsed (we consume the atom as the head of a compound).
                // 'foo (a, b)' with a space is the atom 'foo' followed by a
                // grouping paren — handled by the caller's operator-position
                // loop.
                if (PeekToken().Kind == TokenKind.LParen
                    && IsAdjacent(tok, PeekToken()))
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
                // Snip (Arity-Prolog): `[! Goal !]` desugars to `once((Goal))`.
                // A `,`-chain inside a snip is a goal conjunction — backtracking
                // is permitted internally; once the snip exits successfully its
                // internal choice points are pruned, so a later failure skips
                // back to before the `[!` rather than re-entering the snip.
                // Trade-off: a list whose first element is the cut atom now
                // needs to be written `[(!), ...]` instead of `[!, ...]`.
                // A QUOTED '!' is never a snip opener: the
                // Arity corpus writes lists like ['!', Token], which must
                // parse as ordinary two-element lists.
                if (PeekToken().Kind == TokenKind.Atom && PeekToken().Text == "!"
                    && !PeekToken().WasQuoted)
                {
                    NextToken();   // consume the opening '!'
                    Term snipBody = ReadTermInternal(1200, out _);
                    Token closeBang = NextToken();
                    if (closeBang.Kind != TokenKind.Atom || closeBang.Text != "!")
                        throw new ParseException(
                            $"Expected '!' to close snip; got {DescribeToken(closeBang)}.",
                            closeBang.Position);
                    ExpectKind(TokenKind.RBracket);
                    return new CompoundTerm("once", new[] { snipBody }) { Position = pos };
                }
                return ReadList(pos);

            case TokenKind.LBrace:
                // arity_compat only — Arity embedded
                // native goal: in a NON-DCG clause a body goal can be raw
                // native code between braces (`p :- g, { C code; }, h.`).
                // The brace content is not Prolog-lexable, so it is
                // skipped RAW by the lexer (naive brace counting) and the
                // goal `true` is substituted. TODO: the real
                // implementation (compiling/binding the native code)
                // comes later — for now the goal is a no-op.
                //
                // In a DCG rule (`head --> body`) braces keep their ISO
                // {}/1 meaning — detection is the per-clause _sawDcgArrow
                // flag (the --> token consumed before this `{` in linear
                // order). Known trade-off, documented: under the flag,
                // CLP(R)'s `{Constraint}` syntax in a normal clause (and
                // a `{...}` argument of a directive) is swallowed as
                // native code — acceptable, Arity sources don't use
                // CLP(R).
                if (_flags.ArityCompat && !_sawDcgArrow)
                {
                    // Lookahead invariant: the raw skip must begin at the
                    // character right after this `{`, so no token may
                    // have been prefetched past it. That holds by
                    // construction — multi-token peeks (PeekTokenAt(1/2))
                    // run only when the token at offset 0 is an Atom, so
                    // nothing is ever fetched beyond an LBrace sitting at
                    // the front of the stream, and consuming the `{`
                    // therefore drains the buffer. Guarded here so any
                    // future lookahead change fails loudly instead of
                    // silently mis-skipping.
                    if (_lookahead.Count > 0)
                        throw new ParseException(
                            "internal: token lookahead extends past a native '{' goal "
                            + "(arity_compat); raw skip would start at the wrong position.",
                            pos);
                    // ADR-022 — capture the raw C statement
                    // text and carry it in a `'$native_goal'(RawText)` term (the
                    // raw text as a non-interned StringTerm). Until the native
                    // codegen lands (step 4), '$native_goal'/1 is a no-op builtin
                    // — same runtime behaviour as the previous `true`, but the
                    // span is no longer lost.
                    string nativeText = _lexer.SkipNativeGoalBlock(pos);
                    return new CompoundTerm("$native_goal",
                        new Term[] { new StringTerm(nativeText) { Position = pos } })
                        { Position = pos };
                }
                return ReadBrace(pos);

            case TokenKind.Comma:
            case TokenKind.Bar:
                // Comma / bar as a FUNCTOR in canonical prefix form —
                // ','(A, B) and '|'(A, B). `write_canonical` emits nested
                // conjunctions this way (a Logtalk compiler scratch file is
                // full of `:-(Head, ,(G1, ,(G2, G3)))`), and every ISO Prolog
                // reads it back. These tokens surface with their own kinds
                // (they act as infix separators in operator position), so the
                // functor reading only applies when one lands where an operand
                // is expected AND a '(' immediately follows; a bare separator
                // here is still the syntax error the default arm reports.
                if (PeekToken().Kind == TokenKind.LParen
                    && IsAdjacent(tok, PeekToken()))
                {
                    string sepName = tok.Kind == TokenKind.Comma ? "," : "|";
                    NextToken();   // consume '('
                    var sepArgs = ReadCommaSeparatedArgs(closing: TokenKind.RParen);
                    if (sepArgs.Count == 0)
                        throw new ParseException(
                            $"Compound term '{sepName}' requires at least one argument.", pos);
                    return new CompoundTerm(sepName, sepArgs.ToArray()) { Position = pos };
                }
                goto default;

            default:
                throw new ParseException(
                    $"Unexpected {DescribeToken(tok)} when a term was expected.", pos);
        }
    }

    /// <summary>Materialises a <c>"..."</c> literal according to the
    /// <see cref="PrologFlags.DoubleQuotes"/> setting at parse
    /// time. The default <see cref="DoubleQuotesMode.String"/>
    /// preserves Shumway's native PSTR representation; <c>codes</c>
    /// and <c>chars</c> expand into proper cons lists at parse time
    /// so the rest of the pipeline sees plain Prolog terms.</summary>
    private Term BuildStringLiteral(string text, Shumway.Compiler.Lexer.SourcePosition pos)
    {
        switch (_flags.DoubleQuotes)
        {
            case DoubleQuotesMode.Codes:
            {
                Term acc = new AtomTerm("[]") { Position = pos };
                for (int i = text.Length - 1; i >= 0; i--)
                    acc = new CompoundTerm(".", new Term[] { new IntTerm(text[i]), acc }) { Position = pos };
                return acc;
            }
            case DoubleQuotesMode.Chars:
            {
                Term acc = new AtomTerm("[]") { Position = pos };
                for (int i = text.Length - 1; i >= 0; i--)
                    acc = new CompoundTerm(".", new Term[]
                    {
                        new AtomTerm(text[i].ToString()),
                        acc
                    }) { Position = pos };
                return acc;
            }
            case DoubleQuotesMode.Atom:
                return new AtomTerm(text) { Position = pos };
            case DoubleQuotesMode.String:
            default:
                return new StringTerm(text) { Position = pos };
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
            // Arity tolerates a dangling comma at the end of an argument
            // list (e.g. `ifthenelse(..., save_old_mod,
            // % comment \n )`) — under the flag a comma immediately
            // followed by the closing `)` is treated as if absent. The
            // narrowest tolerance the corpus needs: arg lists only
            // (closing == RParen), never lists/curlies, never flag-off.
            if (_flags.ArityCompat && closing == TokenKind.RParen
                && PeekToken().Kind == TokenKind.RParen)
                break;
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
            Token rbrace = NextToken();
            // ISO 6.3.3 — `{}` is an atom, and like any atom it heads a
            // compound when immediately followed by '(' (no whitespace):
            // `{}(X)` ≡ `{X}`. Logtalk's compiler emits the functional
            // form in its generated code (lgtunit's with_output_to
            // meta-argument), so both spellings must parse.
            if (PeekToken().Kind == TokenKind.LParen
                && IsAdjacent(rbrace, PeekToken()))
            {
                NextToken();   // consume '('
                var args = ReadCommaSeparatedArgs(closing: TokenKind.RParen);
                if (args.Count == 0)
                    throw new ParseException(
                        "Compound term '{}' requires at least one argument; "
                        + "for the zero-arity case use the bare atom.", pos);
                return new CompoundTerm("{}", args.ToArray()) { Position = pos };
            }
            return new AtomTerm("{}") { Position = pos };
        }
        Term inner = ReadTermInternal(1200, out _);
        ExpectKind(TokenKind.RBrace);
        return new CompoundTerm("{}", new[] { inner }) { Position = pos };
    }

    // ---------- Token helpers ----------

    /// <summary>True iff <paramref name="next"/> immediately follows
    /// <paramref name="prev"/> in the source — no whitespace, no
    /// comment, no line break between them. Used by the
    /// function-call-vs-prefix-op disambiguation: <c>foo(a)</c> is
    /// a compound, <c>foo (a)</c> is an atom followed by a
    /// parenthesised term. Reads the lexer's leading-whitespace
    /// flag on the next token directly so quoted atoms (whose
    /// <c>Text</c> is the decoded form, not the source span) still
    /// work.</summary>
    private static bool IsAdjacent(Token prev, Token next) =>
        !next.HasLeadingWhitespace;

    private Token NextToken()
    {
        Token t;
        if (_lookahead.Count > 0)
        {
            t = _lookahead[0];
            _lookahead.RemoveAt(0);
        }
        else
        {
            t = _lexer.NextToken();
        }
        // DCG detection for Arity native goals: record that
        // the current clause consumed the --> arrow. (A quoted '-->'
        // atom is indistinguishable from the operator token here —
        // harmless: it only widens braces back to their ISO meaning.)
        if (t.Kind == TokenKind.Atom && t.Text == "-->") _sawDcgArrow = true;
        return t;
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
