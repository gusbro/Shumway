using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Modes;

/// <summary>
/// The first mode-aware code-generation pass. ADR-012's
/// Phase-3 plan calls for specialised code paths for deterministic
/// modes that drop the choice-point machinery. This transform is the
/// AST-level realisation of that for the safe, contained case:
///
/// <para>When every declared mode of a predicate is deterministic
/// (det / semidet — see <see cref="ModeTable.AllModesDeterministic"/>),
/// each of its clauses gets an implicit trailing cut. A clause
/// <c>H :- B.</c> becomes <c>H :- B, !.</c>; a fact <c>H.</c> becomes
/// <c>H :- !.</c>. The cut commits to the first clause whose head and
/// body both succeed and discards every choice point created since
/// the predicate was entered — so a deterministic predicate leaves no
/// dangling choice point and backtracking never re-enters it.</para>
///
/// <para>The transform runs <em>after</em> DCG / meta / phrase
/// expansion (it appends to the final, plain-rule body) and is gated
/// on the engine's <see cref="ModeTable"/>. Predicates with no mode
/// declaration, or with any multi / nondet mode, pass through
/// untouched — exactly the conservative position ADR-012 wants:
/// the declaration is a contract, and a predicate that never promised
/// determinism keeps full backtracking.</para>
/// </summary>
public static class ModeSpecializationTransform
{
    /// <summary>Returns a clause list where every clause of an
    /// all-deterministic predicate carries an implicit trailing cut.
    /// Directives and DCG rules pass through unchanged (DCG rules
    /// should already be expanded by the time this runs; they're
    /// copied verbatim defensively). When <paramref name="modes"/> has
    /// no declarations the input list is returned as-is.</summary>
    public static List<Clause> Apply(IEnumerable<Clause> clauses, ModeTable modes)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(modes);

        var result = new List<Clause>();
        foreach (var clause in clauses)
        {
            if ((clause.Kind == ClauseKind.Fact || clause.Kind == ClauseKind.Rule)
                && TryHeadFunctorId(clause, out int functorId)
                && modes.AllModesDeterministic(functorId))
            {
                result.Add(AppendCut(clause));
            }
            else
            {
                result.Add(clause);
            }
        }
        return result;
    }

    /// <summary>Rewrites one clause to carry a trailing cut. A fact
    /// becomes a rule whose body is just <c>!</c>; a rule gets its body
    /// conjoined with <c>!</c> on the right.</summary>
    private static Clause AppendCut(Clause clause)
    {
        var cut = new AtomTerm("!") { Position = clause.Position };

        if (clause.Kind == ClauseKind.Fact)
        {
            // H.  →  (H :- !).
            var rule = new CompoundTerm(":-", new Term[] { clause.Term, cut })
            {
                Position = clause.Position,
            };
            return new Clause(ClauseKind.Rule, rule, clause.Position);
        }

        // Rule: (:- H B)  →  (:- H (B, !)).
        var ruleTerm = (CompoundTerm)clause.Term;
        Term head = ruleTerm.Args[0];
        Term body = ruleTerm.Args[1];
        var newBody = new CompoundTerm(",", new[] { body, (Term)cut })
        {
            Position = clause.Position,
        };
        var newRule = new CompoundTerm(":-", new[] { head, (Term)newBody })
        {
            Position = clause.Position,
        };
        return new Clause(ClauseKind.Rule, newRule, clause.Position);
    }

    /// <summary>Interns the global functor id for a clause's head, or
    /// returns false when the head isn't an atom / compound (shouldn't
    /// happen for a well-formed Fact / Rule, but the guard keeps the
    /// transform total).</summary>
    private static bool TryHeadFunctorId(Clause clause, out int functorId)
    {
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        switch (head)
        {
            case AtomTerm a:
                functorId = FunctorTable.Intern(
                    AtomTable.Intern(a.Name, permanent: true).Id, 0);
                return true;
            case CompoundTerm c:
                functorId = FunctorTable.Intern(
                    AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                return true;
            default:
                functorId = 0;
                return false;
        }
    }
}
