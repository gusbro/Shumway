using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Translates Definite Clause Grammar rules (<c>head --&gt; body</c>) into
/// ordinary Prolog clauses by threading a difference-list pair through the
/// body. Each non-terminal in the body picks up two extra arguments — the
/// input list before its consumption and the remaining list after — wired
/// together so the parser-style "consume input" reading drops out of the
/// standard clausal semantics.
///
/// <para>Supported body forms:</para>
/// <list type="bullet">
/// <item>The empty terminal <c>[]</c> — consumes nothing.</item>
/// <item>A list literal <c>[t1, t2, …]</c> — emits an explicit
///   <c>S0 = [t1, t2, … | S1]</c> goal.</item>
/// <item>Conjunction <c>(A, B)</c> — threads the diff-list left-to-right.</item>
/// <item>The Prolog escape <c>{ G }</c> — emits <c>G</c> as-is without
///   threading the diff-list (so <c>G</c> is an ordinary Prolog goal that
///   doesn't see or affect the input).</item>
/// <item>Cut <c>!</c> — passes through; the cut commits but doesn't consume
///   input.</item>
/// <item>Any other atom or compound — treated as a non-terminal call: two
///   extra args (input and output state) are appended.</item>
/// </list>
///
/// <para>Not yet supported inside DCG bodies: disjunction <c>;/2</c>,
/// if-then <c>-&gt;/2</c>, and raw <c>\+/1</c> (use <c>{ \+ G }</c> as a
/// workaround). Users invoke the resulting predicate directly via its
/// expanded arity — for instance, after <c>sentence --&gt; noun_phrase,
/// verb_phrase.</c> the query is <c>?- sentence(Input, []).</c>. A
/// <c>phrase/2</c> wrapper that hides this is a separate concern that needs
/// runtime meta-call support.</para>
/// </summary>
public static class DcgTransform
{
    public static List<Clause> Apply(IEnumerable<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var result = new List<Clause>();
        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.DcgRule
                && clause.Term is CompoundTerm { Functor: "-->" } dcgTerm
                && dcgTerm.Args.Length == 2)
            {
                result.Add(TransformRule(dcgTerm.Args[0], dcgTerm.Args[1], clause.Position));
            }
            else
            {
                result.Add(clause);
            }
        }
        return result;
    }

    private static Clause TransformRule(Term head, Term body, Shumway.Compiler.Lexer.SourcePosition position)
    {
        int counter = 0;
        var sStart = new VarTerm("$S0");

        (Term transformedBody, VarTerm sEnd) = TransformBody(body, sStart, ref counter);
        Term newHead = AppendDiffListArgs(head, sStart, sEnd);
        Term newRuleTerm = new CompoundTerm(":-", new[] { newHead, transformedBody });
        return new Clause(ClauseKind.Rule, newRuleTerm, position);
    }

    private static (Term body, VarTerm sEnd) TransformBody(
        Term body, VarTerm sIn, ref int counter)
    {
        // Conjunction: thread the diff-list left-to-right.
        if (body is CompoundTerm { Functor: "," } conj && conj.Args.Length == 2)
        {
            var (left, sMid) = TransformBody(conj.Args[0], sIn, ref counter);
            var (right, sOut) = TransformBody(conj.Args[1], sMid, ref counter);
            return (new CompoundTerm(",", new[] { left, right }), sOut);
        }

        // Empty terminal [] — consume nothing, no goal emitted.
        if (body is AtomTerm { Name: "[]" })
            return (new AtomTerm("true"), sIn);

        // Non-empty terminal list — emit "sIn = [..elements.. | sOut]".
        if (IsCons(body))
        {
            var sOut = FreshState(ref counter);
            Term listWithTail = BuildListWithTail(body, sOut);
            Term goal = new CompoundTerm("=", new[] { (Term)sIn, listWithTail });
            return (goal, sOut);
        }

        // Prolog escape: { G } — emit G as a plain goal, don't thread sIn.
        if (body is CompoundTerm { Functor: "{}" } brace && brace.Args.Length == 1)
            return (brace.Args[0], sIn);

        // Cut — doesn't consume input.
        if (body is AtomTerm { Name: "!" })
            return (body, sIn);

        // Non-terminal call — append (sIn, sOut) to the call's args.
        {
            var sOut = FreshState(ref counter);
            Term newCall = AppendDiffListArgs(body, sIn, sOut);
            return (newCall, sOut);
        }
    }

    private static VarTerm FreshState(ref int counter)
    {
        counter++;
        return new VarTerm($"$S{counter}");
    }

    private static Term AppendDiffListArgs(Term call, Term sIn, Term sOut)
    {
        switch (call)
        {
            case AtomTerm a:
                return new CompoundTerm(a.Name, new[] { sIn, sOut }) { Position = call.Position };
            case CompoundTerm c:
                var args = new Term[c.Args.Length + 2];
                Array.Copy(c.Args, args, c.Args.Length);
                args[c.Args.Length] = sIn;
                args[c.Args.Length + 1] = sOut;
                return new CompoundTerm(c.Functor, args) { Position = call.Position };
            default:
                throw new InvalidOperationException(
                    $"DcgTransform: cannot add diff-list args to {call.GetType().Name}.");
        }
    }

    private static bool IsCons(Term t) =>
        t is CompoundTerm { Functor: "." } c && c.Args.Length == 2;

    private static Term BuildListWithTail(Term list, Term tail)
    {
        if (list is AtomTerm { Name: "[]" })
            return tail;
        if (list is CompoundTerm { Functor: "." } cons && cons.Args.Length == 2)
        {
            Term newTail = BuildListWithTail(cons.Args[1], tail);
            return new CompoundTerm(".", new[] { cons.Args[0], newTail }) { Position = list.Position };
        }
        // Improper list — shouldn't happen for a DCG terminal but propagate
        // gracefully by leaving the tail attached.
        return new CompoundTerm(".", new[] { list, tail });
    }
}
