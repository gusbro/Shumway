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
        if (userVars.Count == 0)
            return solution.Bindings.Count == 0 ? "true" : solution.ToString(width);

        // Build copy-name -> original-name map from the copies binding
        // (a list `[Copy1, Copy2, ...]` aligned with userVars).
        var copyToOriginal = new Dictionary<string, string>();
        Term? copiesTerm = solution[QueryWrapper.CopiesVarName];
        int idx = 0;
        Term cursor = copiesTerm ?? new AtomTerm("[]");
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c
               && idx < userVars.Count)
        {
            if (c.Args[0] is VarTerm v)
                copyToOriginal[v.Name] = userVars[idx];
            cursor = c.Args[1];
            idx++;
        }

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
            string key = AstTermRenderer.Render(val, 1200, ops);
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
                foreach (Term g in rs) lines.Add(AstTermRenderer.Render(g, 1200, ops));
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
            lines.Add(AstTermRenderer.Render(g, 1200, ops));

        if (lines.Count == 0) return "true";
        return string.Join(",\n", lines);
    }
}
