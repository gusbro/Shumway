using System.Collections.Generic;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>ADR-024 — the set of Arity generic-term-interface predicates that are
/// provided as builtins and whose <c>prlg_ifce.pl</c> source definitions are
/// dropped at consult/compile (recognized by name + arity). Those source clauses
/// are written with the reftype-struct tier (`(*Ref)->ntype`, `(crep)..cint`,
/// `getargp`, `newreftype`, `freepar`, `setcflt`) which Shumway deliberately never
/// compiles — the builtins do the work directly over the heap (see
/// <see cref="TermSlot"/> / <see cref="ReftypeApi"/>).</summary>
internal static class ReftypeInterface
{
    /// <summary>name → set of arities recognized for that name.</summary>
    private static readonly Dictionary<string, HashSet<int>> Predicates = new()
    {
        ["reftype_term"] = new() { 2, 3 },
        ["reftype_functor"] = new() { 4 },
        ["fill_par"] = new() { 2 },
        ["fill_reftype"] = new() { 3 },
        ["fill_args"] = new() { 4 },
        ["preftype"] = new() { 1 },
    };

    public static bool IsInterfacePredicate(string name, int arity)
        => Predicates.TryGetValue(name, out var arities) && arities.Contains(arity);

    /// <summary>Returns the clauses with every term-interface predicate's clauses
    /// removed (the builtins replace them). A directive or a non-interface clause
    /// passes through unchanged.</summary>
    public static List<Clause> DropInterfaceClauses(IReadOnlyList<Clause> clauses)
    {
        var kept = new List<Clause>(clauses.Count);
        foreach (var c in clauses)
        {
            if (c.Kind != ClauseKind.Directive && Head(c.Term) is var (name, arity)
                && IsInterfacePredicate(name, arity))
                continue;   // drop — the builtin provides it
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>The (name, arity) of a clause's head, or null for a directive /
    /// unrecognized shape.</summary>
    private static (string Name, int Arity)? Head(Term clauseTerm)
    {
        Term head = clauseTerm is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
            ? rule.Args[0] : clauseTerm;
        return head switch
        {
            CompoundTerm h => (h.Functor, h.Args.Length),
            AtomTerm a => (a.Name, 0),
            _ => null,
        };
    }
}
