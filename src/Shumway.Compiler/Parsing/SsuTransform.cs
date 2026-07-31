using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Single-sided-unification (SSU) rules — a first-class clause form of the
/// engine, alongside <c>:-</c> and DCG <c>--&gt;</c>. An SSU rule is written
/// <c>Head =&gt; Body</c> (committed choice) or <c>Head, Guard =&gt; Body</c>
/// (guarded), and is a committed, pattern-matching clause: once its head matches
/// the goal the rule is selected deterministically (no other clause is tried),
/// which the guard, when present, gates.
///
/// <para>Handled here in the clause pipeline exactly like <c>--&gt;</c>: each SSU
/// rule is rewritten to an ordinary clause with a neck cut for the commit:</para>
/// <list type="bullet">
///   <item><c>Head =&gt; Body</c> → <c>Head :- !, Body</c></item>
///   <item><c>(Head, Guard) =&gt; Body</c> → <c>Head :- Guard, !, Body</c></item>
/// </list>
///
/// <para>The <c>!</c> is the deterministic commit; a guard that fails falls
/// through to the next clause (the cut has not fired yet). Head selection uses
/// full unification, which coincides with SSU's single-sided matching whenever the
/// goal is instantiated enough that the match does not bind a goal variable — the
/// case for a deterministic pattern-matching predicate. Faithful single-sidedness
/// for a partially instantiated call (where full unification would bind a goal
/// variable the match must leave alone) is future work.</para>
/// </summary>
public static class SsuTransform
{
    public static IEnumerable<Clause> Apply(IEnumerable<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        foreach (var c in clauses)
        {
            if (c.Term is CompoundTerm { Functor: "=>", Args: [var lhs, var body] })
                yield return Clause.From(Rewrite(lhs, body, c.Term.Position));
            else
                yield return c;
        }
    }

    private static Term Rewrite(Term lhs, Term body, Lexer.SourcePosition pos)
    {
        var cut = new AtomTerm("!") { Position = pos };
        Term head, newBody;
        // A leading conjunction is Head + Guard; the head is the first conjunct
        // (the predicate being defined), the rest is the guard.
        if (lhs is CompoundTerm { Functor: ",", Args: [var h, var guard] })
        {
            head = h;
            newBody = Conj(guard, Conj(cut, body, pos), pos);   // Guard, !, Body
        }
        else
        {
            head = lhs;
            newBody = Conj(cut, body, pos);                     // !, Body
        }
        return new CompoundTerm(":-", new[] { head, newBody }) { Position = pos };
    }

    private static Term Conj(Term a, Term b, Lexer.SourcePosition pos) =>
        new CompoundTerm(",", new[] { a, b }) { Position = pos };
}
