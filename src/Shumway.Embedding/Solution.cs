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

    /// <summary>Chunk 238 — engine that produced this solution. Used
    /// by <see cref="Get{T}"/> / <see cref="TryGet{T}"/> to resolve
    /// the host's registered term converters (the built-in scalar
    /// converters work without it, but a user converter for a custom
    /// type needs the engine that registered it). Null only for the
    /// failed-query sentinel created by <c>Query(string)</c>.</summary>
    internal PrologEngine? Engine { get; }

    internal Solution(bool success, IReadOnlyDictionary<string, Term> bindings,
        bool isLast = false, PrologEngine? engine = null)
    {
        Success = success;
        Bindings = bindings;
        IsLast = isLast;
        Engine = engine;
    }

    /// <summary>Returns the binding for the named variable, or <c>null</c> if the
    /// query failed or the variable wasn't in the query.</summary>
    public Term? this[string variableName] =>
        Bindings.TryGetValue(variableName, out var t) ? t : null;

    /// <summary>Chunk 238 — typed accessor: returns the binding for
    /// <paramref name="variableName"/> converted to
    /// <typeparamref name="T"/> via the engine's converters (built-in
    /// or user-registered through
    /// <see cref="PrologEngine.RegisterConverter{T}"/>). Throws
    /// <see cref="KeyNotFoundException"/> if the variable isn't in
    /// the bindings, or whatever <see cref="PrologEngine.FromTerm{T}"/>
    /// raises on a type mismatch.</summary>
    public T Get<T>(string variableName)
    {
        ArgumentNullException.ThrowIfNull(variableName);
        if (!Bindings.TryGetValue(variableName, out var t))
            throw new KeyNotFoundException(
                $"Solution has no binding for variable '{variableName}'.");
        if (Engine is null)
            throw new InvalidOperationException(
                "Solution.Get<T> requires a host PrologEngine; this solution was "
                + "constructed without one (failed-query sentinel).");
        return Engine.FromTerm<T>(t);
    }

    /// <summary>Chunk 238 — non-throwing variant of <see cref="Get{T}"/>:
    /// returns <c>false</c> when the variable isn't bound; surfaces
    /// type-conversion exceptions as-is (they signal a programmer
    /// error, not the absence of data).</summary>
    public bool TryGet<T>(string variableName, out T value)
    {
        ArgumentNullException.ThrowIfNull(variableName);
        if (!Bindings.TryGetValue(variableName, out var t) || Engine is null)
        {
            value = default!;
            return false;
        }
        value = Engine.FromTerm<T>(t);
        return true;
    }

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

    /// <summary>Chunk 252 — pretty-print variant. Each binding fits
    /// on its own line; a term whose compact rendering would exceed
    /// <paramref name="width"/> columns breaks across lines with
    /// indented arguments. Compact terms render as the default
    /// <see cref="ToString()"/> would; the multi-line form only
    /// kicks in when needed.
    ///
    /// <para>The REPL passes <c>Console.WindowWidth</c>; embedding
    /// API consumers that want compact output use the bare
    /// <see cref="ToString()"/> overload.</para></summary>
    public string ToString(int width)
    {
        if (!Success) return "false";
        if (Bindings.Count == 0) return "true";
        // Width budget for each binding: subtract the "X = " prefix
        // from the available columns.
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var kv in Bindings)
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append(kv.Key).Append(" = ");
            int prefix = kv.Key.Length + 3;
            PrettyInto(sb, kv.Value, indent: prefix, maxPrec: 1200, width: width);
        }
        return sb.ToString();
    }

    /// <summary>Chunk 252 — pretty-printer. Tries the compact
    /// <see cref="Render"/> first; if it fits in
    /// <c>width - indent</c> columns, uses it as-is. Otherwise
    /// breaks the term across lines with each argument indented
    /// two spaces past the parent.
    ///
    /// <para>Only compounds and lists can break — atoms, numbers,
    /// strings, and variables emit compact regardless. Operator
    /// compounds also stay compact (breaking at the operator is
    /// notation-fragile and rarely what the user wants when a
    /// single binding overflows).</para></summary>
    private static void PrettyInto(
        System.Text.StringBuilder sb, Term term, int indent, int maxPrec, int width)
    {
        string compact = Render(term, maxPrec);
        int budget = width - indent;
        if (compact.Length <= budget || budget < 16)
        {
            // Either it fits, or the indent has eaten so much of
            // the budget that breaking won't help. Emit compact.
            sb.Append(compact);
            return;
        }

        switch (term)
        {
            case CompoundTerm { Functor: ".", Args.Length: 2 } cons:
                PrettyList(sb, cons, indent, width);
                break;
            case CompoundTerm c when !IsOperatorCompound(c):
                PrettyCompound(sb, c, indent, width);
                break;
            default:
                // Operators / unknown shapes fall back to compact.
                sb.Append(compact);
                break;
        }
    }

    private static void PrettyList(
        System.Text.StringBuilder sb, CompoundTerm cons, int indent, int width)
    {
        // Gather list elements + tail (which may be a non-nil
        // partial-list tail).
        var elements = new List<Term>();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            elements.Add(c.Args[0]);
            cursor = c.Args[1];
        }
        string innerPad = new string(' ', indent + 2);
        string closePad = new string(' ', indent);
        sb.Append('[').Append('\n').Append(innerPad);
        for (int i = 0; i < elements.Count; i++)
        {
            PrettyInto(sb, elements[i], indent + 2, 999, width);
            if (i < elements.Count - 1)
                sb.Append(",\n").Append(innerPad);
        }
        if (cursor is not AtomTerm { Name: "[]" })
        {
            sb.Append('\n').Append(closePad).Append("| ");
            PrettyInto(sb, cursor, indent + 2, 999, width);
        }
        sb.Append('\n').Append(closePad).Append(']');
    }

    private static void PrettyCompound(
        System.Text.StringBuilder sb, CompoundTerm c, int indent, int width)
    {
        string innerPad = new string(' ', indent + 2);
        string closePad = new string(' ', indent);
        sb.Append(c.Functor).Append('(').Append('\n').Append(innerPad);
        for (int i = 0; i < c.Args.Length; i++)
        {
            PrettyInto(sb, c.Args[i], indent + 2, 999, width);
            if (i < c.Args.Length - 1)
                sb.Append(",\n").Append(innerPad);
        }
        sb.Append('\n').Append(closePad).Append(')');
    }

    private static bool IsOperatorCompound(CompoundTerm c) =>
        (c.Args.Length == 2 && Ops.TryGetInfix(c.Functor, out _, out _))
        || (c.Args.Length == 1 && Ops.TryGetPrefix(c.Functor, out _, out _))
        || (c.Args.Length == 1 && Ops.TryGetPostfix(c.Functor, out _, out _));

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
