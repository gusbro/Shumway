using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// Renders one solution the way a Prolog top level does: bindings for the
/// variables that got values, and residual goals — a constraint library's
/// projected attributes — for the ones that are still constrained rather than
/// bound. So <c>?- A #&gt; 5, A #&lt; 10.</c> answers <c>A in 6..9</c> instead
/// of leaving the user with a bare unbound variable.
/// </summary>
public static class SolutionFormatter
{
    /// <summary>Shortens a term for DISPLAY, the way a top level does: a list
    /// keeps <paramref name="limit"/> elements and ends in <c>|...</c>, a
    /// subterm nested deeper than that shows as <c>...</c>, and the answer as
    /// a WHOLE shows at most <paramref name="limit"/> items. A limit of zero
    /// leaves the term alone.
    ///
    /// <para>That last cap is the one that makes the promise hold. Per-list is
    /// not a bound on the output: a list of lists shows limit x limit items, so
    /// `findall(L, (between(1, 1000, X), length(L, X)), Ls)` came out at 55,000
    /// characters with every rule above respected. One budget spent left to
    /// right bounds it whatever shape the term has, and spends what it has on
    /// the front of the answer, which is the part being read.</para>
    ///
    /// <para>Elision belongs here and not in the writer: <c>write/1</c> prints
    /// what it is given, because a program's output is not a summary of itself.
    /// An ANSWER is read by a person, and <c>numlist(1, 10000000, X)</c> has one
    /// nobody wants in full.</para></summary>
    public static Term Elide(Term term, int limit)
    {
        if (limit <= 0) return term;
        int budget = limit;
        return Walk(term, limit, limit, ref budget);

        // `depth` is what is left of the nesting allowance, `limit` how many
        // elements one list may show, `budget` how many items the whole answer
        // has left. Passed rather than captured, so this stays a static local
        // function and allocates no closure.
        static Term Walk(Term t, int depth, int limit, ref int budget)
        {
            if (depth <= 0) return new AtomTerm("...");
            if (t is not CompoundTerm c) return t;

            if (c is { Functor: ".", Args.Length: 2 })
            {
                // Along the spine, not into the tail: the whole point is lists
                // long enough that one C# frame each would not fit.
                var kept = new List<Term>();
                Term cursor = t;
                while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons
                       && kept.Count < limit && budget > 0)
                {
                    budget--;
                    kept.Add(Walk(cons.Args[0], depth - 1, limit, ref budget));
                    cursor = cons.Args[1];
                }
                // Anything left becomes the improper tail `|...`, which is how
                // every top level says "there is more" -- whether what stopped
                // us was this list's own length or the answer's total.
                Term tail = cursor is CompoundTerm { Functor: ".", Args.Length: 2 }
                    ? new AtomTerm("...")
                    : Walk(cursor, depth - 1, limit, ref budget);
                for (int i = kept.Count - 1; i >= 0; i--)
                    tail = new CompoundTerm(".", new[] { kept[i], tail });
                return tail;
            }

            var args = new Term[c.Args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (budget <= 0) { args[i] = new AtomTerm("..."); continue; }
                budget--;
                args[i] = Walk(c.Args[i], depth - 1, limit, ref budget);
            }
            return new CompoundTerm(c.Functor, args);
        }
    }

    /// <summary>Builds a Prolog list AST from a sequence of terms.</summary>
    public static Term MakeList(IList<Term> elements)
    {
        Term tail = new AtomTerm("[]");
        for (int i = elements.Count - 1; i >= 0; i--)
            tail = new CompoundTerm(".", new[] { elements[i], tail });
        return tail;
    }

    /// <summary>Formats a solution: bindings for vars that got values,
    /// residual goals (substituted to mention the original variable
    /// names) for vars that are still attvar-constrained.</summary>
    public static string Format(
        PrologEngine engine, Solution solution, IReadOnlyList<string> userVars, int width)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(userVars);

        var ops = engine.Operators;
        int elide = engine.Flags.AnswerMaxDepth;
        if (userVars.Count == 0)
            return solution.Bindings.Count == 0 ? "true" : solution.ToString(width);

        // An unbound user variable's value is an engine variable `_Gn`; wherever
        // that same `_Gn` turns up inside ANOTHER variable's value, it is the
        // variable the user named. Rendering it as its name is what makes
        // `Y = f(X)` read as f of X rather than f of something anonymous.
        var displayName = new Dictionary<string, string>();
        foreach (string name in userVars)
            if (solution[name] is VarTerm ov) displayName.TryAdd(ov.Name, name);

        // Copy-name -> the name the answer shows, from the copies binding (a list
        // `[Copy1, Copy2, …]` aligned with userVars), walking each copy against the
        // value it was copied from. Roots go first so a user variable's own name
        // always wins over an occurrence of it nested in some other value.
        var copyToOriginal = new Dictionary<string, string>();
        var copies = ResidualProjection.ListElements(
            solution[QueryWrapper.CopiesVarName]).ToList();
        for (int i = 0; i < copies.Count && i < userVars.Count; i++)
            if (copies[i] is VarTerm cv) copyToOriginal.TryAdd(cv.Name, userVars[i]);
        for (int i = 0; i < copies.Count && i < userVars.Count; i++)
            ResidualProjection.MapCopyNames(
                copies[i], solution[userVars[i]], userVars[i], copyToOriginal);
        // A copy that landed on an engine variable the user did name shows the name.
        foreach (string key in copyToOriginal.Keys.ToList())
            if (displayName.TryGetValue(copyToOriginal[key], out string? shown))
                copyToOriginal[key] = shown;

        // Collect residual goals and substitute copy-vars back to originals.
        var residuals = new List<Term>();
        Term? resTerm = solution[QueryWrapper.ResidualVarName];
        Term resCursor = resTerm ?? new AtomTerm("[]");
        while (resCursor is CompoundTerm { Functor: ".", Args.Length: 2 } rc)
        {
            residuals.Add(ResidualProjection.SubstituteVarNames(rc.Args[0], copyToOriginal));
            resCursor = rc.Args[1];
        }

        // For each user var: if it has residuals mentioning it, those
        // replace the binding line; otherwise show the binding (unless
        // the binding is an unbound var with no residuals, in which case
        // skip it — that's what SWI does).
        var residualsByVar = new Dictionary<string, List<Term>>();
        var unattachedResiduals = new List<Term>();
        foreach (Term g in residuals)
        {
            string? owner = ResidualProjection.FindMentionedOwner(g, userVars);
            if (owner is null) unattachedResiduals.Add(g);
            else
            {
                if (!residualsByVar.TryGetValue(owner, out var list))
                    residualsByVar[owner] = list = new List<Term>();
                list.Add(g);
            }
        }

        // Cyclic-term display: a cycle that re-enters at the root of a user
        // variable's value renders as that variable — `A = [a, b | A]` — by
        // mapping the materializer's `_C{addr}` marker back to the variable
        // whose value is rooted at that address.
        Dictionary<string, string>? cycleNames = null;
        if (solution.ValueRootAddresses is { } rootAddrs)
            foreach (string name in userVars)
                if (rootAddrs.TryGetValue(name, out int addr))
                    (cycleNames ??= new Dictionary<string, string>())
                        .TryAdd($"_C{addr}", name);

        // SWI-style binding display: user vars whose values are identical are
        // CHAINED — `A = B, B = algo` instead of `A = algo, B = algo` — and
        // two vars sharing one still-unbound variable show their aliasing
        // (`A = B.`) instead of nothing. A lone unbound var stays omitted.
        var renderedValue = new Dictionary<string, string>();
        var groups = new Dictionary<string, List<string>>();
        foreach (string name in userVars)
        {
            Term? val = solution[name];
            if (val is null || residualsByVar.ContainsKey(name)) continue;
            if (cycleNames is not null) val = ResidualProjection.SubstituteVarNames(val, cycleNames);
            // A named variable occurring inside this value prints as its name.
            if (displayName.Count > 0)
                val = ResidualProjection.SubstituteVarNames(val, displayName);
            string key = AstTermRenderer.Render(Elide(val, elide), 1200, ops);
            renderedValue[name] = key;
            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = new List<string>();
            members.Add(name);
        }

        var lines = new List<string>();
        var groupEmitted = new HashSet<string>();
        foreach (string name in userVars)
        {
            if (residualsByVar.TryGetValue(name, out var rs))
            {
                foreach (Term g in rs)
                    lines.Add(AstTermRenderer.Render(Elide(g, elide), 1200, ops));
                continue;
            }
            if (!renderedValue.TryGetValue(name, out string? key)
                || !groupEmitted.Add(key))
                continue;   // no value, or its group was already emitted
            var members = groups[key];
            for (int i = 0; i + 1 < members.Count; i++)
                lines.Add($"{members[i]} = {members[i + 1]}");
            // The last member carries the value — unless the shared value is
            // itself an unbound variable (the chain alone says it all).
            if (solution[members[^1]] is not VarTerm)
                lines.Add($"{members[^1]} = {key}");
        }
        foreach (Term g in unattachedResiduals)
            lines.Add(AstTermRenderer.Render(Elide(g, elide), 1200, ops));

        if (lines.Count == 0) return "true";
        return string.Join(",\n", lines);
    }
}
