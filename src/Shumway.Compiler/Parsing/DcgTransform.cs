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
/// branch fired. Users invoke the resulting predicate
/// directly via its expanded arity — for instance, after
/// <c>sentence --&gt; noun_phrase, verb_phrase.</c> the query is
/// <c>?- sentence(Input, []).</c>. A <c>phrase/2</c> wrapper that
/// hides this is a separate concern.</para>
/// </summary>
public static class DcgTransform
{
    /// <summary><paramref name="failFast"/> gates the leading-terminal hoist +
    /// compound-head-arg defer below. Debug codegen passes false: the hoist
    /// ERASES the first terminal's body goal (it becomes head unification), so
    /// its source line loses its debug stop site — the one transform outcome
    /// ADR-035's position-coverage invariant forbids.</summary>
    public static List<Clause> Apply(IEnumerable<Clause> clauses, bool failFast = true)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var result = new List<Clause>();
        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.DcgRule
                && clause.Term is CompoundTerm { Functor: "-->" } dcgTerm
                && dcgTerm.Args.Length == 2)
            {
                result.Add(TransformRule(dcgTerm.Args[0], dcgTerm.Args[1], clause.Position, failFast));
            }
            else
            {
                result.Add(clause);
            }
        }
        return result;
    }

    private static Clause TransformRule(Term head, Term body, Shumway.Compiler.Lexer.SourcePosition position,
        bool failFast)
    {
        int counter = 0;
        var sStart = new VarTerm("$S0");

        // Semicontext (pushback) head: `Head, PushBack --> Body`. The
        // pushback terminal list is "put back" onto the residue so the
        // *next* non-terminal sees it. Standard DCG expansion:
        //   Head(S0, S) :- Body(S0, S1), S = PushBack ++ S1.
        // So after Body consumes S0→S1, S is the pushback list prepended
        // to S1 (e.g. `nt, [t] --> [t].` gives `nt(S0,S):-S0=[T|S1],S=[T|S1]`,
        // a pure lookahead).
        if (head is CompoundTerm { Functor: ",", Args.Length: 2 } semi)
        {
            Term realHead = semi.Args[0];
            Term pushBack = semi.Args[1];
            var sFinal = FreshState(ref counter);
            (Term tBody, VarTerm sMid) = TransformBody(body, sStart, ref counter);
            Term consChain = BuildPushbackList(pushBack, sMid);
            Term link = new CompoundTerm("=", new[] { (Term)sFinal, consChain })
            { Position = pushBack.Position };
            Term nHead = AppendDiffListArgs(realHead, sStart, sFinal);
            Term nBody = new CompoundTerm(",", new[] { tBody, link });
            return new Clause(ClauseKind.Rule,
                new CompoundTerm(":-", new[] { nHead, nBody }), position);
        }

        // Fail-fast lowering for a terminal-led body (Djota/DCG parser
        // hot path). A rule like
        //     insert_ast_([insert(Str,Attrs)|Ast0]) --> "{+", seq(Str), ...
        // normally expands so the head builds the [insert(..)|..] output
        // structure *before* the body checks the input starts with "{+".
        // When such a clause is one of many alternatives tried per input
        // position (the inline-parser dispatch), every failed alternative
        // wastefully builds its output structure and then fails.
        //
        // Two coordinated moves, applied ONLY when the body begins with a
        // terminal (so there is an input test to fail on early):
        //   (a) hoist the leading ground terminal(s) into the head's input
        //       argument, so a non-matching input fails at head unification
        //       (before the frame is even allocated); and
        //   (b) defer construction of each COMPOUND head output argument
        //       into the body (`Vi = Origi`), placed after the head match,
        //       so the output structure is only built once the input has
        //       matched.
        // Render-direction rules (`ast_html_node_(paragraph(..)) --> {..}, ...`)
        // begin with a `{ }` goal, NOT a terminal, so neither move fires and
        // their first-argument indexing on the bound AST node is preserved.
        (Term inputArg, Term residualBody, VarTerm residualStart, bool peeled) =
            failFast ? PeelLeadingTerminals(body, sStart, ref counter)
                     : (sStart, body, sStart, false);

        Term effectiveHead = head;
        var deferGoals = new List<Term>();
        if (peeled)
            effectiveHead = DeferCompoundArgs(head, deferGoals, ref counter);

        (Term transformedBody, VarTerm sEnd) = TransformBody(residualBody, residualStart, ref counter);
        Term newHead = AppendDiffListArgs(effectiveHead, inputArg, sEnd);
        Term finalBody = transformedBody;
        for (int i = deferGoals.Count - 1; i >= 0; i--)
            finalBody = new CompoundTerm(",", new[] { deferGoals[i], finalBody });
        Term newRuleTerm = new CompoundTerm(":-", new[] { newHead, finalBody });
        return new Clause(ClauseKind.Rule, newRuleTerm, position);
    }

    /// <summary>Peels leading ground terminals off <paramref name="body"/>'s
    /// conjunction spine into a single head-input list <c>[e… | residualStart]</c>.
    /// Returns the term to place in the head's input argument (either the
    /// original <paramref name="sStart"/> when nothing was peeled, or the cons
    /// chain), the residual body with those terminals removed, the state
    /// variable the residual threads from, and whether anything was peeled.</summary>
    private static (Term inputArg, Term residualBody, VarTerm residualStart, bool peeled)
        PeelLeadingTerminals(Term body, VarTerm sStart, ref int counter)
    {
        var elems = new List<Term>();
        Term cur = body;
        while (true)
        {
            Term first = cur is CompoundTerm { Functor: ",", Args.Length: 2 } c ? c.Args[0] : cur;
            Term? rest = cur is CompoundTerm { Functor: ",", Args.Length: 2 } c2 ? c2.Args[1] : null;
            if (TryTerminalElements(first, out var these))
            {
                elems.AddRange(these);
                if (rest is null) { cur = new AtomTerm("true"); break; }
                cur = rest;
            }
            else break;
        }
        if (elems.Count == 0)
            return (sStart, body, sStart, false);
        var sAfter = FreshState(ref counter);
        Term inputArg = sAfter;
        for (int i = elems.Count - 1; i >= 0; i--)
            inputArg = new CompoundTerm(".", new[] { elems[i], inputArg });
        return (inputArg, cur, sAfter, true);
    }

    /// <summary>Recognises a DCG terminal that consumes a fixed prefix of the
    /// input: a proper cons list (its elements) or a non-empty double-quoted
    /// literal (its characters, as chars or as codes per the literal's own
    /// presentation — ADR-047). Returns false for the empty terminal, improper
    /// lists, and non-terminals.</summary>
    private static bool TryTerminalElements(Term term, out List<Term> elems)
    {
        elems = new List<Term>();
        if (term is StringTerm s)
        {
            if (s.Content.Length == 0) return false;
            foreach (char ch in s.Content) elems.Add(TextElement(ch, s.Kind));
            return true;
        }
        // Proper cons list [e1, …, en].
        Term t = term;
        while (t is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            elems.Add(cons.Args[0]);
            t = cons.Args[1];
        }
        if (t is AtomTerm { Name: "[]" } && elems.Count > 0) return true;
        elems.Clear();
        return false;
    }

    /// <summary>Replaces each COMPOUND top-level head argument with a fresh
    /// variable and records a <c>V = OrigArg</c> goal, so the output structure
    /// is built in the body (after the head input match) rather than during
    /// head unification. Atom / integer / variable head arguments are left in
    /// place so first-argument indexing on a bound scalar still discriminates.</summary>
    private static Term DeferCompoundArgs(Term head, List<Term> deferGoals, ref int counter)
    {
        if (head is not CompoundTerm hc) return head;
        Term[]? newArgs = null;
        for (int i = 0; i < hc.Args.Length; i++)
        {
            if (hc.Args[i] is CompoundTerm)
            {
                newArgs ??= (Term[])hc.Args.Clone();
                var v = new VarTerm($"$O{counter++}");
                newArgs[i] = v;
                deferGoals.Add(new CompoundTerm("=", new[] { (Term)v, hc.Args[i] })
                { Position = hc.Position });
            }
        }
        return newArgs is null ? head : new CompoundTerm(hc.Functor, newArgs) { Position = hc.Position };
    }

    /// <summary>One element of a text literal, as the literal's own
    /// presentation says: a terminal written under <c>double_quotes=chars</c>
    /// must match one-character atoms, not codes.</summary>
    private static Term TextElement(char ch, Shumway.Core.TextKind kind) =>
        kind == Shumway.Core.TextKind.Chars
            ? new AtomTerm(ch.ToString())
            : new IntTerm(ch);

    /// <summary>Builds "PushBack ++ Tail" as a cons chain, for a semicontext
    /// head's pushback list. Accepts a cons list, <c>[]</c>, or a
    /// double-quoted literal (expanded to its own presentation).</summary>
    private static Term BuildPushbackList(Term pushBack, Term tail)
    {
        if (pushBack is StringTerm s)
        {
            Term acc = tail;
            for (int i = s.Content.Length - 1; i >= 0; i--)
                acc = new CompoundTerm(".", new Term[] { TextElement(s.Content[i], s.Kind), acc });
            return acc;
        }
        return BuildListWithTail(pushBack, tail);
    }

    private static (Term body, VarTerm sEnd) TransformBody(
        Term body, VarTerm sIn, ref int counter)
    {
        // Conjunction: thread the diff-list left-to-right.
        if (body is CompoundTerm { Functor: "," } conj && conj.Args.Length == 2)
        {
            var (left, sMid) = TransformBody(conj.Args[0], sIn, ref counter);
            var (right, sOut) = TransformBody(conj.Args[1], sMid, ref counter);
            return (new CompoundTerm(",", new[] { left, right }) { Position = body.Position }, sOut);
        }

        // Empty terminal [] — consume nothing, no goal emitted.
        if (body is AtomTerm { Name: "[]" })
            return (new AtomTerm("true"), sIn);

        // Bare `true` — consumes nothing (the identity grammar step). This
        // also covers the residual left when the whole body was peeled into
        // the head input argument by the fail-fast lowering above.
        if (body is AtomTerm { Name: "true" })
            return (body, sIn);

        // Non-empty terminal list — emit "sIn = [..elements.. | sOut]".
        if (IsCons(body))
        {
            var sOut = FreshState(ref counter);
            Term listWithTail = BuildListWithTail(body, sOut);
            Term goal = new CompoundTerm("=", new[] { (Term)sIn, listWithTail })
                { Position = body.Position };
            return (goal, sOut);
        }

        // Double-quoted string terminal (standard DCG, not
        // dialect-gated). Since ADR-047 the literal survives as a
        // StringTerm under EVERY double_quotes mode, carrying its own
        // presentation kind — the elements it consumes must match it
        // (chars under `chars`, codes under `codes`/`string`), exactly
        // like the leading-terminal hoist above. Hardcoding codes here
        // was a pre-ADR-047 leftover: under the chars default a
        // NON-LEADING terminal ("]" after a nonterminal — Trealla's
        // json grammar) silently failed against chars input. The empty
        // string "" consumes nothing (S0 = S), mirroring the [] empty
        // terminal.
        if (body is StringTerm str)
        {
            if (str.Content.Length == 0)
                return (new AtomTerm("true"), sIn);
            var sOut = FreshState(ref counter);
            Term acc = sOut;
            for (int i = str.Content.Length - 1; i >= 0; i--)
                acc = new CompoundTerm(".",
                    new Term[] { TextElement(str.Content[i], str.Kind), acc });
            Term goal = new CompoundTerm("=", new[] { (Term)sIn, acc }) { Position = body.Position };
            return (goal, sOut);
        }

        // Prolog escape: { G } — emit G as a plain goal, don't thread sIn.
        if (body is CompoundTerm { Functor: "{}" } brace && brace.Args.Length == 1)
            return (brace.Args[0], sIn);

        // Cut — doesn't consume input.
        if (body is AtomTerm { Name: "!" })
            return (body, sIn);

        // Disjunction: each branch consumes the same input range and must end
        // at ONE shared diff-list endpoint. A branch whose
        // endpoint is a FRESH state variable (the common case: it consumed
        // something) gets the shared endpoint SUBSTITUTED into its body
        // directly, like SWI/GProlog's expander; only a branch whose endpoint
        // is still sIn (consumed nothing: `{G}`, `!`, `[]`) keeps an explicit
        // `SShared = SIn` reconciliation goal. This drops the two per-branch
        // `=/2` goals (→ get_variable/unify pairs) the old form always paid.
        // Disjunction — both `;/2` and the DCG-standard `|/2` alternative
        // (Scryer / SWI accept `(A | B)` in grammar bodies).
        if (body is CompoundTerm { Args.Length: 2 } disj
            && (disj.Functor == ";" || disj.Functor == "|"))
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
                // The then-branch's endpoint may have been minted by the
                // CONDITION (a state-consuming nonterminal condition with a
                // then part that consumes nothing, e.g. `( nt(X) -> [] ; …)`):
                // the endpoint variable then occurs only in `cond`, so the
                // merge substitution must cover the (cond, then) PAIR. Renaming
                // the then part alone silently no-opped and left the shared
                // endpoint UNBOUND — every goal after the if-then-else ran on a
                // dangling, freshly-invented state (clpz's propagator queue
                // vanished this way: enable_queue re-enabled a phantom queue).
                Term condFinal, thenFinal;
                if (sOutA is VarTerm thenOut
                    && (sIn is not VarTerm sInV || thenOut.Name != sInV.Name))
                {
                    condFinal = RenameVar(cond, thenOut.Name, sOutMerged);
                    thenFinal = RenameVar(then, thenOut.Name, sOutMerged);
                }
                else
                {
                    // Nothing consumed by cond+then: reconcile explicitly,
                    // INSIDE the then arm (sIn is used outside the branch and
                    // cannot be renamed).
                    condFinal = cond;
                    thenFinal = new CompoundTerm(",", new[]
                    {
                        then,
                        new CompoundTerm("=", new[] { (Term)sOutMerged, sOutA })
                        { Position = then.Position },
                    }) { Position = then.Position };
                }
                Term elseWithMerge = MergeBranchEndpoint(elseBody, sOutB, sOutMerged, sIn);
                Term newIte = new CompoundTerm(";", new[] {
                    new CompoundTerm("->", new[] { condFinal, thenFinal }),
                    elseWithMerge
                }) { Position = body.Position };
                return (newIte, sOutMerged);
            }

            // Plain disjunction A ; B. Both branches thread from sIn to the
            // same shared sOut.
            var (left2, sOutL) = TransformBody(disj.Args[0], sIn, ref counter);
            var (right2, sOutR) = TransformBody(disj.Args[1], sIn, ref counter);
            var sOutShared = FreshState(ref counter);
            Term leftMerged = MergeBranchEndpoint(left2, sOutL, sOutShared, sIn);
            Term rightMerged = MergeBranchEndpoint(right2, sOutR, sOutShared, sIn);
            return (new CompoundTerm(";", new[] { leftMerged, rightMerged })
                { Position = body.Position }, sOutShared);
        }

        // Bare if-then without an else branch: (A -> B). Treated as the
        // sequential composition A, B (an if-then with no fallback fails
        // when A fails, which the sequential form models exactly).
        if (body is CompoundTerm { Functor: "->" } itoOnly && itoOnly.Args.Length == 2)
        {
            var (cond, sMid) = TransformBody(itoOnly.Args[0], sIn, ref counter);
            var (then, sOut) = TransformBody(itoOnly.Args[1], sMid, ref counter);
            return (new CompoundTerm("->", new[] { cond, then }) { Position = body.Position }, sOut);
        }

        // Negation: `\+ NT` — succeeds iff NT cannot parse from sIn. The
        // outer state stays at sIn (no input is consumed, even if NT
        // would have advanced it). We thread a fresh sigma for the
        // discarded NT output so the inner transform produces a valid
        // diff-list endpoint that we then throw away.
        if (body is CompoundTerm { Functor: "\\+" } neg && neg.Args.Length == 1)
        {
            var (inner, _) = TransformBody(neg.Args[0], sIn, ref counter);
            return (new CompoundTerm("\\+", new[] { inner }) { Position = body.Position }, sIn);
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
            return (new CompoundTerm("call", newArgs) { Position = body.Position }, sOut);
        }

        // Lookahead: `peek(X)` — succeeds iff X is the next
        // element of the input, consuming nothing. Transforms to
        // `sIn = [X | _]` so the head of the diff-list state is
        // pattern-matched but the state itself stays at sIn.
        if (body is CompoundTerm { Functor: "peek" } peek && peek.Args.Length == 1)
        {
            Term peekList = new CompoundTerm(".", new[] {
                peek.Args[0],
                new VarTerm("_")
            });
            Term unifyGoal = new CompoundTerm("=", new[] { (Term)sIn, peekList })
                { Position = body.Position };
            return (unifyGoal, sIn);
        }

        // Pushback: `pushback(L)` — extends the diff-list
        // residue by prepending the elements of L, so the *next*
        // non-terminal sees them. After `a --> [x], pushback([y]).`,
        // calling `a([x, z], R)` yields R = [y, z] (the y was pushed
        // back into the residue, the z was already there). Transforms
        // to `sOut = [y | sIn]` materialised as a cons chain.
        if (body is CompoundTerm { Functor: "pushback" } pb && pb.Args.Length == 1)
        {
            var sOut = FreshState(ref counter);
            Term consChain = BuildConsChainEndingIn(pb.Args[0], sIn);
            Term goal = new CompoundTerm("=", new[] { (Term)sOut, consChain })
                { Position = body.Position };
            return (goal, sOut);
        }

        // Variable non-terminal — a body that is a variable expands to a
        // runtime phrase/3 call (standard DCG: `--> V` means
        // `phrase(V, S0, S)`). The prelude phrase/3 interpreter handles the
        // list case (V bound to a terminal list) and the callable case.
        if (body is VarTerm)
        {
            var sOut = FreshState(ref counter);
            Term goal = new CompoundTerm("phrase", new[] { body, (Term)sIn, sOut })
                { Position = body.Position };
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

    /// <summary>Makes a disjunction branch end at the shared
    /// endpoint. When the branch's own endpoint is a FRESH `$Sn` variable
    /// (it consumed input), the shared variable is substituted for it in the
    /// branch body — no reconciliation goal. When the endpoint is still
    /// <paramref name="sIn"/> (nothing consumed), an explicit
    /// <c>Shared = SIn</c> goal remains, since sIn is used outside the branch
    /// and cannot be renamed.</summary>
    private static Term MergeBranchEndpoint(Term branch, Term branchOut, VarTerm shared, Term sIn)
    {
        if (branchOut is VarTerm bv
            && (sIn is not VarTerm siv || bv.Name != siv.Name))
            return RenameVar(branch, bv.Name, shared);
        return new CompoundTerm(",", new[]
        {
            branch,
            new CompoundTerm("=", new[] { (Term)shared, branchOut })
            { Position = branch.Position },
        }) { Position = branch.Position };
    }

    // Replaces every occurrence of the variable named `from` with `to`.
    // Fresh `$Sn` names are unique per transform, so a name-based rename of a
    // branch-local endpoint cannot capture anything else.
    private static Term RenameVar(Term t, string from, VarTerm to) => t switch
    {
        VarTerm v when v.Name == from => to,
        CompoundTerm c => RenameVarInCompound(c, from, to),
        _ => t,
    };

    private static Term RenameVarInCompound(CompoundTerm c, string from, VarTerm to)
    {
        Term[]? newArgs = null;
        for (int i = 0; i < c.Args.Length; i++)
        {
            Term renamed = RenameVar(c.Args[i], from, to);
            if (!ReferenceEquals(renamed, c.Args[i]))
            {
                newArgs ??= (Term[])c.Args.Clone();
                newArgs[i] = renamed;
            }
        }
        return newArgs is null ? c
            : new CompoundTerm(c.Functor, newArgs) { Position = c.Position };
    }

    /// <summary>Builds <c>[l1, l2, …, ln | tail]</c> for a list term
    /// <paramref name="listTerm"/> ending in <paramref name="tail"/>.
    /// Used by the DCG pushback transform to splice the
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
            // A module-qualified nonterminal `M:NT` (SWI's multifile
            // `prolog:message//1` heads): the diff-list args belong to NT,
            // not to the ':' wrapper — appending to ':' fabricates a bogus
            // :/4 head that then trips clause routing. Only when NT is
            // concrete: a RUNTIME `M:Var` keeps the historic outer append
            // (dispatched as a meta-call).
            case CompoundTerm { Functor: ":", Args: [var m, var inner] }
                when inner is AtomTerm or CompoundTerm:
                return new CompoundTerm(":",
                    new[] { m, AppendDiffListArgs(inner, sIn, sOut) })
                    { Position = call.Position };
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
