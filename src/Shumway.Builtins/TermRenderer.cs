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
public static partial class TermRenderer
{
    public static void Render(Activation engine, Cell cell, TextWriter output)
        => Render(engine, cell, output, TermRenderOptions.Default);

    public static void Render(Activation engine, Cell cell, TextWriter output, TermRenderOptions options)
    {
        // Entry funnel — reset the cycle-safety state (the Default options
        // instance is shared across calls).
        options.OnPath?.Clear();
        options.UnrolledOnce?.Clear();
        Render(engine, cell, output, options, maxPriority: 1200);
    }


    /// <summary>Cycle gate for a compound reached in ARGUMENT position:
    /// true = proceed; <paramref name="added"/> / <paramref name="unrolling"/>
    /// say what this frame owns (path entry / the cell's one permitted
    /// unroll) and must remove on the way out. False = on the path and
    /// already unrolled — the caller renders <c>...</c>.</summary>
    private static bool EnterCycleNode(
        TermRenderOptions options, int nodeAddr, out bool added, out bool unrolling)
    {
        unrolling = false;
        var path = options.OnPath ??= new System.Collections.Generic.HashSet<int>();
        added = path.Add(nodeAddr);
        if (added) return true;
        var unrolled = options.UnrolledOnce ??= new System.Collections.Generic.HashSet<int>();
        unrolling = unrolled.Add(nodeAddr);
        return unrolling;
    }

    /// <summary>Is this (resolved) cell a compound whose node is currently
    /// on the render path? List elements, spine cells and tails elide
    /// immediately when it is (the Trealla-printer policy) — only struct
    /// arguments unroll, once per cell.</summary>
    private static bool IsOnPath(TermRenderOptions options, Cell cell)
        => options.OnPath is { } path
            && cell.Tag is Tag.Str or Tag.Lis
            && path.Contains(cell.AsHeapIndex);

    private static int Resolve(Activation engine, ref Cell cell)
    {
        // A bare ATTVAR cell carries its own home index as payload —
        // surface that as the deref address and leave the AttVar-tagged
        // cell for the caller's switch.
        if (cell.Tag == Tag.AttVar) return cell.AsHeapIndex;
        if (cell.Tag == Tag.Pstr) { cell = engine.NormalizeListCell(cell); return -1; }
        if (cell.Tag != Tag.Ref) return -1;
        int addr = engine.Deref(cell.AsHeapIndex);
        cell = engine.NormalizeListCell(engine.GetHeap(addr));
        return addr;
    }



    private static void RemoveSpine(
        TermRenderOptions options, System.Collections.Generic.List<int>? spine)
    {
        if (spine is null) return;
        foreach (int addr in spine) options.OnPath!.Remove(addr);
    }


    private static bool TryRenderAsText(
        Activation engine, Cell lisCell, TextWriter output, TermRenderOptions options)
    {
        if (!TryCollectText(engine, lisCell, out string text, out Cell tail)) return false;
        Resolve(engine, ref tail);
        if (!(tail.Tag == Tag.Atom && tail.AsAtomId == AtomTable.EmptyListId)) return false;
        if (text.Length == 0) return false;
        WriteQuotedText(text, output);
        return true;
    }

    /// <summary>Reads a list's elements as text, one-char atoms or codes but
    /// never both, and hands back what it ended on. The verdict is on the
    /// CONTENT (ADR-047 decision 7), so a packed list and the cons list it
    /// denotes are read alike.</summary>
    private static bool TryCollectText(
        Activation engine, Cell lisCell, out string text, out Cell tail)
    {
        var sb = new System.Text.StringBuilder();
        Cell cur = lisCell;
        bool? chars = null;
        int guard = engine.HeapTop + 2;
        text = "";
        tail = cur;
        while (guard-- > 0)
        {
            Resolve(engine, ref cur);
            if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId) break;
            if (!engine.TryUnconsListLike(cur, out Cell head, out Cell rest))
            {
                // Not a cons: this is where the text stops, and what stops it
                // is the tail.
                text = sb.ToString();
                tail = cur;
                return true;
            }
            Resolve(engine, ref head);
            if (head.Tag == Tag.Atom)
            {
                if (chars == false) return false;
                string n = NameOfAtom(head.AsAtomId);
                if (n.Length != 1) return false;
                chars = true;
                sb.Append(n);
            }
            else if (head.Tag == Tag.Int)
            {
                if (chars == true) return false;
                long c = head.AsInt;
                // Printable, plus the three layout codes. Without this a list
                // of small integers would portray as control characters.
                if (c > char.MaxValue || !(c >= 32 || c is 9 or 10 or 13)) return false;
                chars = false;
                sb.Append((char)c);
            }
            else return false;
            cur = rest;
        }
        if (guard < 0) return false;
        text = sb.ToString();
        tail = cur;
        return true;
    }

    private static void WriteQuotedText(string text, TextWriter output)
    {
        output.Write('"');
        foreach (char c in text)
        {
            if (c == '"') { output.Write("\\\""); continue; }
            string? esc = EscapeQuotedChar(c);
            // EscapeQuotedChar escapes the SINGLE quote for atoms; inside
            // double quotes that character is literal.
            if (esc is not null && c != '\'') output.Write(esc);
            else output.Write(c);
        }
        output.Write('"');
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
    public static string? EscapeQuotedChar(char c) => c switch
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
    /// <summary>Mirror of the LEXER's letter-atom classification — the two
    /// must agree or writeq output stops round-tripping: an atom prints
    /// unquoted exactly when its first character starts an unquoted atom
    /// there (a lowercase / other / modifier letter, BMP or astral).</summary>
    private static bool IsUnquotedAtomStart(string name, out int firstLen)
    {
        char c = name[0];
        firstLen = 1;
        if (char.IsHighSurrogate(c) && name.Length > 1
            && char.IsLowSurrogate(name[1]))
        {
            firstLen = 2;
            return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(name, 0)
                is System.Globalization.UnicodeCategory.LowercaseLetter
                or System.Globalization.UnicodeCategory.OtherLetter
                or System.Globalization.UnicodeCategory.ModifierLetter;
        }
        if (!char.IsLetter(c)) return false;
        return !(char.IsUpper(c)
            || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.TitlecaseLetter);
    }

    /// <summary>One identifier-tail character at <paramref name="i"/> —
    /// letter / digit / underscore, BMP or astral pair.</summary>
    private static bool IsIdentifierTailAt(string name, int i, out int len)
    {
        char c = name[i];
        len = 1;
        if (char.IsLetterOrDigit(c) || c == '_') return true;
        if (char.IsHighSurrogate(c) && i + 1 < name.Length
            && char.IsLowSurrogate(name[i + 1]))
        {
            len = 2;
            return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(name, i)
                is System.Globalization.UnicodeCategory.LowercaseLetter
                or System.Globalization.UnicodeCategory.UppercaseLetter
                or System.Globalization.UnicodeCategory.TitlecaseLetter
                or System.Globalization.UnicodeCategory.OtherLetter
                or System.Globalization.UnicodeCategory.ModifierLetter
                or System.Globalization.UnicodeCategory.DecimalDigitNumber
                or System.Globalization.UnicodeCategory.LetterNumber;
        }
        return false;
    }

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
        if (IsUnquotedAtomStart(name, out int firstLen))
        {
            for (int i = firstLen; i < name.Length; )
            {
                if (!IsIdentifierTailAt(name, i, out int len)) return false;
                i += len;
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
