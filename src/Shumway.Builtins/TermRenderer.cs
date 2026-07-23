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
            case Tag.Float:
            {
                double v = Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex));
                output.Write(Number.FormatPrologFloat(v));
                break;
            }
            case Tag.Str:
                RenderCompound(engine, cell, output, options, maxPriority);
                break;
            case Tag.Lis:
                RenderList(engine, cell, output, options);
                break;
            case Tag.Pstr:
                output.Write('"');
                output.Write(engine.AsPstrString(derefAddr));
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
                // The `,` operator renders tight and unquoted — `a,b`, not
                // `a , b` or the quoted `a ',' b`. An operator in operator
                // position is always written raw (unquoted): it is a valid
                // token there, so quoting `,` / `|` would be wrong.
                bool tight = options.TightSymbolicOperators
                    && (IsSymbolicName(name) || name == ",");
                Render(engine, engine.GetHeap(functorIdx + 1), output, options, leftMax);
                if (!tight) output.Write(' ');
                output.Write(name);
                if (!tight) output.Write(' ');
                Render(engine, engine.GetHeap(functorIdx + 2), output, options, rightMax);
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
                bool operandParens = name == "-"
                    && RendersLeadingDigit(engine, engine.GetHeap(functorIdx + 1), options);
                if (needsParens) output.Write('(');
                WriteAtomName(name, output, options);
                // Even in tight mode a prefix symbolic operator needs a
                // space before a numeric / symbolic argument, else
                // `- 1` would fuse into the negative literal `-1` and
                // `- (-a)` would mis-lex. Keep it simple: prefix ops
                // always get one trailing space.
                output.Write(' ');
                int argMax = prefixShape == OperatorShape.Fy ? prefixPrec : prefixPrec - 1;
                if (operandParens) output.Write('(');
                Render(engine, engine.GetHeap(functorIdx + 1), output, options,
                    operandParens ? 1200 : argMax);
                if (operandParens) output.Write(')');
                if (needsParens) output.Write(')');
                return;
            }
            if (arity == 1 && options.Operators.TryGetPostfix(name, out int postPrec, out OperatorShape postShape))
            {
                bool needsParens = postPrec > maxPriority;
                if (needsParens) output.Write('(');
                int argMax = postShape == OperatorShape.Yf ? postPrec : postPrec - 1;
                Render(engine, engine.GetHeap(functorIdx + 1), output, options, argMax);
                bool tightPost = options.TightSymbolicOperators && IsSymbolicName(name);
                if (!tightPost) output.Write(' ');
                WriteAtomName(name, output, options);
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
        output.Write('[');
        bool first = true;
        Cell cursor = lisCell;
        while (true)
        {
            Resolve(engine, ref cursor);
            if (cursor.Tag != Tag.Lis) break;
            if (!first) output.Write(',');   // ISO: no layout between elements
            int headIdx = cursor.AsHeapIndex;
            Render(engine, engine.GetHeap(headIdx), output, options);
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
            Render(engine, cursor, output, options);
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
            case Tag.Float:
                return Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex)) >= 0;
            case Tag.Str:
            {
                if (options.IgnoreOps || options.Operators is null) return false;
                int fIdx = cell.AsHeapIndex;
                var (atomId, ar) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
                string fname = AtomTable.GetById(atomId)?.Name ?? "";
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
