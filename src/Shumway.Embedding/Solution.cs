using System.Globalization;
using Shumway.Compiler.Ast;

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

    internal Solution(bool success, IReadOnlyDictionary<string, Term> bindings)
    {
        Success = success;
        Bindings = bindings;
    }

    /// <summary>Returns the binding for the named variable, or <c>null</c> if the
    /// query failed or the variable wasn't in the query.</summary>
    public Term? this[string variableName] =>
        Bindings.TryGetValue(variableName, out var t) ? t : null;

    /// <summary>Renders the solution in a familiar interactive-Prolog form:
    /// <c>"true"</c> / <c>"false"</c> for variableless queries, or
    /// <c>"X = a, Y = foo(1)"</c> for queries with bindings.</summary>
    public override string ToString()
    {
        if (!Success) return "false";
        if (Bindings.Count == 0) return "true";
        return string.Join(", ", Bindings.Select(kv =>
            $"{kv.Key} = {Render(kv.Value)}"));
    }

    private static string Render(Term term) => term switch
    {
        AtomTerm a => a.Name,
        VarTerm v => v.Name,
        IntTerm n => n.Value.ToString(CultureInfo.InvariantCulture),
        FloatTerm f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        StringTerm s => $"\"{s.Content}\"",
        CompoundTerm { Functor: ".", Args.Length: 2 } c => RenderList(c),
        CompoundTerm c => $"{c.Functor}({string.Join(", ", c.Args.Select(Render))})",
        _ => term.ToString() ?? "?",
    };

    private static string RenderList(CompoundTerm cons)
    {
        var elements = new List<string>();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            elements.Add(Render(c.Args[0]));
            cursor = c.Args[1];
        }
        // Proper list (tail is [])?
        if (cursor is AtomTerm { Name: "[]" })
            return "[" + string.Join(", ", elements) + "]";
        // Improper / partial list — render with bar tail.
        return "[" + string.Join(", ", elements) + " | " + Render(cursor) + "]";
    }
}
