using System.Globalization;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>
/// Chunk 254 — operator-aware renderer for AST <see cref="Term"/>
/// trees. Replaces ad-hoc <see cref="object.ToString"/> calls that
/// produce canonical-form output (<c>=(X, hello(Y))</c>) with the
/// reader-friendly operator form (<c>X = hello(Y)</c>), list syntax
/// (<c>[a, b | T]</c> instead of <c>.(a, .(b, T))</c>), and the
/// standard atom / number / string spellings.
///
/// <para>Originally inlined inside <see cref="Solution"/>'s
/// <c>Render</c> for binding display; lifted out so the
/// <c>listing</c> path (chunk 254) and any future AST consumer
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

    /// <summary>Operator-aware overload using a caller-supplied table —
    /// pass <see cref="PrologEngine.Operators"/> to render terms that
    /// mention operators introduced at runtime (e.g. CLP(FD)'s
    /// <c>in</c>, <c>..</c>, <c>#=</c>) in their operator form.</summary>
    public static string Render(Term term, int maxPrec, OperatorTable ops)
    {
        switch (term)
        {
            case AtomTerm a: return a.Name;
            case VarTerm v: return v.Name;
            case IntTerm n: return n.Value.ToString(CultureInfo.InvariantCulture);
            case FloatTerm f: return f.Value.ToString("R", CultureInfo.InvariantCulture);
            case StringTerm s: return $"\"{s.Content}\"";
            case BigIntTerm b: return b.Value.ToString(CultureInfo.InvariantCulture);
            case CompoundTerm { Functor: ".", Args.Length: 2 } list:
                return RenderList(list, ops);
            case CompoundTerm c:
                return RenderCompound(c, maxPrec, ops);
            default:
                return term.ToString() ?? "?";
        }
    }

    private static string RenderCompound(CompoundTerm c, int maxPrec, OperatorTable ops)
    {
        if (c.Args.Length == 2 && ops.TryGetInfix(c.Functor, out int iPrec, out var iType))
        {
            int leftMax = iType == OperatorType.Yfx ? iPrec : iPrec - 1;
            int rightMax = iType == OperatorType.Xfy ? iPrec : iPrec - 1;
            // Chunk 258 — comma and semicolon (sequence / disjunction
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
            string body = $"{Render(c.Args[0], leftMax, ops)}{sep}{Render(c.Args[1], rightMax, ops)}";
            return iPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && ops.TryGetPrefix(c.Functor, out int pPrec, out var pType))
        {
            int argMax = pType == OperatorType.Fy ? pPrec : pPrec - 1;
            string body = $"{c.Functor} {Render(c.Args[0], argMax, ops)}";
            return pPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && ops.TryGetPostfix(c.Functor, out int sPrec, out var sType))
        {
            int argMax = sType == OperatorType.Yf ? sPrec : sPrec - 1;
            string sep = IsSymbolic(c.Functor) ? c.Functor : $" {c.Functor}";
            string body = $"{Render(c.Args[0], argMax, ops)}{sep}";
            return sPrec > maxPrec ? $"({body})" : body;
        }
        // Canonical form. Arguments sit at priority 999 (below the
        // argument-comma's 1000) so a comma-term arg gets parenthesised.
        var sb = new StringBuilder(c.Functor).Append('(');
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Render(c.Args[i], 999, ops));
        }
        return sb.Append(')').ToString();
    }

    private static bool IsSymbolic(string name)
    {
        if (name.Length == 0) return false;
        foreach (char ch in name)
            if ("+-*/\\^<>=~:.?@#&$".IndexOf(ch) < 0) return false;
        return true;
    }

    private static string RenderList(CompoundTerm cons, OperatorTable ops)
    {
        var elements = new List<string>();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            elements.Add(Render(c.Args[0], 999, ops));
            cursor = c.Args[1];
        }
        if (cursor is AtomTerm { Name: "[]" })
            return "[" + string.Join(", ", elements) + "]";
        return "[" + string.Join(", ", elements) + " | " + Render(cursor, 999, ops) + "]";
    }
}
