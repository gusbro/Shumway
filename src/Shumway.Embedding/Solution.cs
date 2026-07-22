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

    /// <summary>engine that produced this solution. Used
    /// by <see cref="Get{T}"/> / <see cref="TryGet{T}"/> to resolve
    /// the host's registered term converters (the built-in scalar
    /// converters work without it, but a user converter for a custom
    /// type needs the engine that registered it). Null only for the
    /// failed-query sentinel created by <c>Query(string)</c>.</summary>
    internal PrologEngine? Activation { get; }

    /// <summary>For each bound variable whose value is a compound: the heap
    /// address of the value's root node — the address a cyclic term's
    /// <c>_C{addr}</c> marker carries when the cycle re-enters at that root.
    /// Lets a display layer print <c>A = [a, b | A]</c> instead of the raw
    /// marker. Null when not collected (failed-query sentinel).</summary>
    internal IReadOnlyDictionary<string, int>? ValueRootAddresses { get; }

    internal Solution(bool success, IReadOnlyDictionary<string, Term> bindings,
        bool isLast = false, PrologEngine? engine = null,
        IReadOnlyDictionary<string, int>? valueRootAddresses = null)
    {
        Success = success;
        Bindings = bindings;
        IsLast = isLast;
        Activation = engine;
        ValueRootAddresses = valueRootAddresses;
    }

    /// <summary>Returns the binding for the named variable, or <c>null</c> if the
    /// query failed or the variable wasn't in the query.</summary>
    public Term? this[string variableName] =>
        Bindings.TryGetValue(variableName, out var t) ? t : null;

    /// <summary>typed accessor: returns the binding for
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
        if (Activation is null)
            throw new InvalidOperationException(
                "Solution.Get<T> requires a host PrologEngine; this solution was "
                + "constructed without one (failed-query sentinel).");
        return Activation.FromTerm<T>(t);
    }

    /// <summary>non-throwing variant of <see cref="Get{T}"/>:
    /// returns <c>false</c> when the variable isn't bound; surfaces
    /// type-conversion exceptions as-is (they signal a programmer
    /// error, not the absence of data).</summary>
    public bool TryGet<T>(string variableName, out T value)
    {
        ArgumentNullException.ThrowIfNull(variableName);
        if (!Bindings.TryGetValue(variableName, out var t) || Activation is null)
        {
            value = default!;
            return false;
        }
        value = Activation.FromTerm<T>(t);
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

    /// <summary>pretty-print variant. Each binding fits
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

    /// <summary>pretty-printer. Tries the compact
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

    /// <summary>delegates to the extracted public
    /// <see cref="AstTermRenderer"/> so the listing builtin and any
    /// future AST consumer share the same rendering rules.</summary>
    private static string Render(Term term, int maxPrec)
        => AstTermRenderer.Render(term, maxPrec);
}
