using System.Globalization;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>
/// The outcome of running a single Prolog query through <see cref="PrologEngine.Query"/>.
/// <see cref="Success"/> is true when the query succeeded (the interpreter halted
/// after the body satisfied) and false when it failed (no solution exists, or the
/// only solution was undone by a subsequent failure). <see cref="Bindings"/>
/// exposes the post-run value of every named variable that appeared in the query.
///
/// <para>On a failed query <see cref="Bindings"/> is empty — variable values are
/// only meaningful when the query succeeded. The query's anonymous variables
/// (<c>_</c>) never appear in the bindings since each occurrence is a fresh
/// distinct variable with no name to address it by.</para>
/// </summary>
public sealed class Solution
{
    public bool Success { get; }
    public IReadOnlyDictionary<string, Term> Bindings { get; }

    /// <summary>True when the engine has no choice point left for this
    /// query at the moment this solution was produced — i.e. it is
    /// known to be the last solution. An interactive top-level uses
    /// this to stop without the <c>;</c> prompt (and without a trailing
    /// <c>false</c>), matching SWI / GNU / SICStus: <c>member(A,[x,y])</c>
    /// prints <c>A = x ;</c> then <c>A = y.</c> with no further prompt,
    /// and <c>A = x, !</c> finishes immediately. <c>false</c> for a
    /// failed query.</summary>
    public bool IsLast { get; }

    internal Solution(bool success, IReadOnlyDictionary<string, Term> bindings,
        bool isLast = false)
    {
        Success = success;
        Bindings = bindings;
        IsLast = isLast;
    }

    /// <summary>Returns the binding for the named variable, or <c>null</c> if the
    /// query failed or the variable wasn't in the query.</summary>
    public Term? this[string variableName] =>
        Bindings.TryGetValue(variableName, out var t) ? t : null;

    /// <summary>Renders the solution in a familiar interactive-Prolog form:
    /// <c>"true"</c> / <c>"false"</c> for variableless queries, or
    /// <c>"X = a, Y = foo(1)"</c> for queries with bindings. Compound
    /// terms whose functor is a known operator print in operator form —
    /// <c>X = hola/2</c>, not <c>X = /(hola, 2)</c> — matching what the
    /// other Prologs' top-levels show and what <c>write/1</c> produces.</summary>
    public override string ToString()
    {
        if (!Success) return "false";
        if (Bindings.Count == 0) return "true";
        return string.Join(", ", Bindings.Select(kv =>
            $"{kv.Key} = {Render(kv.Value, 1200)}"));
    }

    /// <summary>Operator table used for binding display. The REPL parses
    /// queries with <see cref="OperatorTable.Default"/>, so rendering
    /// bindings with the same table round-trips.</summary>
    private static readonly OperatorTable Ops = OperatorTable.Default();

    /// <summary>Renders <paramref name="term"/> bounded by
    /// <paramref name="maxPrec"/>: a compound whose operator priority
    /// exceeds the bound is wrapped in parens so the result re-parses.</summary>
    private static string Render(Term term, int maxPrec)
    {
        switch (term)
        {
            case AtomTerm a: return a.Name;
            case VarTerm v: return v.Name;
            case IntTerm n: return n.Value.ToString(CultureInfo.InvariantCulture);
            case FloatTerm f: return f.Value.ToString("R", CultureInfo.InvariantCulture);
            case StringTerm s: return $"\"{s.Content}\"";
            case CompoundTerm { Functor: ".", Args.Length: 2 } list:
                return RenderList(list);
            case CompoundTerm c:
                return RenderCompound(c, maxPrec);
            default:
                return term.ToString() ?? "?";
        }
    }

    private static string RenderCompound(CompoundTerm c, int maxPrec)
    {
        if (c.Args.Length == 2 && Ops.TryGetInfix(c.Functor, out int iPrec, out var iType))
        {
            int leftMax = iType == OperatorType.Yfx ? iPrec : iPrec - 1;
            int rightMax = iType == OperatorType.Xfy ? iPrec : iPrec - 1;
            string sep = IsSymbolic(c.Functor) ? c.Functor : $" {c.Functor} ";
            string body = $"{Render(c.Args[0], leftMax)}{sep}{Render(c.Args[1], rightMax)}";
            return iPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && Ops.TryGetPrefix(c.Functor, out int pPrec, out var pType))
        {
            int argMax = pType == OperatorType.Fy ? pPrec : pPrec - 1;
            // Prefix ops keep a separating space so `- 1` doesn't fuse
            // into the negative literal -1.
            string body = $"{c.Functor} {Render(c.Args[0], argMax)}";
            return pPrec > maxPrec ? $"({body})" : body;
        }
        if (c.Args.Length == 1 && Ops.TryGetPostfix(c.Functor, out int sPrec, out var sType))
        {
            int argMax = sType == OperatorType.Yf ? sPrec : sPrec - 1;
            string sep = IsSymbolic(c.Functor) ? c.Functor : $" {c.Functor}";
            string body = $"{Render(c.Args[0], argMax)}{sep}";
            return sPrec > maxPrec ? $"({body})" : body;
        }
        // Canonical form. Arguments sit at priority 999 (below the
        // argument-comma's 1000) so a comma-term arg gets parenthesised.
        return $"{c.Functor}({string.Join(", ", c.Args.Select(a => Render(a, 999)))})";
    }

    /// <summary>True when <paramref name="name"/> is built entirely from
    /// ISO graphic chars (a symbolic operator like <c>/</c> or <c>+</c>),
    /// which render tight (no surrounding spaces). Alphabetic operators
    /// (<c>is</c>, <c>mod</c>) keep their spaces.</summary>
    private static bool IsSymbolic(string name)
    {
        if (name.Length == 0) return false;
        foreach (char ch in name)
            if ("+-*/\\^<>=~:.?@#&$".IndexOf(ch) < 0) return false;
        return true;
    }

    private static string RenderList(CompoundTerm cons)
    {
        var elements = new List<string>();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            // List elements sit at priority 999 — a comma-term element
            // must be parenthesised so it doesn't read as more elements.
            elements.Add(Render(c.Args[0], 999));
            cursor = c.Args[1];
        }
        // Proper list (tail is [])?
        if (cursor is AtomTerm { Name: "[]" })
            return "[" + string.Join(", ", elements) + "]";
        // Improper / partial list — render with bar tail.
        return "[" + string.Join(", ", elements) + " | " + Render(cursor, 999) + "]";
    }
}
