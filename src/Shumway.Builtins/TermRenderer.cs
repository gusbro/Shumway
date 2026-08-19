using System.Globalization;
using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Walks a heap cell and writes its Prolog source representation to a
/// <see cref="TextWriter"/>. Used by <see cref="IOBuiltins"/> for
/// <c>write/1</c> and friends; also handy for debugging from C#.
///
/// <para>Integers render in base 10, floats in round-trippable Prolog form,
/// compound terms in operator form when an operator table is supplied (else
/// <c>functor(arg, arg)</c>), cons-chains as bracketed lists <c>[a, b, c]</c>
/// (or <c>[a, b | T]</c> for partial / improper lists), and atoms quoted per
/// <see cref="TermRenderOptions.Quoted"/>. Unbound variables render as
/// <c>_Gn</c> with their heap index — matching the convention used by
/// <c>TermReader</c> in the embedding layer.</para>
/// </summary>
public static class TermRenderer
{
    public static void Render(Activation engine, Cell cell, TextWriter output)
        => Render(engine, cell, output, TermRenderOptions.Default);

    public static void Render(Activation engine, Cell cell, TextWriter output, TermRenderOptions options)
        => Render(engine, cell, output, options, maxPriority: 1200);

    /// <summary>Renders <paramref name="cell"/> bounded by
    /// <paramref name="maxPriority"/>: if the term itself is an operator
    /// of higher priority it gets wrapped in parens so the result is
    /// re-parseable. Top-level callers pass 1200 (no bound).</summary>
    public static void Render(Activation engine, Cell cell, TextWriter output,
        TermRenderOptions options, int maxPriority)
    {
        int derefAddr = Resolve(engine, ref cell);

        // portrayed(true): the user's portray/1 gets first shot at every
        // subterm; on success its output IS the rendering.
        if (options.Portray is { } portray
            && cell.Tag is not (Tag.Ref or Tag.AttVar)
            && portray(engine, cell, output))
            return;

        switch (cell.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                // An attributed variable is still an unbound variable —
                // it renders exactly like a plain one. Its attributes
                // are not part of its written form.
                if (options.VariableNames is not null
                    && options.VariableNames.TryGetValue(derefAddr, out string? vName))
                {
                    output.Write(vName);
                    break;
                }
                output.Write("_G");
                output.Write(derefAddr.ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.Atom:
                WriteAtomName(NameOfAtom(cell.AsAtomId), output, options);
                break;
            case Tag.Int:
                output.Write(cell.AsInt.ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.BigInt:
                output.Write(engine.AsBigInt(cell).ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.Rational:
            {
                // Rendered as the operator term `Num rdiv Den` (re-readable —
                // `rdiv` is a 400,yfx operator that re-evaluates to the value).
                // Parenthesise where an enclosing operator's priority forbids
                // a 400-priority operand.
                var r = engine.AsRational(cell);
                bool paren = maxPriority < 400;
                if (paren) output.Write('(');
                output.Write(r.Num.ToString(CultureInfo.InvariantCulture));
                output.Write(" rdiv ");
                output.Write(r.Den.ToString(CultureInfo.InvariantCulture));
                if (paren) output.Write(')');
                break;
            }
            case Tag.Float:
            {
                double v = Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex));
                output.Write(Number.FormatPrologFloat(v));
                break;
            }
            case Tag.Str:
                if (options.MaxDepth > 0)
                {
                    if (options.CurrentDepth >= options.MaxDepth)
                    {
                        output.Write("...");
                        break;
                    }
                    options.CurrentDepth++;
                    try { RenderCompound(engine, cell, output, options, maxPriority); }
                    finally { options.CurrentDepth--; }
                    break;
                }
                RenderCompound(engine, cell, output, options, maxPriority);
                break;
            case Tag.Lis:
                if (options.MaxDepth > 0)
                {
                    if (options.CurrentDepth >= options.MaxDepth)
                    {
                        output.Write("...");
                        break;
                    }
                    options.CurrentDepth++;
                    try { RenderList(engine, cell, output, options); }
                    finally { options.CurrentDepth--; }
                    break;
                }
                RenderList(engine, cell, output, options);
                break;
            case Tag.Pstr:
                output.Write('"');
                // Read the chain from the CELL, not from a heap index: an
                // element taken straight out of a list arrives already
                // dereferenced (derefAddr == -1), and AsPstrString's
                // index-based entry point cannot be used then.
                output.Write(engine.ReadPstrChain(cell, out _));
                output.Write('"');
                break;
            default:
                output.Write('<');
                output.Write(cell.Tag.ToString());
                output.Write('>');
                break;
        }
    }

    private static int Resolve(Activation engine, ref Cell cell)
    {
        // A bare ATTVAR cell carries its own home index as payload —
        // surface that as the deref address and leave the AttVar-tagged
        // cell for the caller's switch.
        if (cell.Tag == Tag.AttVar) return cell.AsHeapIndex;
        if (cell.Tag != Tag.Ref) return -1;
        int addr = engine.Deref(cell.AsHeapIndex);
        cell = engine.GetHeap(addr);
        return addr;
    }

    private static void RenderCompound(Activation engine, Cell strCell, TextWriter output, TermRenderOptions options, int maxPriority)
    {
        int functorIdx = strCell.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = NameOfAtom(atomId);

        // numbervars(true): '$VAR'(N) renders as letter sequence A, B, ..., Z, A1, B1, ...
        if (options.Numbervars && arity == 1 && name == "$VAR")
        {
            Cell nCell = engine.GetHeap(functorIdx + 1);
            Resolve(engine, ref nCell);
            if (nCell.Tag == Tag.Int)
            {
                long n = nCell.AsInt;
                if (n >= 0)
                {
                    output.Write(NumbervarsName(n));
                    return;
                }
            }
        }

        // Curly-brace notation: '{}'(Body) is {Body}. UNLIKE list notation it
        // does NOT survive ignore_ops(true) — write_canonical prints the
        // functional {}(Body) (SWI and Scryer agree; Scryer conformity test
        // 96). The braces bracket the body, so it renders at full priority.
        if (arity == 1 && name == "{}" && !options.IgnoreOps)
        {
            output.Write('{');
            Render(engine, engine.GetHeap(functorIdx + 1), output, options, 1200);
            output.Write('}');
            return;
        }

        // Operator-form rendering: consult the lookup table if enabled. The
        // priority bound is applied symmetrically — if the term's own
        // operator priority exceeds maxPriority we wrap in parens and
        // recurse with a fresh ceiling.
        if (!options.IgnoreOps && options.Operators is not null)
        {
            if (arity == 2 && options.Operators.TryGetInfix(name, out int infixPrec, out OperatorShape infixShape))
            {
                bool needsParens = infixPrec > maxPriority;
                if (needsParens) output.Write('(');
                int leftMax = infixShape switch
                {
                    OperatorShape.Yfx => infixPrec,       // left can be same prec
                    OperatorShape.Xfx or OperatorShape.Xfy => infixPrec - 1,
                    _ => infixPrec - 1,
                };
                int rightMax = infixShape switch
                {
                    OperatorShape.Xfy => infixPrec,        // right can be same prec
                    OperatorShape.Xfx or OperatorShape.Yfx => infixPrec - 1,
                    _ => infixPrec - 1,
                };
                // A y-LEFT operand of equal priority that is open on the right
                // must be parenthesised — `yfx(fy(1),2)` prints `(fy 1)yfx 2`
                // (Neumerkel #153); its bare text would re-read differently.
                bool leftOpenParens = infixShape == OperatorShape.Yfx
                    && OperandOpenRightAt(
                        engine, engine.GetHeap(functorIdx + 1), infixPrec, options);
                // The `,` operator renders tight and unquoted — `a,b`, not
                // `a , b` or the quoted `a ',' b`. An operator in operator
                // position is written raw when its name is a valid bare token
                // there (quoting `,` / `|` would be wrong); a name that is NOT
                // (the empty atom `''` as an operator — Neumerkel #119-family
                // op(100,xfx,'') — or one with layout in it) must be quoted or
                // the output is unreadable (and `itext[0]` on "" crashed).
                string itext = (name == "," || name == "|" || NeedsNoQuoting(name))
                    ? name : QuotedAtomName(name);
                if (options.TightSymbolicOperators)
                {
                    // Fuse-aware spacing for EVERY infix operator: adjacent
                    // tokens of the same character class fuse on re-read
                    // (`1=\\` lexes `=\\` as one atom; `1 yfx 2` needs both
                    // spaces, `(fy 1)yfx 2` neither side of the paren). Render
                    // the operands to temp writers so we know their edge
                    // chars, and insert a space ONLY where the operator would
                    // fuse with an operand — EXCEPT (symbolic operators only)
                    // when the operand is an unbound variable: its
                    // variable_names name (or _Gn form) is written verbatim
                    // per §7.10.5, and Neumerkel does not space it
                    // (`1+/*r*/V`, not `1+ /*r*/V`).
                    bool symbolic = IsSymbolicName(name);
                    bool leftVar = symbolic && IsUnboundVarCell(engine, engine.GetHeap(functorIdx + 1));
                    bool rightVar = symbolic && IsUnboundVarCell(engine, engine.GetHeap(functorIdx + 2));
                    var lw = new StringWriter();
                    if (leftOpenParens)
                    {
                        lw.Write('(');
                        Render(engine, engine.GetHeap(functorIdx + 1), lw, options, 1200);
                        lw.Write(')');
                    }
                    else
                        RenderOperand(engine, engine.GetHeap(functorIdx + 1), lw, options, leftMax);
                    var rw = new StringWriter();
                    RenderOperand(engine, engine.GetHeap(functorIdx + 2), rw, options, rightMax);
                    string ls = lw.ToString(), rs = rw.ToString();
                    output.Write(ls);
                    if (ls.Length > 0 && ((!leftVar && CharsFuse(ls[^1], itext[0]))
                                          || ZeroThenQuote(ls, itext)))
                        output.Write(' ');
                    output.Write(itext);
                    if (rs.Length > 0 && !rightVar && CharsFuse(itext[^1], rs[0])) output.Write(' ');
                    output.Write(rs);
                }
                else
                {
                    if (leftOpenParens)
                    {
                        output.Write('(');
                        Render(engine, engine.GetHeap(functorIdx + 1), output, options, 1200);
                        output.Write(')');
                    }
                    else
                        RenderOperand(engine, engine.GetHeap(functorIdx + 1), output, options, leftMax);
                    output.Write(' ');
                    output.Write(itext);
                    output.Write(' ');
                    RenderOperand(engine, engine.GetHeap(functorIdx + 2), output, options, rightMax);
                }
                if (needsParens) output.Write(')');
                return;
            }
            if (arity == 1 && options.Operators.TryGetPrefix(name, out int prefixPrec, out OperatorShape prefixShape))
            {
                bool needsParens = prefixPrec > maxPriority;
                // ISO writeq: prefix `-` applied to a term whose leftmost token
                // is a non-negative number must parenthesise THE OPERAND —
                // `- 1` reads back as the negative-number literal -1, not the
                // compound -(1). `+`/`\` have no such literal, so only `-`.
                bool operandParens =
                    (name == "-"
                     && RendersLeadingDigit(engine, engine.GetHeap(functorIdx + 1), options))
                    || IsBareOperatorAtomCell(engine, engine.GetHeap(functorIdx + 1), options)
                    // Neumerkel vn #43: prefix `-` applied to an operand that
                    // is a LEFT-CLOSED operator of EQUAL priority is
                    // parenthesised — `- X^2` → `- (X^2)` (`-` fy 200, `^` xfy
                    // 200). ONLY for `-`: Scryer's conformity prints
                    // `+ (1*2)^3` bare (both re-read fine; SICStus-lineage
                    // parenthesises the `-` family for the negative-literal
                    // hazard's sake, and vn #43 pins that). The >-priority
                    // case is already parenthesised by argMax below.
                    || (name == "-"
                        && OperandIsOperatorPriorityAtLeast(
                               engine, engine.GetHeap(functorIdx + 1), prefixPrec, options));
                if (needsParens) output.Write('(');
                string prefixText = (!options.Quoted || NeedsNoQuoting(name))
                    ? name : QuotedAtomName(name);
                output.Write(prefixText);
                int argMax = prefixShape == OperatorShape.Fy ? prefixPrec : prefixPrec - 1;
                var opw = new StringWriter();
                if (operandParens) opw.Write('(');
                Render(engine, engine.GetHeap(functorIdx + 1), opw, options,
                    operandParens ? 1200 : argMax);
                if (operandParens) opw.Write(')');
                string os = opw.ToString();
                // Space only where the tokens would otherwise fuse — `fy 1`
                // but `--a` / `' op'[]` / `-A` (Neumerkel #274/#133/#279) —
                // plus ALWAYS before a parenthesised operand: `fy(...)` would
                // re-read as FUNCTIONAL notation, a different term
                // (`fy (fy 1)yf` vs `fy(fy 1)yf`, #319; `- (1)`, `- (X^2)`).
                bool prefixVar = IsSymbolicName(name)
                    && IsUnboundVarCell(engine, engine.GetHeap(functorIdx + 1));
                if (os.Length > 0
                    && ((!prefixVar && CharsFuse(prefixText[^1], os[0]))
                        || os[0] == '('))
                    output.Write(' ');
                output.Write(os);
                if (needsParens) output.Write(')');
                return;
            }
            if (arity == 1 && options.Operators.TryGetPostfix(name, out int postPrec, out OperatorShape postShape))
            {
                bool needsParens = postPrec > maxPriority;
                if (needsParens) output.Write('(');
                int argMax = postShape == OperatorShape.Yf ? postPrec : postPrec - 1;
                // A y-LEFT operand of equal priority that is open on the right
                // must be parenthesised — yf(fy(1)) prints `(fy 1)yf`
                // (Neumerkel #150/#156/#319).
                bool postOpenParens = postShape == OperatorShape.Yf
                    && OperandOpenRightAt(
                        engine, engine.GetHeap(functorIdx + 1), postPrec, options);
                string postText = (!options.Quoted || NeedsNoQuoting(name))
                    ? name : QuotedAtomName(name);
                if (options.TightSymbolicOperators)
                {
                    // Fuse-aware spacing (as for infix): `1 yf` needs the
                    // space, `(fy 1)yf` / `-1'$VAR'` do not (Neumerkel
                    // #149/#150/#355). Variable operands stay verbatim under
                    // a symbolic operator.
                    bool postVar = IsSymbolicName(name)
                        && IsUnboundVarCell(engine, engine.GetHeap(functorIdx + 1));
                    var pw = new StringWriter();
                    if (postOpenParens)
                    {
                        pw.Write('(');
                        Render(engine, engine.GetHeap(functorIdx + 1), pw, options, 1200);
                        pw.Write(')');
                    }
                    else
                        RenderOperand(engine, engine.GetHeap(functorIdx + 1), pw, options, argMax);
                    string ps = pw.ToString();
                    output.Write(ps);
                    if (ps.Length > 0
                        && ((!postVar && CharsFuse(ps[^1], postText[0]))
                            || ZeroThenQuote(ps, postText)))
                        output.Write(' ');
                    output.Write(postText);
                }
                else
                {
                    if (postOpenParens)
                    {
                        output.Write('(');
                        Render(engine, engine.GetHeap(functorIdx + 1), output, options, 1200);
                        output.Write(')');
                    }
                    else
                        RenderOperand(engine, engine.GetHeap(functorIdx + 1), output, options, argMax);
                    output.Write(' ');
                    output.Write(postText);
                }
                if (needsParens) output.Write(')');
                return;
            }
        }

        WriteAtomName(name, output, options);
        if (arity == 0) return;
        output.Write('(');
        for (int i = 0; i < arity; i++)
        {
            if (i > 0) output.Write(',');   // ISO: no layout between args
            // Inside argument lists, comma is precedence 1000 in standard
            // Prolog so each arg can carry up to 999 priority without parens.
            Render(engine, engine.GetHeap(functorIdx + 1 + i), output, options, 999);
        }
        output.Write(')');
    }

    private static void RenderList(Activation engine, Cell lisCell, TextWriter output, TermRenderOptions options)
    {
        if (options.IgnoreOps)
        {
            // ISO §7.10.5 canonical form (write_canonical): a list is the
            // compound '.'(H, T) and ignore_ops means FUNCTIONAL notation —
            // `'.'(a,[])`, not `[a]` (Neumerkel #34). Iterative over the
            // spine — don't recurse per element: a deep list overflows the
            // C# stack (same rule as the term materializers).
            int depth = 0;
            Cell cur = lisCell;
            while (true)
            {
                Resolve(engine, ref cur);
                if (cur.Tag != Tag.Lis) break;
                output.Write("'.'(");
                Render(engine, engine.GetHeap(cur.AsHeapIndex), output, options, 999);
                output.Write(',');
                depth++;
                cur = engine.GetHeap(cur.AsHeapIndex + 1);
            }
            Render(engine, cur, output, options, 999);
            for (int i = 0; i < depth; i++) output.Write(')');
            return;
        }
        output.Write('[');
        bool first = true;
        Cell cursor = lisCell;
        // max_depth: the ENTRY cons already consumed one level (the Render
        // gate incremented); every further cons consumes another. When the
        // budget runs out the tail elides to `|...`.
        int consDepth = options.CurrentDepth;
        while (true)
        {
            Resolve(engine, ref cursor);
            if (cursor.Tag != Tag.Lis) break;
            if (!first)
            {
                consDepth++;
                if (options.MaxDepth > 0 && consDepth > options.MaxDepth)
                {
                    output.Write("|...]");
                    return;
                }
                output.Write(',');   // ISO: no layout between elements
            }
            int headIdx = cursor.AsHeapIndex;
            // Each element is an argument-priority (999) position: a ','/2
            // element must parenthesise (`[(a,b)]`, not `[a,b]` — which would
            // re-read as a two-element list), as must any operator ≥ 1000.
            // Plain Render, NOT RenderOperand: an ATOM that is an operator is
            // a legal argument bare (ISO §6.3.3 — `[:-,-]`, not `[(:-),(-)]`);
            // the operand-position parens are only for operator operands.
            Render(engine, engine.GetHeap(headIdx), output, options, 999);
            cursor = engine.GetHeap(headIdx + 1);
            first = false;
        }
        // Cursor is now whatever the tail dereffed to.
        Resolve(engine, ref cursor);
        if (cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId)
        {
            // Proper list — clean close.
        }
        else
        {
            output.Write('|');   // ISO: compact improper-list tail
            Render(engine, cursor, output, options, 999);   // arg-priority tail
        }
        output.Write(']');
    }

    /// <summary>Writes an atom name with quoting applied when
    /// <paramref name="options"/>.<c>Quoted</c> is set and the name
    /// isn't a plain alphanumeric identifier. The rule is conservative:
    /// any name that starts with a non-letter, contains a non-identifier
    /// character, or is the empty string gets single-quoted.</summary>
    private static void WriteAtomName(string name, TextWriter output, TermRenderOptions options)
    {
        if (!options.Quoted || NeedsNoQuoting(name))
        {
            output.Write(name);
            return;
        }
        output.Write('\'');
        foreach (char c in name)
        {
            string? esc = EscapeQuotedChar(c);
            if (esc is not null) output.Write(esc);
            else output.Write(c);
        }
        output.Write('\'');
    }

    /// <summary>The writeq/write_canonical escape sequence for a character
    /// inside a single-quoted atom, or <c>null</c> when the character is
    /// written literally. ISO §6.3.7: the quote and backslash are escaped,
    /// the named control characters use their letter escapes, and any other
    /// control / DEL character uses the <c>\xHH\</c> hexadecimal form — so a
    /// quoted atom carrying a newline round-trips through <c>read/1</c>
    /// instead of embedding a raw control byte.</summary>
    private static string? EscapeQuotedChar(char c) => c switch
    {
        '\'' => "\\'",
        '\\' => "\\\\",
        '\a' => "\\a",
        '\b' => "\\b",
        '\t' => "\\t",
        '\n' => "\\n",
        '\v' => "\\v",
        '\f' => "\\f",
        '\r' => "\\r",
        _ when c < ' ' || c == '\x7f'
            => "\\x" + ((int)c).ToString("x", CultureInfo.InvariantCulture) + "\\",
        _ => null,
    };

    /// <summary>The writeq-style form of an atom name: single-quoted (with
    /// <c>'</c> and <c>\</c> escaped) unless it needs no quoting. Shared with the
    /// debugger's AST renderer (ADR-035), whose Locals display must round-trip
    /// through the Watch-window EDIT: showing the atom <c>'1234'</c> as bare
    /// <c>1234</c> would make the user's re-typed value an INTEGER.</summary>
    public static string QuotedAtomName(string name)
    {
        if (NeedsNoQuoting(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 2);
        sb.Append('\'');
        foreach (char c in name)
        {
            string? esc = EscapeQuotedChar(c);
            if (esc is not null) sb.Append(esc);
            else sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>A name needs no quoting if it's a non-empty sequence of
    /// alphanumeric / underscore characters starting with a lowercase
    /// letter, a solo punctuation atom (<c>[]</c> / <c>{}</c> / <c>,</c>
    /// / <c>!</c> / <c>;</c>), OR an all-symbolic atom — a non-empty run
    /// of the ISO "graphic" characters (<c>+ - * / \ ^ &lt; &gt; = ~ :
    /// . ? @ # &amp; $</c>). The last case is what lets symbolic
    /// operators like <c>/</c> and <c>+</c> print unquoted under
    /// <c>quoted(true)</c> — quoting them (<c>'/'</c>) is wrong and
    /// breaks SWI-compatible round-tripping (term_to_atom/2).</summary>
    private static bool NeedsNoQuoting(string name)
    {
        if (name.Length == 0) return false;
        // ',' and '.' as solo atoms MUST be quoted by writeq / write_canonical:
        // a bare ',' is the argument/list separator and a bare '.' is the
        // end-of-clause token, so neither round-trips unquoted (SWI / GProlog:
        // writeq(',') => ','  and  writeq('.') => '.').
        if (name == "," || name == ".") return false;
        if (name == "[]" || name == "{}" || name == "!"
            || name == ";") return true;
        char first = name[0];
        if (char.IsLower(first))
        {
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }
        // All-symbolic atom: every character is an ISO graphic char.
        if (IsSymbolChar(first))
        {
            // …but a name that OPENS a block comment (`/*`) is consumed as a
            // comment when written bare, so it must be quoted to round-trip.
            // (`*/`, `//*` etc. do not open a comment and stay bare.)
            if (name.StartsWith("/*", System.StringComparison.Ordinal)) return false;
            for (int i = 1; i < name.Length; i++)
                if (!IsSymbolChar(name[i])) return false;
            return true;
        }
        return false;
    }

    /// <summary>Renders an operator's operand, parenthesising it when it is a
    /// bare operator-atom — `-(-,-)` writes as `(-)-(-)`, not `- - -` (which
    /// the reader rejects: ISO §6.3.1.3 forbids a bare operator-atom as an
    /// operand). Otherwise defers to the priority-driven <see cref="Render"/>.</summary>
    private static void RenderOperand(
        Activation engine, Cell cell, TextWriter output, TermRenderOptions options, int maxPrio)
    {
        if (IsBareOperatorAtomCell(engine, cell, options))
        {
            output.Write('(');
            Render(engine, cell, output, options, 1200);
            output.Write(')');
        }
        else
        {
            Render(engine, cell, output, options, maxPrio);
        }
    }

    /// <summary>True when <paramref name="cell"/> is an atom that is a defined
    /// prefix / infix / postfix operator (so, as a bare operand, it must be
    /// parenthesised to round-trip).</summary>
    private static bool IsBareOperatorAtomCell(
        Activation engine, Cell cell, TermRenderOptions options)
    {
        if (options.IgnoreOps || options.Operators is null) return false;
        if (cell.Tag == Tag.Ref) cell = engine.GetHeap(engine.Deref(cell.AsHeapIndex));
        if (cell.Tag != Tag.Atom) return false;
        string name = NameOfAtom(cell.AsAtomId);
        return options.Operators.TryGetPrefix(name, out _, out _)
            || options.Operators.TryGetInfix(name, out _, out _)
            || options.Operators.TryGetPostfix(name, out _, out _);
    }

    /// <summary>True when <paramref name="cell"/> renders with a leading
    /// decimal digit — i.e. its leftmost token is a non-negative number. Used
    /// to decide whether the operand of a prefix <c>-</c> must be
    /// parenthesised (so it does not fuse into a negative-number literal).
    /// Descends the left spine of an infix operator and through a postfix
    /// operand; a functional compound, a list, an atom or a negative number
    /// does not start with a digit.</summary>
    private static bool RendersLeadingDigit(
        Activation engine, Cell cell, TermRenderOptions options)
    {
        if (cell.Tag == Tag.Ref)
        {
            int addr = engine.Deref(cell.AsHeapIndex);
            cell = engine.GetHeap(addr);
        }
        switch (cell.Tag)
        {
            case Tag.Int:
                return cell.AsInt >= 0;
            case Tag.BigInt:
                return engine.AsBigInt(cell).Sign >= 0;
            case Tag.Rational:
                // Renders as `Num rdiv Den`; leads with Num's sign.
                return engine.AsRational(cell).Num.Sign >= 0;
            case Tag.Float:
                return Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex)) >= 0;
            case Tag.Str:
            {
                if (options.IgnoreOps || options.Operators is null) return false;
                int fIdx = cell.AsHeapIndex;
                var (atomId, ar) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
                string fname = AtomTable.GetById(atomId)?.Name ?? "";
                // Under numbervars a '$VAR'(N≥0) renders as a LETTER, not the
                // digit payload — `- '$VAR'(0)` is `-A` (Neumerkel #279), even
                // when '$VAR' is also a registered operator.
                if (options.Numbervars && ar == 1 && fname == "$VAR")
                {
                    Cell nCell = engine.GetHeap(fIdx + 1);
                    Resolve(engine, ref nCell);
                    if (nCell.Tag == Tag.Int && nCell.AsInt >= 0) return false;
                }
                if (ar == 2 && options.Operators.TryGetInfix(fname, out _, out _))
                    return RendersLeadingDigit(engine, engine.GetHeap(fIdx + 1), options);
                if (ar == 1 && options.Operators.TryGetPostfix(fname, out _, out _))
                    return RendersLeadingDigit(engine, engine.GetHeap(fIdx + 1), options);
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>ISO §6.4.2 "graphic char" set — the characters a
    /// symbolic (un-quoted) atom like <c>/</c>, <c>=..</c> or
    /// <c>:-</c> is built from.</summary>
    private static bool IsSymbolChar(char c)
        => "+-*/\\^<>=~:.?@#&$".IndexOf(c) >= 0;

    /// <summary>True when the operand text ends with the lone integer token
    /// <c>0</c> and the following operator token opens with a quote — glued,
    /// <c>0'…</c> would re-lex as a character-code literal, so a space is
    /// required (Neumerkel #208 <c>0 'f '</c>); <c>-1'$VAR'</c> stays tight
    /// (#355, only <c>0'</c> is special).</summary>
    private static bool ZeroThenQuote(string left, string opText)
        => opText.Length > 0 && opText[0] == '\''
        && left.Length > 0 && left[^1] == '0'
        && (left.Length == 1
            || !(char.IsLetterOrDigit(left[^2]) || left[^2] == '_'));

    /// <summary>True when two adjacent output characters would lex as a single
    /// token — both ISO graphic (symbol) chars (<c>=</c> then <c>\</c> → the one
    /// atom <c>=\</c>), or both alphanumeric / underscore (an identifier / number
    /// run). Used to decide whether a tight (space-free) operator needs a
    /// separating space from an operand so writeq round-trips.</summary>
    private static bool CharsFuse(char a, char b)
        => (IsSymbolChar(a) && IsSymbolChar(b))
        || ((char.IsLetterOrDigit(a) || a == '_') && (char.IsLetterOrDigit(b) || b == '_'))
        // A closing quote met by an opening quote reads as a DOUBLED quote
        // inside one token — `'.'' '` is the single atom `.' ` — so quoted
        // tokens never touch (Neumerkel #333 `'.' ' '`).
        || (a == '\'' && b == '\'')
        || (a == '"' && b == '"');

    /// <summary>True when <paramref name="cell"/> dereferences to an unbound
    /// variable — its written form (a variable_names name, or the <c>_Gn</c>
    /// fallback) is verbatim, so the tight-operator fusion spacing must not
    /// apply to it.</summary>
    private static bool IsUnboundVarCell(Activation engine, Cell cell)
    {
        Resolve(engine, ref cell);
        return cell.Tag is Tag.Ref or Tag.AttVar;
    }

    /// <summary>True when <paramref name="cell"/> is a compound whose principal
    /// functor is a defined infix (arity 2) or postfix (arity 1) operator of
    /// priority &gt;= <paramref name="threshold"/>. Used to parenthesise a prefix
    /// operator's operand when the operand is an operator of equal-or-higher
    /// priority (Neumerkel writeq conformity).</summary>
    private static bool OperandIsOperatorPriorityAtLeast(
        Activation engine, Cell cell, int threshold, TermRenderOptions options)
    {
        if (options.IgnoreOps || options.Operators is null) return false;
        Resolve(engine, ref cell);
        if (cell.Tag != Tag.Str) return false;
        int fidx = cell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(engine.GetHeap(fidx).AsFunctorId);
        string nm = NameOfAtom(atomId);
        // At EQUAL priority a fy operand needs parens only when it is
        // LEFT-CLOSED (its left position is x): `- (X^2)` (vn #43, ^ xfy),
        // but `fy 1 yf` / `fy 1 yfx 2` stay bare (Neumerkel #149/#152 —
        // yf/yfx have a y left position, so the reader rebuilds them).
        if (arity == 2 && options.Operators.TryGetInfix(nm, out int ip, out OperatorShape ish))
            return ip > threshold
                || (ip == threshold
                    && ish is OperatorShape.Xfy or OperatorShape.Xfx);
        if (arity == 1 && options.Operators.TryGetPostfix(nm, out int pp, out OperatorShape psh))
            return pp > threshold
                || (pp == threshold && psh is OperatorShape.Xf);
        return false;
    }

    /// <summary>True when <paramref name="cell"/> is an operator term of
    /// priority exactly <paramref name="prec"/> that is OPEN ON THE RIGHT at
    /// that priority — a prefix fy term or an infix xfy term. Rendered bare in
    /// a y-LEFT operand position, the following operator token would bind
    /// INSIDE it on re-read: `fy 1 yf` reads as fy(yf(1)), so yf(fy(1)) must
    /// print `(fy 1)yf` (Neumerkel #150/#153/#156/#319).</summary>
    private static bool OperandOpenRightAt(
        Activation engine, Cell cell, int prec, TermRenderOptions options)
    {
        if (options.IgnoreOps || options.Operators is null) return false;
        Resolve(engine, ref cell);
        if (cell.Tag != Tag.Str) return false;
        int fidx = cell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(engine.GetHeap(fidx).AsFunctorId);
        string nm = NameOfAtom(atomId);
        if (arity == 1 && options.Operators.TryGetPrefix(nm, out int pp, out OperatorShape psh))
            return pp == prec && psh == OperatorShape.Fy;
        if (arity == 2 && options.Operators.TryGetInfix(nm, out int ip, out OperatorShape ish))
            return ip == prec && ish == OperatorShape.Xfy;
        return false;
    }

    /// <summary>True when every character of <paramref name="name"/> is
    /// an ISO graphic char — a symbolic operator like <c>/</c> or
    /// <c>=..</c> as opposed to an alphabetic one like <c>is</c> /
    /// <c>mod</c>. Used to decide tight (space-free) operator spacing.</summary>
    private static bool IsSymbolicName(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name)
            if (!IsSymbolChar(c)) return false;
        return true;
    }

    /// <summary>Converts <c>'$VAR'(N)</c>'s integer payload into the
    /// ISO-standard alphabetic variable name: 0 → A, 1 → B, …, 25 → Z,
    /// 26 → A1, 27 → B1, etc.</summary>
    private static string NumbervarsName(long n)
    {
        char letter = (char)('A' + n % 26);
        long suffix = n / 26;
        return suffix == 0
            ? letter.ToString()
            : letter.ToString() + suffix.ToString(CultureInfo.InvariantCulture);
    }

    private static string NameOfAtom(int id)
    {
        var atom = AtomTable.GetById(id);
        return atom?.Name ?? $"<atom-{id}>";
    }
}
