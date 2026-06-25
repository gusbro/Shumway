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
        // quote_str(X, XR): prlg_ifce.pl quotes a string through C buffers
        // (ppchar / malloc / *deref — the unsupported tier). The builtin does the
        // quoting directly (render X writeq-style → XR). Used only inside prlg_ifce.
        ["quote_str"] = new() { 2 },
    };

    public static bool IsInterfacePredicate(string name, int arity)
        => Predicates.TryGetValue(name, out var arities) && arities.Contains(arity);

    /// <summary>Returns the clauses with every term-interface predicate's clauses
    /// removed (the builtins replace them), plus — under arity_compat — any
    /// predicate that redefines a registered C# builtin (Arity programs supply
    /// their own definitions of predicates Shumway predefines as intrinsics, e.g.
    /// make_c_string/4). A directive or an ordinary clause passes through unchanged.
    /// Each dropped builtin redefinition is reported in
    /// <paramref name="droppedBuiltins"/> so the caller can warn.</summary>
    public static List<Clause> DropInterfaceClauses(IReadOnlyList<Clause> clauses,
        List<(string Name, int Arity)>? droppedBuiltins = null)
    {
        var reportedBuiltins = new HashSet<(string, int)>();
        var kept = new List<Clause>(clauses.Count);
        foreach (var c in clauses)
        {
            if (c.Kind != ClauseKind.Directive && Head(c.Term) is var (name, arity))
            {
                if (IsInterfacePredicate(name, arity))
                    continue;   // curated drop (some are not builtins) — no warning
                if (IsBuiltin(name, arity))
                {
                    // Redefinition of a Shumway builtin — drop it, warn once.
                    if (droppedBuiltins is not null && reportedBuiltins.Add((name, arity)))
                        droppedBuiltins.Add((name, arity));
                    continue;
                }
            }
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>True when <paramref name="name"/>/<paramref name="arity"/> is a
    /// registered C# builtin.</summary>
    private static bool IsBuiltin(string name, int arity)
    {
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
        return Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _);
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
