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
    private OperatorTable _operators;

    /// <summary>ADR-046 — retargets the parser at another operator layer
    /// mid-stream. Called by the ClauseReader when a <c>:- module/2</c>
    /// directive switches the rest of the file to the module's table.</summary>
    internal void SwitchOperators(OperatorTable operators)
        => _operators = operators;
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

    // Set by ReadPrefixOrPrimary / ReadTermInternal: true when the term just
    // read was a BARE operator-atom (unparenthesised, unquoted, no operator
    // applied on top). ISO §6.3.1.3 forbids it as the immediate operand of an
    // operator; the apply sites throw — except for the predicate-indicator
    // `op/N` (see TryApplyInfix), which every Prolog accepts (`dynamic/1`).
    private bool _bareOp;

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
        // Flags that affect LEXING travel to the lexer here, so every parse
        // path (consult, runtime queries, read/1, term_from_atom) lexes the
        // same way; ClauseReader can still flip them mid-file on a
        // set_prolog_flag directive.
        lexer.ArityCompat = flags.ArityCompat;
        lexer.DigitSeparators = flags.DigitSeparators;
        lexer.LenientQuoteCharLiteral = flags.LenientQuoteCharLiteral;
        lexer.LenientEscapes = flags.LenientEscapes;
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
        return ContinueTerm(left, maxPrec, ref builtPrec);
    }

    /// <summary>The operator loop, entered with <paramref name="left"/> already
    /// read. Split out so a caller that obtained the leading primary by other
    /// means — the iterative compound reader — can finish the expression
    /// without re-entering the recursive path.</summary>
    private Term ContinueTerm(Term left, int maxPrec, ref int builtPrec)
    {
        bool leftBareOp = _bareOp;   // is `left` (still) a bare operator-atom?

        while (true)
        {
            Token tok = PeekToken();

            // Comma and bar surface as their own token kinds but act as infix
            // operators when their precedence fits — treat them as named atoms
            // for operator-lookup purposes. In SWI argument mode the enclosing
            // argument/list context suppresses them (they are separators there
            // even though the argument itself reads at 1200).
            if (tok.Kind == TokenKind.Comma)
            {
                if (_suppressComma) break;
                if (!TryApplyInfix(",", 1000, OperatorType.Xfy, maxPrec, ref left, ref builtPrec, ref leftBareOp)) break;
                continue;
            }
            if (tok.Kind == TokenKind.Bar)
            {
                if (_suppressBar) break;
                // Strict ISO has no bar operator: `(a|b)` is a syntax error
                // unless op/3 registered `|` (infix > 1000, Cor.2). Two
                // sanctioned exceptions get the classic xfy 1100: a DCG rule
                // body, where `|` is the TS 13211-3 alternation connective,
                // and dialect leniency (SWI / Scryer / Arity sources).
                if (!_operators.TryGetInfix("|", out int barPrec, out OperatorType barType))
                {
                    if (!_sawDcgArrow && !_flags.LenientBareOperatorOperands
                        && !_flags.ArityCompat)
                        break;
                    barPrec = 1100; barType = OperatorType.Xfy;
                }
                if (!TryApplyInfix("|", barPrec, barType, maxPrec, ref left, ref builtPrec, ref leftBareOp)) break;
                continue;
            }

            if (tok.Kind == TokenKind.Atom)
            {
                bool applied = false;
                bool knownOp = false;
                if (_operators.TryGetInfix(tok.Text, out int iPrec, out OperatorType iType))
                {
                    knownOp = true;
                    applied = TryApplyInfix(tok.Text, iPrec, iType, maxPrec, ref left, ref builtPrec, ref leftBareOp);
                }
                if (!applied && _operators.TryGetPostfix(tok.Text, out int pPrec, out OperatorType pType))
                {
                    knownOp = true;
                    applied = TryApplyPostfix(tok.Text, pPrec, pType, maxPrec, ref left, ref builtPrec, ref leftBareOp);
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
                    && TrySplitInfixUnary(tok, maxPrec, ref left, ref builtPrec, ref leftBareOp))
                    applied = true;
                if (applied) continue;
            }

            break;
        }

        _bareOp = leftBareOp;   // report whether the whole term is a bare op-atom
        return left;
    }

    private bool TrySplitInfixUnary(Token tok, int maxPrec, ref Term left, ref int builtPrec, ref bool leftBareOp)
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
            if (TryApplyInfix(prefix, piPrec, piType, maxPrec, ref left, ref builtPrec, ref leftBareOp))
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
        int maxPrec, ref Term left, ref int builtPrec, ref bool leftBareOp)
    {
        if (opPrec > maxPrec) return false;
        // Left-arg constraint: x = strictly lower, y = same or lower
        int leftMax = opType == OperatorType.Yfx ? opPrec : opPrec - 1;
        if (builtPrec > leftMax) return false;

        SourcePosition pos = PeekToken().Position;
        NextToken();
        int rightMax = opType == OperatorType.Xfy ? opPrec : opPrec - 1;
        Term right = ReadTermInternal(rightMax, out _);
        bool rightBareOp = _bareOp;

        // ISO §6.3.1.3: the operands of an operator may not be a bare
        // operator-atom (`* = *` must be `(*) = (*)`) — INCLUDING the
        // predicate-indicator shape `op/N`: `--> /2` reads only as
        // `(-->)/2` (conformity s#378), exactly as the strictest engines
        // read it. SWI-style sources that write `dynamic/1` bare load
        // under the dialect leniency below.
        // Arity sources use quoted operator atoms as plain operands
        // (Blint's `Char = '/'`) — arity_compat rides the same leniency the
        // dialect scopes get; the bare ISO default stays strict.
        bool lenientOperand = _flags.LenientBareOperatorOperands || _flags.ArityCompat;
        if (leftBareOp && !lenientOperand)
            throw new ParseException(
                $"Operator atom cannot be the left operand of '{name}' "
                + "without parentheses.", pos);
        if (rightBareOp && !lenientOperand)
            throw new ParseException(
                $"Operator atom cannot be the right operand of '{name}' "
                + "without parentheses.", pos);

        left = new CompoundTerm(name, new[] { left, right }) { Position = pos };
        builtPrec = opPrec;
        leftBareOp = false;   // `left` is now a compound
        return true;
    }

    private bool TryApplyPostfix(
        string name, int opPrec, OperatorType opType,
        int maxPrec, ref Term left, ref int builtPrec, ref bool leftBareOp)
    {
        if (opPrec > maxPrec) return false;
        int leftMax = opType == OperatorType.Yf ? opPrec : opPrec - 1;
        if (builtPrec > leftMax) return false;

        if (leftBareOp && !_flags.LenientBareOperatorOperands && !_flags.ArityCompat)
            throw new ParseException(
                $"Operator atom cannot be the operand of postfix '{name}' "
                + "without parentheses.", PeekToken().Position);

        SourcePosition pos = PeekToken().Position;
        NextToken();
        left = new CompoundTerm(name, new[] { left }) { Position = pos };
        builtPrec = opPrec;
        leftBareOp = false;
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
                return new FloatTerm(-FiniteFloat(numTok, pos, negated: true)) { Position = pos };
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

            // SWI leniency: when the would-be operand is itself a bare
            // NON-prefix operator atom (`Spec == '-'  ->  …` — after the '-'
            // comes `->`), SWI reads the current atom as a PLAIN ATOM and
            // lets the following operator apply infix, where strict ISO
            // §6.3.1.3 rejects the whole form. Only when the next atom cannot
            // head a compound (no adjacent '(').
            bool nextIsBareNonPrefixOp =
                _flags.LenientBareOperatorOperands
                && next.Kind == TokenKind.Atom && !next.WasQuoted
                && !_operators.TryGetPrefix(next.Text, out _, out _)
                && BareOperatorAtomPriority(next.Text) > 0
                && !(PeekTokenAt(2).Kind == TokenKind.LParen
                     && IsAdjacent(next, PeekTokenAt(2)));

            if (nextCanStart && !followedByCompoundParen
                && !nextIsPredicateIndicatorSlash
                && !nextIsBareNonPrefixOp)
            {
                NextToken();
                int rightMax = opType == OperatorType.Fy ? opPrec : opPrec - 1;
                Term operand = ReadTermInternal(rightMax, out _);
                // ISO §6.3.1.3: a prefix operator's operand may not be a bare
                // operator-atom (`- =` must be `- (=)`).
                if (_bareOp && !_flags.LenientBareOperatorOperands)
                    throw new ParseException(
                        $"Operator atom cannot be the operand of prefix "
                        + $"'{tok.Text}' without parentheses.", pos);
                builtPrec = opPrec;
                _bareOp = false;
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
        // parenthesised `(*)` / compound `f(*)` reads as a non-atom (exempt).
        //
        // A QUOTED atom is NOT exempt: quotes change the token, not the atom —
        // `'\\'` IS the operator `\` (Neumerkel syntax #106), so `X = '\\'` is
        // the same error as `X = *`, and the conforming spelling is `X = ('\\')`.
        // The leniencies that DO accept it: arity_compat (Arity sources use
        // quoted operator atoms as plain operands) and the SWI dialect scope
        // (LenientBareOperatorOperands — SWI accepts them everywhere).
        Token pk = PeekToken();
        Term prim = ReadPrimary();
        builtPrec = 0;
        _bareOp = false;
        bool quotedExempt = pk.WasQuoted
            && (_flags.ArityCompat || _flags.LenientBareOperatorOperands);
        if (pk.Kind == TokenKind.Atom && !quotedExempt
            && prim is AtomTerm bareAt && bareAt.Name == pk.Text)
        {
            // A bar atom only ever reaches here QUOTED (the bare bar lexes as
            // TokenKind.Bar); the table decides whether it is an operator, so
            // `op(0, xfy, '|')` lifts the operand restriction with it.
            int p = BareOperatorAtomPriority(bareAt.Name);
            if (p > 0)   // the atom is an operator
            {
                _bareOp = true;
                // builtPrec stays 0: the bare atom's own token priority for
                // subsequent operator application is 0, so `/` still BINDS a
                // `dynamic/1`-shaped indicator — the apply site then rejects
                // it via _bareOp unless a dialect leniency admits it. The ISO
                // §6.3.1.3 rejection rides on _bareOp (the apply sites) and
                // the direct priority throw below.
                if (maxPrec < 999 && p > maxPrec && !_flags.LenientBareOperatorOperands)
                    throw new ParseException(
                        $"Operator '{bareAt.Name}' (priority {p}) needs parentheses "
                        + $"to be an operand here (maximum priority {maxPrec}).", pk.Position);
            }
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
                return new FloatTerm(FiniteFloat(tok, pos)) { Position = pos };

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
                    return ReadCompoundArgs(tok.Text, pos);
                }
                return new AtomTerm(tok.Text) { Position = pos };

            case TokenKind.LParen:
            {
                // A grouping paren re-enables the separators an enclosing
                // argument context suppressed: `f(( a, b ))` is a conjunction.
                bool sc = _suppressComma, sb = _suppressBar;
                _suppressComma = false;
                _suppressBar = false;
                try
                {
                    Term inner = ReadTermInternal(1200, out _);
                    ExpectKind(TokenKind.RParen);
                    // Keep the inner term's own position (more informative than the
                    // paren's) — Position is excluded from value equality anyway.
                    return inner;
                }
                finally { _suppressComma = sc; _suppressBar = sb; }
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
                // parse as ordinary two-element lists. Nor is a bare '!'
                // that immediately closes or continues the list — `[!]`,
                // `[!, X]`, `[! | T]` are ordinary lists with the cut atom as
                // an element (ISO-valid, and used by real libraries such as
                // Scryer's clpz). A real snip `[! Goal !]` always has a goal
                // after the opening '!' — so an INFIX operator right after the
                // '!' also means list, with '!' as its left operand
                // (`[!-1, !-2]`); goals never start with an infix-only token.
                if (PeekToken().Kind == TokenKind.Atom && PeekToken().Text == "!"
                    && !PeekToken().WasQuoted
                    && PeekTokenAt(1).Kind is not (TokenKind.RBracket
                        or TokenKind.Comma or TokenKind.Bar
                        // A goal can start with neither a number ([!-1, …]:
                        // the lexer folds the sign in) …
                        or TokenKind.Integer or TokenKind.Float)
                    // … nor an infix operator taking the '!' as left operand.
                    && !(PeekTokenAt(1).Kind == TokenKind.Atom
                        && _operators.TryGetInfix(PeekTokenAt(1).Text, out _, out _)))
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
                // ISO 6.3.3: the `[]` atom (even spelled `[ ]`) immediately
                // followed by `(` is functional notation — `[ ](X)` is the
                // compound '[]'(X) (Neumerkel #97).
                if (PeekToken().Kind == TokenKind.RBracket
                    && PeekTokenAt(1).Kind == TokenKind.LParen
                    && IsAdjacent(PeekToken(), PeekTokenAt(1)))
                {
                    NextToken();   // ']'
                    NextToken();   // '('
                    var nilArgs = ReadCommaSeparatedArgs(closing: TokenKind.RParen);
                    if (nilArgs.Count == 0)
                        throw new ParseException(
                            "Compound term '[]' requires at least one argument.", pos);
                    return new CompoundTerm("[]", nilArgs.ToArray()) { Position = pos };
                }
                return ReadList(pos);

            case TokenKind.LBrace:
                // arity_compat only — Arity embedded
                // native goal: in a NON-DCG clause a body goal can be raw
                // native code between braces (`p :- g, { C code; }, h.`).
                // The brace content is not Prolog-lexable, so it is
                // skipped RAW by the lexer (naive brace counting) and
                // carried as '$native_goal'(RawText); the consult-time
                // NativeTransform (ADR-022) compiles it for real.
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
                    // ADR-022 — capture the raw C statement text in a
                    // `'$native_goal'(RawText)` term (a non-interned
                    // StringTerm). NativeTransform rewrites it to a compiled
                    // '$native_run' at consult; one that survives to
                    // execution raises loudly (see its registration in
                    // StandardBuiltins) rather than silently succeeding.
                    string nativeText = _lexer.SkipNativeGoalBlock(pos);
                    return new CompoundTerm("$native_goal",
                        new Term[] { new StringTerm(nativeText, Shumway.Core.TextKind.Codes) { Position = pos } })
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
                // `|` in primary position is the bar ATOM only under dialect
                // leniency and only when DELIMITED-AND-CLOSED — the next token
                // is `)` — the `(|)` / `f(|)` shape Scryer's builtins.pl op/3
                // permission-error term relies on. Strict ISO has no bar atom
                // token at all (Neumerkel #356 `{|}`, #360/#361 `(|)`); the
                // list-tail `[H|T]` bar is consumed by list parsing before it
                // reaches here, so this never shadows it.
                if (tok.Kind == TokenKind.Bar
                    && PeekToken().Kind == TokenKind.RParen
                    && (_flags.ArityCompat || _flags.LenientBareOperatorOperands))
                    return new AtomTerm("|") { Position = pos };
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
        // Every text mode produces a StringTerm, which the compiler packs
        // (ADR-047 decision 8). Building the cons list here is what made a
        // literal cost 2n+1 cells; the flag now decides only what the list's
        // ELEMENTS are, and that travels with the datum from here on.
        if (text.Length == 0 && _flags.DoubleQuotes != DoubleQuotesMode.Atom)
            return new AtomTerm("[]") { Position = pos };
        return _flags.DoubleQuotes switch
        {
            DoubleQuotesMode.Codes => new StringTerm(text, Shumway.Core.TextKind.Codes) { Position = pos },
            DoubleQuotesMode.Atom => new AtomTerm(text) { Position = pos },
            // `string` is an SWI compatibility alias for chars, not a type.
            _ => new StringTerm(text, Shumway.Core.TextKind.Chars) { Position = pos },
        };
    }

    // SWI argument mode (LenientArgumentPriority): an argument / list element
    // reads at FULL 1200 priority with the separator tokens suppressed as
    // operators — comma always (it separates), bar only where it marks a list
    // tail. ISO mode reads at 999 as before. The suppression flags are
    // cleared by every bracketing context (parens, braces, a nested arg list
    // re-establishes its own), so `f(( a, b ))` keeps its conjunction.
    private bool _suppressComma;
    private bool _suppressBar;

    private Term ReadArgTerm(bool barIsSeparator)
    {
        if (!_flags.LenientArgumentPriority)
            return ReadTermInternal(999, out _);
        bool savedComma = _suppressComma, savedBar = _suppressBar;
        _suppressComma = true;
        _suppressBar = barIsSeparator;
        try { return ReadTermInternal(1200, out _); }
        finally { _suppressComma = savedComma; _suppressBar = savedBar; }
    }

    /// <summary>Continues an ARGUMENT expression whose leading primary is
    /// already read, under the same priority ceiling and separator
    /// suppression <see cref="ReadArgTerm"/> would have used.</summary>
    private Term ContinueArgTerm(Term left, bool barIsSeparator)
    {
        int builtPrec = 0;
        _bareOp = false;   // a compound is never a bare operator-atom
        if (!_flags.LenientArgumentPriority)
            return ContinueTerm(left, 999, ref builtPrec);
        bool savedComma = _suppressComma, savedBar = _suppressBar;
        _suppressComma = true;
        _suppressBar = barIsSeparator;
        try { return ContinueTerm(left, 1200, ref builtPrec); }
        finally { _suppressComma = savedComma; _suppressBar = savedBar; }
    }

    /// <summary>One compound being read in functional notation: its name,
    /// where it started, and the arguments read so far.</summary>
    private sealed class CompoundFrame
    {
        public string Name = "";
        public SourcePosition Pos;
        public readonly List<Term> Args = new();
    }

    /// <summary>Is the lookahead an atom immediately followed by the
    /// function-call <c>(</c>? That is exactly the test <see
    /// cref="ReadPrimary"/> uses, and ISO §6.4.7 adjacency makes it decisive:
    /// a prefix operator never wins against it.</summary>
    private bool StartsAdjacentCompound()
    {
        Token tok = PeekToken();
        if (tok.Kind != TokenKind.Atom) return false;
        Token next = PeekTokenAt(1);
        return next.Kind == TokenKind.LParen && IsAdjacent(tok, next);
    }

    /// <summary>Reads the argument list of <c>name(</c> — the opening paren
    /// already consumed — and returns the compound.
    ///
    /// <para>ITERATIVE in the one shape that nests without bound: an argument
    /// that is itself a compound in functional notation. <c>write_canonical/1</c>
    /// renders a list as <c>'.'(H, T)</c>, so a canonical ten-thousand-element
    /// list is a ten-thousand-deep nest — and recursing once per level meant
    /// our own output could not be read back: the C# stack overflowed, which
    /// kills the process rather than raising a syntax error. Descending pushes
    /// a frame instead of a stack of parser calls, and a run of closing parens
    /// unwinds them in a loop. Every other shape still recurses, as an
    /// operator-precedence parser does.</para></summary>
    private Term ReadCompoundArgs(string name, SourcePosition pos)
    {
        var frames = new List<CompoundFrame>(8)
        {
            new CompoundFrame { Name = name, Pos = pos },
        };
        while (true)
        {
            while (StartsAdjacentCompound())
            {
                Token head = NextToken();   // the atom
                NextToken();                // the '('
                frames.Add(new CompoundFrame { Name = head.Text, Pos = head.Position });
            }
            CompoundFrame open = frames[^1];
            if (open.Args.Count == 0 && PeekToken().Kind == TokenKind.RParen)
                throw new ParseException(
                    $"Compound term '{open.Name}' requires at least one argument; "
                    + "for the zero-arity case use the bare atom.", open.Pos);

            Term arg = ReadArgTerm(barIsSeparator: false);

            // Attach, then close as many frames as the closing parens ask for.
            while (true)
            {
                frames[^1].Args.Add(arg);
                if (PeekToken().Kind == TokenKind.Comma)
                {
                    NextToken();
                    // Arity tolerates a dangling comma before the closing
                    // paren; anything else starts another argument.
                    if (!(_flags.ArityCompat && PeekToken().Kind == TokenKind.RParen))
                        break;
                }
                ExpectKind(TokenKind.RParen);
                CompoundFrame done = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                Term compound = new CompoundTerm(done.Name, done.Args.ToArray())
                {
                    Position = done.Pos,
                };
                if (frames.Count == 0) return compound;
                // The enclosing argument may continue past the compound:
                // `f(g(1) + 2)`.
                arg = ContinueArgTerm(compound, barIsSeparator: false);
            }
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
        args.Add(ReadArgTerm(barIsSeparator: false));
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
            args.Add(ReadArgTerm(barIsSeparator: false));
        }
        ExpectKind(closing);
        return args;
    }

    private Term ReadList(SourcePosition pos)
    {
        if (PeekToken().Kind == TokenKind.RBracket)
        {
            Token rb = NextToken();
            // SWI zero-name compound `[](Args)` (hashtable.pl's bucket
            // arrays): `[]` immediately followed by '(' heads a compound,
            // exactly like the ISO `{}(X)` form below. Lenient-scope only.
            if (_flags.LenientArgumentPriority
                && PeekToken().Kind == TokenKind.LParen
                && IsAdjacent(rb, PeekToken()))
            {
                NextToken();   // consume '('
                var cargs = ReadCommaSeparatedArgs(closing: TokenKind.RParen);
                if (cargs.Count > 0)
                    return new CompoundTerm("[]", cargs.ToArray()) { Position = pos };
            }
            return new AtomTerm("[]") { Position = pos };
        }

        var elements = new List<Term>();
        elements.Add(ReadArgTerm(barIsSeparator: true));
        while (PeekToken().Kind == TokenKind.Comma)
        {
            NextToken();
            elements.Add(ReadArgTerm(barIsSeparator: true));
        }

        Term tail;
        if (PeekToken().Kind == TokenKind.Bar)
        {
            NextToken();
            tail = ReadArgTerm(barIsSeparator: true);
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
        // Braces re-enable suppressed separators, like grouping parens.
        bool sc = _suppressComma, sb = _suppressBar;
        _suppressComma = false;
        _suppressBar = false;
        try
        {
            Term inner = ReadTermInternal(1200, out _);
            ExpectKind(TokenKind.RBrace);
            return new CompoundTerm("{}", new[] { inner }) { Position = pos };
        }
        finally { _suppressComma = sc; _suppressBar = sb; }
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

    /// <summary>A float literal past double range lexes as an infinity (the
    /// lexer keeps both frameworks identical); the SYNTAX is perfect, so it is
    /// not a syntax error — it names a value outside the float range, which
    /// only a representation_error can report (Neumerkel number_chars #82 /
    /// stc #74). The SIGN follows the syntax: a UNARY minus makes the literal
    /// itself negative, below min_float; everywhere else the positive literal
    /// is above max_float — so `-9.9e999` is min_float while `0-9.9e999` is
    /// max_float (the minus there is binary and the literal is positive).
    /// The flaw rides on the exception for the ISO-error carriers to honour.</summary>
    private static double FiniteFloat(Token tok, SourcePosition pos, bool negated = false)
        => double.IsFinite(tok.FloatValue)
            ? tok.FloatValue
            : throw new ParseException(
                negated
                    ? $"float literal '-{tok.Text}' is below min_float."
                    : $"float literal '{tok.Text}' is above max_float.", pos)
                { RepresentationFlaw = negated ? "min_float" : "max_float" };

    private static string DescribeToken(Token tok) => tok.Kind switch
    {
        TokenKind.Eof => "end of input",
        TokenKind.Atom or TokenKind.Variable or TokenKind.String => $"{tok.Kind} '{tok.Text}'",
        TokenKind.Integer => $"integer {tok.IntValue}",
        TokenKind.Float => $"float {tok.FloatValue}",
        _ => $"{tok.Kind} '{tok.Text}'",
    };
}
