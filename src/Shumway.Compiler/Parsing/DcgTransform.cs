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
/// <para>Disjunction <c>;/2</c>, if-then-else <c>(A -&gt; B ; C)</c>, and
/// negation <c>\+/1</c> in rule bodies thread the diff-list through
/// each branch and unify their sOut endpoints with a shared output
/// state so the caller sees one diff-list pair regardless of which
/// branch fired (chunk 58). Users invoke the resulting predicate
/// directly via its expanded arity — for instance, after
/// <c>sentence --&gt; noun_phrase, verb_phrase.</c> the query is
/// <c>?- sentence(Input, []).</c>. A <c>phrase/2</c> wrapper that
/// hides this is a separate concern.</para>
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

        // Double-quoted string terminal (chunk 439 — standard DCG, not
        // dialect-gated). Under double_quotes = codes / chars the parser
        // already expanded "ab" into a cons list at parse time, so the
        // terminal-list case above covers those modes. Under the default
        // `string` mode the literal survives as a StringTerm; a DCG
        // terminal must still mean "consume these characters", so expand
        // to the equivalent code-list terminal (Shumway's PSTR is a
        // compact character-code list, so codes are the representation a
        // string already unifies with): "ab" emits
        // sIn = [0'a, 0'b | sOut]; the empty string "" consumes nothing
        // (S0 = S), mirroring the [] empty terminal.
        if (body is StringTerm str)
        {
            if (str.Content.Length == 0)
                return (new AtomTerm("true"), sIn);
            var sOut = FreshState(ref counter);
            Term acc = sOut;
            for (int i = str.Content.Length - 1; i >= 0; i--)
                acc = new CompoundTerm(".", new Term[] { new IntTerm(str.Content[i]), acc });
            Term goal = new CompoundTerm("=", new[] { (Term)sIn, acc });
            return (goal, sOut);
        }

        // Prolog escape: { G } — emit G as a plain goal, don't thread sIn.
        if (body is CompoundTerm { Functor: "{}" } brace && brace.Args.Length == 1)
            return (brace.Args[0], sIn);

        // Cut — doesn't consume input.
        if (body is AtomTerm { Name: "!" })
            return (body, sIn);

        // Disjunction: each branch consumes the same input range. We unify
        // the two branches' outputs with a fresh shared sOut so callers see
        // one diff-list endpoint regardless of which branch fired.
        if (body is CompoundTerm { Functor: ";" } disj && disj.Args.Length == 2)
        {
            // If-then-else: (A -> B ; C). A and B share an intermediate
            // sMid; C runs independently from sIn. Both end at the same
            // sOut.
            if (disj.Args[0] is CompoundTerm { Functor: "->" } itc && itc.Args.Length == 2)
            {
                var (cond, sMid) = TransformBody(itc.Args[0], sIn, ref counter);
                var (then, sOutA) = TransformBody(itc.Args[1], sMid, ref counter);
                var (elseBody, sOutB) = TransformBody(disj.Args[1], sIn, ref counter);
                var sOutMerged = FreshState(ref counter);
                Term thenWithMerge = new CompoundTerm(",", new[] {
                    then,
                    new CompoundTerm("=", new[] { (Term)sOutMerged, sOutA })
                });
                Term elseWithMerge = new CompoundTerm(",", new[] {
                    elseBody,
                    new CompoundTerm("=", new[] { (Term)sOutMerged, sOutB })
                });
                Term newIte = new CompoundTerm(";", new[] {
                    new CompoundTerm("->", new[] { cond, thenWithMerge }),
                    elseWithMerge
                });
                return (newIte, sOutMerged);
            }

            // Plain disjunction A ; B. Both branches thread from sIn to the
            // same shared sOut.
            var (left2, sOutL) = TransformBody(disj.Args[0], sIn, ref counter);
            var (right2, sOutR) = TransformBody(disj.Args[1], sIn, ref counter);
            var sOutShared = FreshState(ref counter);
            Term leftMerged = new CompoundTerm(",", new[] {
                left2,
                new CompoundTerm("=", new[] { (Term)sOutShared, sOutL })
            });
            Term rightMerged = new CompoundTerm(",", new[] {
                right2,
                new CompoundTerm("=", new[] { (Term)sOutShared, sOutR })
            });
            return (new CompoundTerm(";", new[] { leftMerged, rightMerged }), sOutShared);
        }

        // Bare if-then without an else branch: (A -> B). Treated as the
        // sequential composition A, B (an if-then with no fallback fails
        // when A fails, which the sequential form models exactly).
        if (body is CompoundTerm { Functor: "->" } itoOnly && itoOnly.Args.Length == 2)
        {
            var (cond, sMid) = TransformBody(itoOnly.Args[0], sIn, ref counter);
            var (then, sOut) = TransformBody(itoOnly.Args[1], sMid, ref counter);
            return (new CompoundTerm("->", new[] { cond, then }), sOut);
        }

        // Negation: `\+ NT` — succeeds iff NT cannot parse from sIn. The
        // outer state stays at sIn (no input is consumed, even if NT
        // would have advanced it). We thread a fresh sigma for the
        // discarded NT output so the inner transform produces a valid
        // diff-list endpoint that we then throw away.
        if (body is CompoundTerm { Functor: "\\+" } neg && neg.Args.Length == 1)
        {
            var (inner, _) = TransformBody(neg.Args[0], sIn, ref counter);
            return (new CompoundTerm("\\+", new[] { inner }), sIn);
        }

        // Meta-call inside a DCG body: `call(G)` is a non-terminal whose
        // identity is bound at runtime. Transform to `call(G, sIn, sOut)`
        // — the runtime call/N then dispatches to the right diff-list
        // continuation. Higher-arity forms (`call(G, X)` etc.) tack their
        // user args on first, then the diff-list pair.
        if (body is CompoundTerm callForm && callForm.Functor == "call"
            && callForm.Args.Length >= 1)
        {
            var sOut = FreshState(ref counter);
            var newArgs = new Term[callForm.Args.Length + 2];
            Array.Copy(callForm.Args, newArgs, callForm.Args.Length);
            newArgs[callForm.Args.Length] = sIn;
            newArgs[callForm.Args.Length + 1] = sOut;
            return (new CompoundTerm("call", newArgs), sOut);
        }

        // Lookahead: `peek(X)` (chunk 52) — succeeds iff X is the next
        // element of the input, consuming nothing. Transforms to
        // `sIn = [X | _]` so the head of the diff-list state is
        // pattern-matched but the state itself stays at sIn.
        if (body is CompoundTerm { Functor: "peek" } peek && peek.Args.Length == 1)
        {
            Term peekList = new CompoundTerm(".", new[] {
                peek.Args[0],
                new VarTerm("_")
            });
            Term unifyGoal = new CompoundTerm("=", new[] { (Term)sIn, peekList });
            return (unifyGoal, sIn);
        }

        // Pushback: `pushback(L)` (chunk 52) — extends the diff-list
        // residue by prepending the elements of L, so the *next*
        // non-terminal sees them. After `a --> [x], pushback([y]).`,
        // calling `a([x, z], R)` yields R = [y, z] (the y was pushed
        // back into the residue, the z was already there). Transforms
        // to `sOut = [y | sIn]` materialised as a cons chain.
        if (body is CompoundTerm { Functor: "pushback" } pb && pb.Args.Length == 1)
        {
            var sOut = FreshState(ref counter);
            Term consChain = BuildConsChainEndingIn(pb.Args[0], sIn);
            Term goal = new CompoundTerm("=", new[] { (Term)sOut, consChain });
            return (goal, sOut);
        }

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

    /// <summary>Builds <c>[l1, l2, …, ln | tail]</c> for a list term
    /// <paramref name="listTerm"/> ending in <paramref name="tail"/>.
    /// Used by the DCG pushback transform (chunk 52) to splice the
    /// pushed-back elements onto the residual diff-list endpoint.</summary>
    private static Term BuildConsChainEndingIn(Term listTerm, Term tail)
    {
        // Walk the list term left-to-right collecting elements; emit the
        // chain back-to-front so the result is a properly-shaped
        // cons cell. Improper lists (variable / atom tails) are
        // passed through to the unify step which will throw at runtime
        // if the binding doesn't make sense.
        var elements = new List<Term>();
        Term cur = listTerm;
        while (cur is CompoundTerm { Functor: "." } cons && cons.Args.Length == 2)
        {
            elements.Add(cons.Args[0]);
            cur = cons.Args[1];
        }
        if (!(cur is AtomTerm { Name: "[]" }))
            throw new InvalidOperationException(
                "DCG pushback/1 requires a ground proper list of tokens.");
        Term acc = tail;
        for (int i = elements.Count - 1; i >= 0; i--)
            acc = new CompoundTerm(".", new[] { elements[i], acc });
        return acc;
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
