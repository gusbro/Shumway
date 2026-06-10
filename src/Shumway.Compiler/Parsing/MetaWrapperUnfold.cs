using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Chunk 407 (Phase 29, ADR-021 candidate #2) — conservative unfolding of
/// user-defined META-WRAPPER predicates at their call sites.
///
/// <para>Arity-compat programs define control wrappers like
/// <c>ifthen(X,Y) :- X -&gt; !, Y.  ifthen(_,_) :- !.</c> and call them with
/// statically-known goals: <c>ifthen(not(flag), (writeln(A), writeln(B)))</c>.
/// Each such call costs the goal-TERM construction on the heap at the call
/// site, the wrapper's frame + its lowered <c>$disj</c> helper call, and a
/// runtime meta-dispatch per goal argument (Blint: 90 K dispatches + ~1.6 M
/// sandwich opcodes per self-lint). Unfolding the wrapper at the call site —
/// <c>( not(flag) -&gt; writeln(A), writeln(B) ; true )</c> — turns all of it
/// into compile-time-lowered control flow with direct calls.</para>
///
/// <para>v1 is deliberately TEMPLATE-CONSERVATIVE (the user-decided scope; the
/// general single-clause partial-deduction unfold is reserved for the future
/// multi-module phase). A predicate is a wrapper iff its clause group matches
/// exactly one of:</para>
/// <list type="bullet">
/// <item><b>T1 — pure control body</b>: a SINGLE clause whose head arguments
///   are distinct variables and whose body is built solely of <c>,</c> /
///   <c>;</c> / <c>-&gt;</c> / <c>*-&gt;</c> / <c>\+</c> / <c>not</c> over
///   those variables (each used exactly once) and the atoms
///   <c>true</c>/<c>fail</c>/<c>false</c> — no cut, nothing else. Unfolding is
///   a pure resolution step (head args are distinct vars, so head unification
///   is trivial substitution). Covers <c>ifthenelse(X,Y,Z) :- X-&gt;Y;Z.</c></item>
/// <item><b>T2 — if-then with commit</b>: <c>w(C,T) :- C -&gt; !, T.</c> (or
///   <c>C, !, T</c>) plus a catch-all second clause <c>w(_,_) [:- !]</c>.
///   Net semantics ≡ <c>( C -&gt; T ; true )</c>: C succeeds → committed to T
///   (T fails ⇒ w fails, both sides); C fails → second clause / else-true.
///   Covers Blint's <c>ifthen/2</c>.</item>
/// <item><b>T3 — negation</b>: <c>w(G) :- G, !, fail.</c> plus catch-all
///   <c>w(_) [:- !]</c> ≡ <c>\+ G</c>.</item>
/// </list>
///
/// <para>A call site is unfolded only when EVERY argument is a statically
/// callable term (atom or compound). A variable argument means a runtime goal
/// (the wrapper handles it); a number/string argument would raise
/// <c>type_error(callable)</c> lazily at run time inside the wrapper —
/// unfolding would move that error to a different place, so we leave it.
/// Cut opacity is preserved: a <c>!</c> INSIDE a passed goal is opaque both
/// ways (meta-called goal: cut barrier at the call — chunk 88; unfolded: the
/// if-then-else condition is opaque to cut per ISO).</para>
///
/// <para>The wrapper's own clauses are LEFT INTACT and compiled normally — a
/// runtime-constructed goal (<c>call(ifthen(A,B))</c>, <c>=..</c>) must keep
/// dispatching to the standalone predicate (fid-reachability is not statically
/// decidable; the chunk-401 lesson). Only STATIC predicates can match (the
/// drivers run this over the static clause set; dynamic-head clauses are
/// routed elsewhere before the pipeline), so the unfolded semantics can never
/// go stale (static predicates are immutable).</para>
///
/// <para>Runs BEFORE <see cref="ClausePipeline"/> (the unfold inserts
/// <c>-&gt;</c>/<c>;</c>/<c>\+</c> that <see cref="MetaTransform"/> then
/// lowers), driven by whoever holds a whole module's clause list
/// (ConsultString, ShmoCompiler; the linker for cross-module in a later
/// chunk).</para>
/// </summary>
public static class MetaWrapperUnfold
{
    private const int MaxUnfoldDepth = 32;

    /// <summary>Chunk 414 — diag-build-only (<c>-p:ShumwayDiag=true</c> +
    /// <c>SHUMWAY_UNFOLD_DIAG=1</c>): lists the wrappers a registry holds.
    /// Stripped from normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagWrappers(
        Dictionary<(string Name, int Arity), WrapperTemplate> wrappers)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_UNFOLD_DIAG") == "1"
            && wrappers.Count > 0)
            System.Console.Error.WriteLine("[unfold] wrappers: " + string.Join(", ",
                System.Linq.Enumerable.Select(wrappers,
                    kv => $"{kv.Key.Name}/{kv.Key.Arity}")));
    }

    /// <summary>One detected wrapper: the equivalent control template
    /// (a term over the wrapper's own head variables) plus the head-variable
    /// names in argument order, so instantiation is clone-with-substitution.</summary>
    internal sealed class WrapperTemplate
    {
        public required Term Body { get; init; }
        public required string[] ArgVars { get; init; }
    }

    /// <summary>Opaque wrapper registry for the two-phase (cross-module) API:
    /// the linker detects each module's wrappers once, filters them by
    /// visibility, merges (caller-local first), and rewrites every module
    /// against the merged view. See <see cref="DetectRegistry"/>.</summary>
    public sealed class WrapperRegistry
    {
        internal readonly Dictionary<(string Name, int Arity), WrapperTemplate> Map;
        internal WrapperRegistry(Dictionary<(string, int), WrapperTemplate> map) => Map = map;

        public int Count => Map.Count;
        public IEnumerable<(string Name, int Arity)> Keys => Map.Keys;

        /// <summary>A registry restricted to the given predicate indicators
        /// (the linker keeps only a module's PUBLIC wrappers for export).</summary>
        public WrapperRegistry Restrict(System.Func<string, int, bool> keep)
        {
            var m = new Dictionary<(string, int), WrapperTemplate>();
            foreach (var (k, v) in Map)
                if (keep(k.Name, k.Arity)) m[k] = v;
            return new WrapperRegistry(m);
        }

        /// <summary>Merges with <paramref name="fallback"/>; entries in THIS
        /// registry win (caller-module locals shadow global publics).</summary>
        public WrapperRegistry MergeOver(WrapperRegistry fallback)
        {
            var m = new Dictionary<(string, int), WrapperTemplate>(fallback.Map);
            foreach (var (k, v) in Map) m[k] = v;
            return new WrapperRegistry(m);
        }

        public static readonly WrapperRegistry Empty =
            new(new Dictionary<(string, int), WrapperTemplate>());
    }

    /// <summary>Detects the conservative wrapper templates defined in
    /// <paramref name="clauses"/> (one module's static clause list).</summary>
    public static WrapperRegistry DetectRegistry(IReadOnlyList<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        return new WrapperRegistry(DetectWrappers(clauses));
    }

    /// <summary>Applies the unfold over one module's clause list, detecting the
    /// module's own wrappers (the module-local driver). Returns the input list
    /// unchanged (same reference) when nothing changes.</summary>
    public static IReadOnlyList<Clause> Apply(IReadOnlyList<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        return Apply(clauses, DetectRegistry(clauses));
    }

    /// <summary>Applies the unfold against an explicit wrapper registry (the
    /// linker's cross-module driver). Returns the input list unchanged (same
    /// reference) when no call site qualifies.</summary>
    public static IReadOnlyList<Clause> Apply(
        IReadOnlyList<Clause> clauses, WrapperRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(registry);
        var wrappers = registry.Map;
        DiagWrappers(wrappers);
        if (wrappers.Count == 0) return clauses;

        List<Clause>? result = null;
        for (int i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            Clause rewritten = RewriteClause(clause, wrappers);
            if (!ReferenceEquals(rewritten, clause) && result is null)
            {
                result = new List<Clause>(clauses.Count);
                for (int j = 0; j < i; j++) result.Add(clauses[j]);
            }
            result?.Add(rewritten);
        }
        return result ?? clauses;
    }

    // ----- detection -----

    private static Dictionary<(string Name, int Arity), WrapperTemplate> DetectWrappers(
        IReadOnlyList<Clause> clauses)
    {
        // Group Rule/Fact clauses by head (name, arity), preserving order; a
        // predicate qualifies only if its ENTIRE group matches one template.
        var groups = new Dictionary<(string, int), List<(Term Head, Term? Body)>>();
        var order = new List<(string, int)>();
        foreach (var clause in clauses)
        {
            (Term head, Term? body)? hb = clause switch
            {
                { Kind: ClauseKind.Rule, Term: CompoundTerm { Functor: ":-", Args.Length: 2 } r }
                    => (r.Args[0], r.Args[1]),
                { Kind: ClauseKind.Fact } => (clause.Term, null),
                _ => null,
            };
            if (hb is null) continue;
            (string, int)? key = hb.Value.head switch
            {
                AtomTerm a => (a.Name, 0),
                CompoundTerm c => (c.Functor, c.Args.Length),
                _ => null,
            };
            if (key is null) continue;
            if (!groups.TryGetValue(key.Value, out var list))
            {
                groups[key.Value] = list = new List<(Term, Term?)>();
                order.Add(key.Value);
            }
            list.Add(hb.Value);
        }

        var wrappers = new Dictionary<(string, int), WrapperTemplate>();
        foreach (var key in order)
        {
            var group = groups[key];
            WrapperTemplate? t = TryMatchPureControl(group)
                ?? TryMatchIfThen(group)
                ?? TryMatchNegation(group);
            if (t is not null) wrappers[key] = t;
        }
        return wrappers;
    }

    /// <summary>T1: single clause, distinct-var head args, body a pure control
    /// tree over exactly those vars (once each) + true/fail leaves, no cut.</summary>
    private static WrapperTemplate? TryMatchPureControl(List<(Term Head, Term? Body)> group)
    {
        if (group.Count != 1 || group[0].Body is null) return null;
        string[]? argVars = DistinctVarArgs(group[0].Head);
        if (argVars is null || argVars.Length == 0) return null;

        var remaining = new HashSet<string>(argVars);
        if (remaining.Count != argVars.Length) return null;   // duplicate head var

        bool ControlTree(Term t) => t switch
        {
            VarTerm v => remaining.Remove(v.Name),             // each head var once
            AtomTerm { Name: "true" or "fail" or "false" } => true,
            CompoundTerm { Args.Length: 2 } c
                when c.Functor is "," or ";" or "->" or "*->"
                => ControlTree(c.Args[0]) && ControlTree(c.Args[1]),
            CompoundTerm { Args.Length: 1 } c
                when c.Functor is "\\+" or "not"
                => ControlTree(c.Args[0]),
            _ => false,
        };
        if (!ControlTree(group[0].Body!) || remaining.Count != 0) return null;
        return new WrapperTemplate { Body = group[0].Body!, ArgVars = argVars };
    }

    /// <summary>T2: <c>w(C,T) :- C -&gt; !, T.</c> (or <c>C, !, T</c>) + catch-all
    /// <c>w(_,_) [:- !]</c> ⇒ <c>( C -&gt; T ; true )</c>.</summary>
    private static WrapperTemplate? TryMatchIfThen(List<(Term Head, Term? Body)> group)
    {
        if (group.Count != 2) return null;
        string[]? argVars = DistinctVarArgs(group[0].Head);
        if (argVars is null || argVars.Length != 2) return null;
        if (!IsCatchAll(group[1], arity: 2)) return null;

        // Body shapes: '->'(C, ','(!, T))  |  ','(C, ','(!, T)).
        if (group[0].Body is not CompoundTerm
            { Args: [VarTerm c, CompoundTerm { Functor: ",", Args: [AtomTerm { Name: "!" }, VarTerm t] }] } outer
            || (outer.Functor is not ("->" or ","))
            || c.Name != argVars[0] || t.Name != argVars[1])
            return null;

        Term template = new CompoundTerm(";", new Term[]
        {
            new CompoundTerm("->", new Term[] { c, t }),
            new AtomTerm("true"),
        });
        return new WrapperTemplate { Body = template, ArgVars = argVars };
    }

    /// <summary>T3: <c>w(G) :- G, !, fail.</c> + catch-all <c>w(_) [:- !]</c>
    /// ⇒ <c>\+ G</c>.</summary>
    private static WrapperTemplate? TryMatchNegation(List<(Term Head, Term? Body)> group)
    {
        if (group.Count != 2) return null;
        string[]? argVars = DistinctVarArgs(group[0].Head);
        if (argVars is null || argVars.Length != 1) return null;
        if (!IsCatchAll(group[1], arity: 1)) return null;

        if (group[0].Body is not CompoundTerm
            {
                Functor: ",",
                Args: [VarTerm g, CompoundTerm
                {
                    Functor: ",",
                    Args: [AtomTerm { Name: "!" }, AtomTerm { Name: "fail" or "false" }]
                }]
            }
            || g.Name != argVars[0])
            return null;

        Term template = new CompoundTerm("\\+", new Term[] { g });
        return new WrapperTemplate { Body = template, ArgVars = argVars };
    }

    /// <summary>Head args as DISTINCT named variable names; null if any arg is
    /// a non-var, anonymous, or repeated (a repeated head var constrains the
    /// arguments to unify — not a transparent wrapper).</summary>
    private static string[]? DistinctVarArgs(Term head)
    {
        if (head is AtomTerm) return System.Array.Empty<string>();
        if (head is not CompoundTerm c) return null;
        var names = new string[c.Args.Length];
        var seen = new HashSet<string>();
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (c.Args[i] is not VarTerm v || v.Name == "_" || !seen.Add(v.Name))
                return null;
            names[i] = v.Name;
        }
        return names;
    }

    /// <summary>The trailing catch-all clause: head args all anonymous/unused
    /// variables, body absent or exactly <c>!</c>.</summary>
    private static bool IsCatchAll((Term Head, Term? Body) clause, int arity)
    {
        var (head, body) = clause;
        if (body is not null && body is not AtomTerm { Name: "!" }) return false;
        if (head is AtomTerm) return arity == 0;
        if (head is not CompoundTerm c || c.Args.Length != arity) return false;
        // Every arg an INDEPENDENT variable: anonymous, or a named var used
        // only once across the head (a repeated var — w(A,A) — constrains the
        // args to unify and is NOT a catch-all). The body is cut-only or
        // absent, so head vars cannot occur there.
        var seen = new HashSet<string>();
        foreach (var a in c.Args)
        {
            if (a is not VarTerm v) return false;
            if (v.Name != "_" && !seen.Add(v.Name)) return false;
        }
        return true;
    }

    // ----- rewriting -----

    private static Clause RewriteClause(
        Clause clause,
        Dictionary<(string, int), WrapperTemplate> wrappers)
    {
        if (clause.Kind != ClauseKind.Rule
            || clause.Term is not CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
            return clause;
        Term newBody = RewriteGoal(rule.Args[1], wrappers, depth: 0);
        if (ReferenceEquals(newBody, rule.Args[1])) return clause;
        var newRule = new CompoundTerm(":-", new[] { rule.Args[0], newBody })
        {
            Position = rule.Position,
        };
        return new Clause(ClauseKind.Rule, newRule, clause.Position);
    }

    /// <summary>Rewrites goal positions. Descends through the control
    /// constructs only — an argument of <c>call/N</c> / <c>findall</c> / any
    /// other goal is DATA here (a runtime term); rewriting it would change what
    /// the program can observe, so it is left alone (the wrapper's standalone
    /// form serves it at run time).</summary>
    private static Term RewriteGoal(
        Term goal,
        Dictionary<(string, int), WrapperTemplate> wrappers,
        int depth)
    {
        if (depth > MaxUnfoldDepth) return goal;
        switch (goal)
        {
            case CompoundTerm { Args.Length: 2 } c
                when c.Functor is "," or ";" or "->" or "*->":
            {
                Term l = RewriteGoal(c.Args[0], wrappers, depth);
                Term r = RewriteGoal(c.Args[1], wrappers, depth);
                if (ReferenceEquals(l, c.Args[0]) && ReferenceEquals(r, c.Args[1]))
                    return goal;
                return new CompoundTerm(c.Functor, new[] { l, r }) { Position = c.Position };
            }
            case CompoundTerm { Args.Length: 1 } c
                when c.Functor is "\\+" or "not":
            {
                Term inner = RewriteGoal(c.Args[0], wrappers, depth);
                if (ReferenceEquals(inner, c.Args[0])) return goal;
                return new CompoundTerm(c.Functor, new[] { inner }) { Position = c.Position };
            }
            case CompoundTerm c
                when wrappers.TryGetValue((c.Functor, c.Args.Length), out var template):
            {
                // Every argument must be a statically callable goal term.
                foreach (var a in c.Args)
                    if (a is not (AtomTerm or CompoundTerm))
                        return goal;
                Term inst = Instantiate(template, c.Args, c.Position);
                // The instantiated control body may itself contain wrapper
                // calls (nested ifthen) — rewrite it recursively.
                return RewriteGoal(inst, wrappers, depth + 1);
            }
            default:
                return goal;
        }
    }

    /// <summary>Clones the template body substituting each wrapper head var by
    /// the corresponding call-site argument. The template contains only its own
    /// head vars, control functors and true/fail atoms (guaranteed by the
    /// detectors), so no variable capture is possible.</summary>
    private static Term Instantiate(WrapperTemplate template, Term[] args,
        Shumway.Compiler.Lexer.SourcePosition position)
    {
        Term Subst(Term t) => t switch
        {
            VarTerm v => args[System.Array.IndexOf(template.ArgVars, v.Name)],
            CompoundTerm c => new CompoundTerm(
                c.Functor, System.Array.ConvertAll(c.Args, Subst))
            { Position = position },
            _ => t,
        };
        return Subst(template.Body);
    }
}
