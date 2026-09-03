using System.Globalization;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>
/// operator-aware renderer for AST <see cref="Term"/>
/// trees. Replaces ad-hoc <see cref="object.ToString"/> calls that
/// produce canonical-form output (<c>=(X, hello(Y))</c>) with the
/// reader-friendly operator form (<c>X = hello(Y)</c>), list syntax
/// (<c>[a, b | T]</c> instead of <c>.(a, .(b, T))</c>), and the
/// standard atom / number / string spellings.
///
/// <para>Originally inlined inside <see cref="Solution"/>'s
/// <c>Render</c> for binding display; lifted out so the
/// <c>listing</c> path and any future AST consumer
/// can reuse the same logic. The renderer operates on the AST
/// <em>without</em> involving the engine heap, so variable names
/// the parser captured (<see cref="VarTerm.Name"/>) survive
/// verbatim — the original goal of the chunk.</para>
/// </summary>
public static class AstTermRenderer
{
    private static readonly OperatorTable DefaultOps = OperatorTable.Default();

    /// <summary>Renders <paramref name="term"/> using the default
    /// operator table (the one the parser uses), at the maximum
    /// priority bound — i.e. no enclosing context forces
    /// parenthesisation. Equivalent to Prolog's
    /// <c>write_term(Term, [quoted(false)])</c> rendering.</summary>
    public static string Render(Term term)
        => Render(term, 1200, DefaultOps);

    /// <summary>Renders <paramref name="term"/> bounded by
    /// <paramref name="maxPrec"/>: a compound whose operator priority
    /// exceeds the bound is wrapped in parens so the result re-parses
    /// to the same AST shape.</summary>
    public static string Render(Term term, int maxPrec)
        => Render(term, maxPrec, DefaultOps);

    /// <summary>The <c>writeq</c>-flavoured render with the default operator
    /// table — what <c>listing</c>/<c>portray_clause</c> emit, where an atom
    /// that would not re-read as itself must be quoted.</summary>
    public static string RenderQuoted(Term term, int maxPrec = 1200)
        => Render(term, maxPrec, DefaultOps, quoted: true);

    /// <summary>Operator-aware overload using a caller-supplied table —
    /// pass <see cref="PrologEngine.Operators"/> to render terms that
    /// mention operators introduced at runtime (e.g. CLP(FD)'s
    /// <c>in</c>, <c>..</c>, <c>#=</c>) in their operator form.</summary>
    public static string Render(Term term, int maxPrec, OperatorTable ops)
        => Render(term, maxPrec, ops, quoted: false);

    /// <summary>ADR-035 D5+ — the <c>writeq</c>-style overload: atoms and canonical
    /// functor names that would not re-parse to the same term are single-quoted. The
    /// debugger's displays use this — a Locals value feeds the Watch-window EDIT, and
    /// showing the atom <c>'1234'</c> as bare <c>1234</c> made the round-tripped value
    /// an INTEGER. Operator occurrences stay unquoted (they re-parse as written).</summary>
    public static string Render(Term term, int maxPrec, OperatorTable ops, bool quoted)
        => Render(term, maxPrec, ops, quoted, portrayText: false);

    /// <summary>The top level's ANSWER rendering: quoted (re-readable — a raw
    /// newline inside an atom never leaks into the transcript) and with text
    /// portrayed: a proper list of characters shows as <c>"..."</c> with
    /// escapes. Program text (listing, portray_clause) stays list-shaped;
    /// this form is for a human reading answers.</summary>
    public static string RenderAnswer(Term term, OperatorTable ops)
        => Render(term, 1200, ops, quoted: true, portrayText: true);

    public static string Render(
        Term term, int maxPrec, OperatorTable ops, bool quoted, bool portrayText)
    {
        switch (term)
        {
            case AtomTerm a:
                return quoted ? Shumway.Builtins.TermRenderer.QuotedAtomName(a.Name) : a.Name;
            case VarTerm v: return v.Name;
            case IntTerm n: return n.Value.ToString(CultureInfo.InvariantCulture);
            case FloatTerm f: return Shumway.Builtins.Number.FormatPrologFloat(f.Value);
            case StringTerm s: return RenderDoubleQuoted(s.Content);
            case BigIntTerm b: return b.Value.ToString(CultureInfo.InvariantCulture);
            case CompoundTerm { Functor: ".", Args.Length: 2 } list:
                return portrayText && TryRenderTextList(list, out string text)
                    ? text
                    : RenderList(list, ops, quoted, portrayText);
            // '{}'(X) reads back as {X} — the canonical form would re-parse
            // but is not what writeq/portray_clause emit.
            case CompoundTerm { Functor: "{}", Args.Length: 1 } curly:
                return "{" + Render(curly.Args[0], 1200, ops, quoted, portrayText) + "}";
            case CompoundTerm c:
                return RenderCompound(c, maxPrec, ops, quoted, portrayText);
            default:
                return term.ToString() ?? "?";
        }
    }

    /// <summary>A proper, non-empty list of single-character atoms renders as
    /// a double-quoted string — the text reading of the default
    /// <c>double_quotes = chars</c>. CODES stay numeric on purpose:
    /// <c>[65, 66]</c> displaying as <c>"AB"</c> would dress arbitrary small
    /// integers up as text (the strictest engine agrees); the cell writer's
    /// portray-text OPTION still covers codes for callers that ask.</summary>
    private static bool TryRenderTextList(CompoundTerm cons, out string rendered)
    {
        rendered = "";
        var sb = new StringBuilder();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            if (c.Args[0] is not AtomTerm a || !IsOneCodePoint(a.Name))
                return false;
            sb.Append(a.Name);
            cursor = c.Args[1];
        }
        if (cursor is not AtomTerm { Name: "[]" } || sb.Length == 0) return false;
        rendered = RenderDoubleQuoted(sb.ToString());
        return true;
    }

    private static bool IsOneCodePoint(string name) =>
        name.Length == 1
        || (name.Length == 2 && char.IsHighSurrogate(name[0])
            && char.IsLowSurrogate(name[1]));

    /// <summary>Double-quoted text with the writeq escapes — a raw control
    /// character in the content must never reach the transcript raw.</summary>
    private static string RenderDoubleQuoted(string content)
    {
        var sb = new StringBuilder(content.Length + 2);
        sb.Append('"');
        foreach (char ch in content)
        {
            if (ch == '"') { sb.Append("\\\""); continue; }
            string? esc = Shumway.Builtins.TermRenderer.EscapeQuotedChar(ch);
            // The single quote is literal inside double quotes.
            if (esc is not null && ch != '\'') sb.Append(esc);
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    private static string RenderCompound(
        CompoundTerm c, int maxPrec, OperatorTable ops, bool quoted, bool portrayText)
    {
        if (c.Args.Length == 2 && ops.TryGetInfix(c.Functor, out int iPrec, out var iType))
        {
            int leftMax = iType == OperatorType.Yfx ? iPrec : iPrec - 1;
            int rightMax = iType == OperatorType.Xfy ? iPrec : iPrec - 1;
            // comma and semicolon (sequence / disjunction
            // operators) render with no leading space: `a, b` and
            // `a ; b`. Symbolic operators (`+`, `/`, `=`) stay tight.
            // Alphabetic operators (`is`, `mod`) keep spaces both
            // sides.
            string sep = c.Functor switch
            {
                "," => ", ",
                ";" => "; ",
                _ when IsSymbolic(c.Functor) => c.Functor,
                _ => $" {c.Functor} ",
            };
            string leftStr = RenderOperand(c.Args[0], leftMax, ops, quoted, portrayText);
            string rightStr = RenderOperand(c.Args[1], rightMax, ops, quoted, portrayText);
            // A tight symbolic operator fuses with a graphic-ending operand
            // into ONE token on re-read (`.. = ..` as `..=..`; `X = -1` as
            // `X=-1`, lexing `=-`): pad exactly where adjacency would fuse.
            if (sep.Length > 0 && IsGraphicChar(sep[0]))
            {
                if (leftStr.Length > 0 && IsGraphicChar(leftStr[^1]))
                    sep = " " + sep;
                if (rightStr.Length > 0 && IsGraphicChar(rightStr[0]))
                    sep += " ";
            }
            string body = $"{leftStr}{sep}{rightStr}";
            return iPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && ops.TryGetPrefix(c.Functor, out int pPrec, out var pType))
        {
            int argMax = pType == OperatorType.Fy ? pPrec : pPrec - 1;
            string body = $"{c.Functor} {RenderOperand(c.Args[0], argMax, ops, quoted, portrayText)}";
            return pPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && ops.TryGetPostfix(c.Functor, out int sPrec, out var sType))
        {
            int argMax = sType == OperatorType.Yf ? sPrec : sPrec - 1;
            string sep = IsSymbolic(c.Functor) ? c.Functor : $" {c.Functor}";
            string operandStr = RenderOperand(c.Args[0], argMax, ops, quoted, portrayText);
            if (sep.Length > 0 && IsGraphicChar(sep[0])
                && operandStr.Length > 0 && IsGraphicChar(operandStr[^1]))
                sep = " " + sep;
            string body = $"{operandStr}{sep}";
            return sPrec > maxPrec ? $"({body})" : body;
        }
        // Canonical form. Arguments sit at priority 999 (below the
        // argument-comma's 1000) so a comma-term arg gets parenthesised.
        var sb = new StringBuilder(
            quoted ? Shumway.Builtins.TermRenderer.QuotedAtomName(c.Functor) : c.Functor);
        sb.Append('(');
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Render(c.Args[i], 999, ops, quoted, portrayText));
        }
        return sb.Append(')').ToString();
    }

    /// <summary>An OPERAND of an operator: a bare operator atom there would
    /// not re-read (ISO 6.3.1.3, the s#378 rule the parser now enforces), so
    /// it renders parenthesised — `(is)/2`, never `is/2`. Argument and list
    /// positions render through plain Render and keep the bare atom, which
    /// is exactly where ISO admits it.</summary>
    private static string RenderOperand(
        Term t, int maxPrec, OperatorTable ops, bool quoted, bool portrayText)
    {
        string s = Render(t, maxPrec, ops, quoted, portrayText);
        return t is AtomTerm a
               && (ops.TryGetInfix(a.Name, out _, out _)
                   || ops.TryGetPrefix(a.Name, out _, out _)
                   || ops.TryGetPostfix(a.Name, out _, out _))
            ? "(" + s + ")"
            : s;
    }

    private static bool IsSymbolic(string name)
    {
        if (name.Length == 0) return false;
        foreach (char ch in name)
            if (!IsGraphicChar(ch)) return false;
        return true;
    }

    /// <summary>A char of the Prolog graphic-token alphabet: two adjacent
    /// graphic chars lex as one token, so renderers must pad where rendered
    /// pieces would otherwise fuse.</summary>
    internal static bool IsGraphicChar(char ch)
        => "+-*/\\^<>=~:.?@#&$".IndexOf(ch) >= 0;

    private static string RenderList(
        CompoundTerm cons, OperatorTable ops, bool quoted, bool portrayText)
    {
        var elements = new List<string>();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            elements.Add(Render(c.Args[0], 999, ops, quoted, portrayText));
            cursor = c.Args[1];
        }
        if (cursor is AtomTerm { Name: "[]" })
            return "[" + string.Join(", ", elements) + "]";
        return "[" + string.Join(", ", elements) + " | "
            + Render(cursor, 999, ops, quoted, portrayText) + "]";
    }
}
