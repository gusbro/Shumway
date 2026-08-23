using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    // term_expansion / goal_expansion C# hooks — the "C# shim" path for
    // supporting foreign engines' libraries. A hook inspects a term (a clause or
    // directive, for term_expansion; a body goal, for goal_expansion) and returns
    // its replacement, or null to decline. First hook that returns non-null wins;
    // a term_expansion may return several terms (one clause → many) or an empty
    // list (drop the term). These run BEFORE the Prolog-level term_expansion/2 //
    // goal_expansion/2 predicates.
    private List<System.Func<Term, IReadOnlyList<Term>?>>? _termExpansions;
    private List<System.Func<Term, Term?>>? _goalExpansions;

    /// <summary>Registers a C# <c>term_expansion</c> hook (ADR-038 shim path): it
    /// receives each term read during a consult (a clause or a <c>:- Directive</c>)
    /// and returns the term(s) that replace it, or <c>null</c> to decline. An empty
    /// list drops the term. Hooks run in registration order; the first non-null
    /// result wins. Runs before any Prolog <c>term_expansion/2</c>.</summary>
    public void RegisterTermExpansion(System.Func<Term, IReadOnlyList<Term>?> hook)
    {
        System.ArgumentNullException.ThrowIfNull(hook);
        (_termExpansions ??= new()).Add(hook);
    }

    /// <summary>Registers a C# <c>goal_expansion</c> hook (ADR-038 shim path): it
    /// receives each body goal and returns its replacement goal, or <c>null</c> to
    /// decline. Hooks run in registration order; the first non-null result wins.
    /// Runs before any Prolog <c>goal_expansion/2</c>.</summary>
    public void RegisterGoalExpansion(System.Func<Term, Term?> hook)
    {
        System.ArgumentNullException.ThrowIfNull(hook);
        (_goalExpansions ??= new()).Add(hook);
    }

    internal bool HasTermExpansions => _termExpansions is { Count: > 0 };
    internal bool HasGoalExpansions => _goalExpansions is { Count: > 0 };

    // Applies the C# term_expansion hooks to one raw term. Returns null when no
    // hook fired (the caller keeps the original term unchanged); otherwise the
    // replacement list (possibly empty to drop the term).
    internal IReadOnlyList<Term>? ApplyCsTermExpansion(Term term)
    {
        if (_termExpansions is null) return null;
        foreach (var hook in _termExpansions)
        {
            var r = hook(term);
            if (r is not null) return r;
        }
        return null;
    }

    // Applies the C# goal_expansion hooks to one goal. Returns null when no hook
    // fired (keep the goal unchanged).
    internal Term? ApplyCsGoalExpansion(Term goal)
    {
        if (_goalExpansions is null) return null;
        foreach (var hook in _goalExpansions)
        {
            var r = hook(goal);
            if (r is not null) return r;
        }
        return null;
    }

    private static readonly int TermExpansionFid =
        FunctorTable.Intern(AtomTable.Intern("term_expansion", permanent: true).Id, 2);

    // Scryer's extended term_expansion/6:
    // term_expansion(Term0, Layout0, Ids0, Term, Layout, Ids). Layout is source
    // positions (we don't track them, so pass fresh vars); Ids is an already-applied
    // expansion-id set threaded through the fixpoint to stop re-expansion. clpz's
    // dual-accumulator `++>` grammar is expanded this way.
    private static readonly int TermExpansion6Fid =
        FunctorTable.Intern(AtomTable.Intern("term_expansion", permanent: true).Id, 6);

    internal bool HasPrologTermExpansion => HasPredicate(TermExpansionFid);

    internal bool HasPrologTermExpansion6 => HasPredicate(TermExpansion6Fid);

    // ---- hook discriminator index ----
    //
    // Every consulted term (and, for goal_expansion, every body GOAL of every
    // clause) pays one QueryAll per live hook family — the dominant cost of
    // loading a large library under active hooks (~2 ms per no-match QueryAll).
    // Most hook clauses discriminate their input by a principal functor, either
    // in the head (`term_expansion(marker(E), _)`) or via the Scryer idiom of a
    // var head narrowed by a body unification
    // (`goal_expansion(T, ...) :- nonvar(T), T = put_atts(V, M, A)`). This
    // index extracts those discriminators once per program change; a QueryAll
    // is skipped entirely when no hook clause could match the input's shape.
    // A clause the analysis can't see through (dcgs's DCG translator calls
    // `dcg_rule` inside catch/3) makes its family AnyMatch — always queried,
    // never wrong.
    private sealed class HookDiscriminators
    {
        public bool AnyMatch;
        public HashSet<(string Name, int Arity)>? Keys;

        public bool CouldMatch(Term input)
        {
            if (AnyMatch) return true;
            if (Keys is null) return false;          // hook has no clauses
            return input switch
            {
                CompoundTerm c => Keys.Contains((c.Functor, c.Args.Length)),
                AtomTerm a => Keys.Contains((a.Name, 0)),
                VarTerm => true,                     // unbound input: anything could match
                _ => false,                          // number/string: no functor to match
            };
        }
    }

    private HookDiscriminators? _teIdx, _te6Idx, _geIdx;
    internal bool _hookIndexValid;

    private HookDiscriminators HookIndex(int hookFid, ref HookDiscriminators? slot)
    {
        if (!_hookIndexValid) { _teIdx = _te6Idx = _geIdx = null; _hookIndexValid = true; }
        if (slot is not null) return slot;
        var idx = new HookDiscriminators();
        // A hook living in the DYNAMIC store (asserted, no static clauses) can
        // change without a consult — don't reason about its clauses.
        if (_dynStore.IsDynamic(hookFid)) { idx.AnyMatch = true; return slot = idx; }
        var (atomId, arity) = FunctorTable.Lookup(hookFid);
        string hookName = AtomTable.GetById(atomId)?.Name ?? "";
        foreach (var manifest in _modules.Values)
        foreach (var c in manifest.Clauses)
        {
            (Term head, Term? body) = c.Term is CompoundTerm { Functor: ":-", Args.Length: 2 } r
                ? (r.Args[0], r.Args[1]) : (c.Term, null);
            // An M:head that escaped the consult-time strip still contributes.
            if (head is CompoundTerm { Functor: ":", Args.Length: 2 } q) head = q.Args[1];
            if (head is not CompoundTerm h || h.Functor != hookName || h.Args.Length != arity)
                continue;
            AddClauseKey(idx, h.Args[0], body);
            if (idx.AnyMatch) return slot = idx;
        }
        return slot = idx;
    }

    private static void AddClauseKey(HookDiscriminators idx, Term firstArg, Term? body)
    {
        switch (firstArg)
        {
            case CompoundTerm p:
                (idx.Keys ??= new()).Add((p.Functor, p.Args.Length));
                return;
            case AtomTerm a:
                (idx.Keys ??= new()).Add((a.Name, 0));
                return;
            case VarTerm v:
                // Walk the body's top-level conjunction: skip guards that cannot
                // bind the head var, take the discriminator from the first
                // `V = Pattern` / `Pattern = V`. Anything else → AnyMatch.
                Term? cur = body;
                while (cur is CompoundTerm { Functor: ",", Args.Length: 2 } conj)
                {
                    if (!Step(conj.Args[0])) return;
                    cur = conj.Args[1];
                }
                if (cur is not null && !Step(cur)) return;
                idx.AnyMatch = true;   // ran out of body without a discriminator
                return;

                bool Step(Term g)
                {
                    switch (g)
                    {
                        case CompoundTerm { Functor: "$te_after" or "nonvar" or "callable", Args.Length: 1 }:
                            return true;   // transparent guard — keep walking
                        case CompoundTerm { Functor: "=", Args.Length: 2 } eq:
                            Term pat = eq.Args[0] is VarTerm pv && pv.Name == v.Name ? eq.Args[1]
                                : eq.Args[1] is VarTerm pv2 && pv2.Name == v.Name ? eq.Args[0]
                                : null!;
                            if (pat is CompoundTerm pc)
                                (idx.Keys ??= new()).Add((pc.Functor, pc.Args.Length));
                            else if (pat is AtomTerm pa)
                                (idx.Keys ??= new()).Add((pa.Name, 0));
                            else idx.AnyMatch = true;   // `V = OtherVar` or not our var
                            return false;               // decided (key or AnyMatch)
                        default:
                            idx.AnyMatch = true;        // opaque goal before the pattern
                            return false;
                    }
                }
            default:
                idx.AnyMatch = true;   // exotic first arg (number literal, ...)
                return;
        }
    }

    // The Prolog term_expansion/2 hook: call the user predicate
    // `term_expansion(Input, Expanded)` in the live engine (works mid-consult) and
    // return its expansion. A term_expansion result that is a PROPER LIST is a list
    // of clauses (SWI/Scryer); anything else is a single clause; `[]` drops the
    // term. Returns false when term_expansion/2 is undefined or fails (no
    // expansion). Only consulted when HasPrologTermExpansion is true, so a program
    // that defines no hook pays nothing.
    internal bool TryPrologTermExpansion(Term input, out List<Term> output)
    {
        output = new List<Term>();
        return ExpandTermFixpoint(input, output, 64, new AtomTerm("[]")) && output.Count switch
        {
            // The fixpoint always adds the input itself when nothing expanded;
            // report "no expansion" in that case so the caller keeps the original.
            1 when ReferenceEquals(output[0], input) => Reset(output),
            _ => true,
        };

        static bool Reset(List<Term> o) { o.Clear(); return false; }
    }

    // Applies term_expansion (C# then Prolog) repeatedly, to a fixpoint, matching
    // SWI/Scryer: an expansion's output is itself re-expanded until it stops
    // changing. Real libraries depend on this — Scryer's DCG expander emits a
    // `throw_dcg_expansion_error(E)` marker for a deferred error and relies on a
    // second expansion pass turning it back into `throw(E)`. `depth` bounds the
    // recursion so a pathological hook cannot loop forever.
    private bool ExpandTermFixpoint(Term term, List<Term> output, int depth, Term ids)
    {
        // Scryer's term_expansion/6 first — it threads `ids` (the already-applied
        // expansion set) so its own guard stops re-expansion. clpz's `++>` grammar.
        if (depth > 0 && HasPrologTermExpansion6
            && TryPrologTermExpansion6(term, ids, out Term t6, out Term newIds)
            && !t6.Equals(term))
        {
            ExpandTermFixpoint(t6, output, depth - 1, newIds);
            return true;
        }
        IReadOnlyList<Term>? repl = HasTermExpansions ? ApplyCsTermExpansion(term) : null;
        if (repl is null && HasPrologTermExpansion)
        {
            var one = new List<Term>();
            if (TryPrologTermExpansionOnce(term, one)) repl = one;
        }
        // No hook fired, or the sole result is the term unchanged, or we've hit the
        // depth bound → this term is final.
        if (repl is null || depth <= 0
            || (repl.Count == 1 && repl[0].Equals(term)))
        {
            output.Add(term);
            return true;
        }
        foreach (var t in repl)
        {
            // A DIRECTIVE in a hook's output is an instruction to the compiler,
            // final as emitted — never re-expanded (SWI single-pass semantics).
            // record.pl's expansion opens with an xref marker directive
            // `(:- record('<compiled>'))`; re-running the hook on it generated
            // spurious `default_<compiled>` clauses. The fixpoint stays for
            // non-directive outputs (Scryer's dcgs error-marker second pass).
            if (t is CompoundTerm { Functor: ":-", Args.Length: 1 })
                output.Add(t);
            else
                ExpandTermFixpoint(t, output, depth - 1, ids);
        }
        return true;
    }

    // One term_expansion/6 pass: term_expansion(Term0, Layout0, Ids0, Term, Layout,
    // Ids). Returns the expanded Term and the new Ids set; false (leaving both at the
    // inputs) when the hook is undefined or fails for this term.
    private bool TryPrologTermExpansion6(Term input, Term ids, out Term output, out Term newIds)
    {
        if (!HookIndex(TermExpansion6Fid, ref _te6Idx).CouldMatch(input))
        {
            output = input;
            newIds = ids;
            return false;
        }
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var termVar = new VarTerm("$TE6_Term");
        var idsVar = new VarTerm("$TE6_Ids");
        var goal = new CompoundTerm("term_expansion", new Term[]
        {
            input, new VarTerm("$TE6_L0"), ids, termVar, new VarTerm("$TE6_L1"), idsVar,
        });
        long t0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            foreach (var sol in QueryAll(goal))
            {
                Term? t = sol["$TE6_Term"];
                if (t is null) break;
                output = RelinkInputVars(t, inputVars, sol);
                newIds = sol["$TE6_Ids"] ?? ids;
                return true;
            }
            output = input;
            newIds = ids;
            return false;
        }
        finally
        {
            if (LoadProfEnabled)
            {
                ProfTeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                ProfTeCalls++;
            }
        }
    }

    // One term_expansion/2 pass over `input`; appends its flattened result to
    // `output`. Returns false (leaving output untouched) when the hook is
    // undefined or fails for this term.
    private bool TryPrologTermExpansionOnce(Term input, List<Term> output)
    {
        if (!HookIndex(TermExpansionFid, ref _teIdx).CouldMatch(input)) return false;
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var expandedVar = new VarTerm("$TE_Expanded");
        var goal = new CompoundTerm("term_expansion", new Term[] { input, expandedVar });
        bool diag = TeDiagEnabled;
        if (diag) System.Console.Error.WriteLine($"[TE] try (expandPos={_consultExpandPos}, loadmod={_currentLoadModule}): {input}");
        long t0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            foreach (var sol in QueryAll(goal))
            {
                Term? expanded = sol["$TE_Expanded"];
                if (expanded is null) return false;
                if (diag) System.Console.Error.WriteLine($"[TE] fired -> {expanded}");
                FlattenExpansion(RelinkInputVars(expanded, inputVars, sol), output);
                return true;
            }
            if (diag) System.Console.Error.WriteLine($"[TE] no solution (hook failed) for: {input}");
            return false;
        }
        finally
        {
            if (LoadProfEnabled)
            {
                ProfTeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                ProfTeCalls++;
            }
        }
    }

    /// <summary>Runs the EARLY-ACTIVATED in-file term_expansion hooks (renamed
    /// to <paramref name="predName"/> in the consult's hidden module) against
    /// one directive term. Same solution/flatten handling as
    /// <see cref="TryPrologTermExpansionOnce"/>, but scoped to the dedicated
    /// predicate so a partial hook never affects ordinary clause
    /// expansion.</summary>
    internal bool TryEarlyTermExpansion(string predName, Term input, out List<Term> output)
    {
        output = new List<Term>();
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var expandedVar = new VarTerm("$TE_Expanded");
        var goal = new CompoundTerm(predName, new Term[] { input, expandedVar });
        foreach (var sol in QueryAll(goal))
        {
            Term? expanded = sol["$TE_Expanded"];
            if (expanded is null) return false;
            FlattenExpansion(RelinkInputVars(expanded, inputVars, sol), output);
            return true;
        }
        return false;
    }

    private static readonly bool TeDiagEnabled =
        System.Environment.GetEnvironmentVariable("SHUMWAY_TE_DIAG") == "1";

    // ---- load-profiling counters (SHUMWAY_LOAD_PROF=1) ----
    // Wall time + call counts of the consult-time hot paths, printed by the
    // REPL at exit. Static (per-process) — the load profile is per-run anyway.
    internal static readonly bool LoadProfEnabled =
        System.Environment.GetEnvironmentVariable("SHUMWAY_LOAD_PROF") == "1";
    static PrologEngine()
    {
        if (LoadProfEnabled)
            Shumway.Compiler.Wam.ModuleCompiler.ProfCompiledByFid = new();
    }
    internal static long ProfTeTicks, ProfTeCalls;
    internal static long ProfGeTicks, ProfGeCalls;
    internal static long ProfSetupTicks, ProfSetupCalls, ProfProductBuilds;
    internal static long ProfReExpandTicks, ProfReExpandCalls;
    // Warm-setup sub-phases.
    internal static long ProfMergeTicks, ProfQueryCompileTicks, ProfUniqTicks, ProfActivationTicks;
    internal static long ProfActCtorTicks, ProfInstallTicks, ProfHotnessTicks;
    internal static long ProfPrologTicks, ProfProductCheckTicks;
    internal static long ProfPbRewriteTicks, ProfPbCompileTicks, ProfPbPartitionTicks;
    internal static long ProfPbElideTicks, ProfPbModCompileTicks, ProfPbLateHelpersTicks;
    internal static long ProfPbCompiledPreds;

    internal static void PrintLoadProfile()
    {
        if (!LoadProfEnabled) return;
        static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        System.Console.Error.WriteLine(
            $"[PROF] te-QueryAll: {ProfTeCalls} calls, {Ms(ProfTeTicks):F0} ms\n" +
            $"[PROF] ge-QueryAll: {ProfGeCalls} calls, {Ms(ProfGeTicks):F0} ms\n" +
            $"[PROF] query-setup: {ProfSetupCalls} setups ({ProfProductBuilds} product builds), {Ms(ProfSetupTicks):F0} ms\n" +
            $"[PROF]   merge-maps: {Ms(ProfMergeTicks):F0} ms | query-compile+link: {Ms(ProfQueryCompileTicks):F0} ms | uniqueness: {Ms(ProfUniqTicks):F0} ms | activation: {Ms(ProfActivationTicks):F0} ms\n" +
            $"[PROF]   act-ctor: {Ms(ProfActCtorTicks):F0} ms | install-callil: {Ms(ProfInstallTicks):F0} ms | hotness: {Ms(ProfHotnessTicks):F0} ms\n" +
            $"[PROF]   prolog: {Ms(ProfPrologTicks):F0} ms | product-check: {Ms(ProfProductCheckTicks):F0} ms\n" +
            $"[PROF]   pb-rewrite: {Ms(ProfPbRewriteTicks):F0} ms | pb-compile: {Ms(ProfPbCompileTicks):F0} ms | pb-partition: {Ms(ProfPbPartitionTicks):F0} ms\n" +
            $"[PROF]   elide-cuts (all Compile calls): {Ms(Shumway.Compiler.Wam.ModuleCompiler.ProfElideTicks):F0} ms | pred-compile: {Shumway.Compiler.Wam.ModuleCompiler.ProfCompiledPreds} compiled / {Shumway.Compiler.Wam.ModuleCompiler.ProfSkippedPreds} skipped, {Ms(Shumway.Compiler.Wam.ModuleCompiler.ProfPredTicks):F0} ms\n" +
            $"[PROF]   pb-elide: {Ms(ProfPbElideTicks):F0} ms | pb-modcompile: {Ms(ProfPbModCompileTicks):F0} ms | pb-latehelpers: {Ms(ProfPbLateHelpersTicks):F0} ms\n" +
            $"[PROF]   product-compiled: {ProfPbCompiledPreds} | compile-calls: {Shumway.Compiler.Wam.ModuleCompiler.ProfCompileCalls} | grouping: {Ms(Shumway.Compiler.Wam.ModuleCompiler.ProfGroupTicks):F0} ms | pool-snapshots: {Ms(Shumway.Compiler.Wam.ModuleCompiler.ProfSnapshotTicks):F0} ms\n" +
            $"[PROF] re-expand passes: {ProfReExpandCalls}, {Ms(ProfReExpandTicks):F0} ms");
        if (Shumway.Compiler.Wam.ModuleCompiler.ProfCompiledByFid is { } pcf)
        {
            var top = new List<KeyValuePair<int, int>>(pcf);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder("[PROF] top recompiled fids (non-query): ");
            int shown = 0;
            for (int i = 0; i < top.Count && shown < 15; i++)
            {
                var (atomId, arity) = FunctorTable.Lookup(top[i].Key);
                string nm = AtomTable.GetById(atomId)?.Name ?? "?";
                if (nm.StartsWith("__query__") || nm.StartsWith("$q")) continue;
                sb.Append($"{nm}/{arity}×{top[i].Value} ");
                shown++;
            }
            System.Console.Error.WriteLine(sb.ToString());
        }
    }

    // Running an expansion through QueryAll materialises the input's variables and
    // reads the output back with fresh heap-address names (_G<addr>) — losing the
    // sharing between the input's vars and the clause around it. This restores it:
    // read each input var back too (same heap address → same _G<addr> name) and
    // rename that name in the output to the input var's ORIGINAL name, so the
    // expansion shares variables with the rest of the clause again.
    //
    // Any variable the HOOK introduced (not an input var) then gets a globally
    // UNIQUE name: heap-address names repeat across materialisations (a later
    // expansion's _G<addr> can equal a _G<addr> already in the surrounding
    // clause from an earlier expansion — clpz's ++>-expanded clauses meet their
    // brace-goal expansions), and a colliding name silently ALIASES two
    // unrelated variables in the rebuilt clause.
    private static int _freshExpansionVar;

    private static Term RelinkInputVars(Term output, HashSet<string> inputVars, Solution sol)
    {
        Dictionary<string, Term>? rename = null;
        foreach (string name in inputVars)
        {
            if (sol[name] is VarTerm rb && rb.Name != name)
                (rename ??= new())[rb.Name] = new VarTerm(name);
        }
        // Uniquify hook-introduced variables. Collect the output's var names;
        // anything that is neither an input var nor an input's read-back alias
        // is hook-fresh.
        var outVars = new HashSet<string>();
        CollectVarNames(output, outVars);
        foreach (string name in outVars)
        {
            if (name == "_" || inputVars.Contains(name)) continue;
            if (rename is not null && rename.ContainsKey(name)) continue;
            int n = System.Threading.Interlocked.Increment(ref _freshExpansionVar);
            (rename ??= new())[name] = new VarTerm("_Uexp" + n);
        }
        return rename is null ? output : SubstituteVars(output, rename);
    }

    private static void CollectVarNames(Term t, HashSet<string> into)
    {
        switch (t)
        {
            case VarTerm v: into.Add(v.Name); break;
            case CompoundTerm c:
                foreach (var a in c.Args) CollectVarNames(a, into);
                break;
        }
    }

    private static Term SubstituteVars(Term t, Dictionary<string, Term> map)
    {
        switch (t)
        {
            case VarTerm v: return map.TryGetValue(v.Name, out var r) ? r : t;
            case CompoundTerm c:
            {
                Term[]? args = null;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Term s = SubstituteVars(c.Args[i], map);
                    if (!ReferenceEquals(s, c.Args[i])) (args ??= (Term[])c.Args.Clone())[i] = s;
                }
                return args is null ? t : new CompoundTerm(c.Functor, args) { Position = c.Position };
            }
            default: return t;
        }
    }

    private static readonly int GoalExpansionFid =
        FunctorTable.Intern(AtomTable.Intern("goal_expansion", permanent: true).Id, 2);

    internal bool HasPrologGoalExpansion => HasPredicate(GoalExpansionFid);

    // term_expansion/2 and goal_expansion/2 are GLOBAL hooks: any module that
    // defines one (real libraries do it with a `user:` clause head — atts.pl,
    // dcgs.pl) contributes to the single global hook, so the functor is never
    // mangled into a module-local name, even inside an export-qualified module.
    // The clause itself stays in its file's module, so its BODY still resolves
    // against that module's own predicates.
    internal static bool IsGlobalHookFunctor(int fid) =>
        fid == TermExpansionFid || fid == GoalExpansionFid || fid == TermExpansion6Fid;

    // Apply goal_expansion to every body goal of a clause (or a directive's goal).
    // A fact has no body and is returned unchanged.
    internal Clause ExpandClauseGoals(Clause clause)
    {
        switch (clause.Term)
        {
            case CompoundTerm { Functor: ":-", Args: [var head, var body] } rule:
            {
                Term nb = ExpandGoalTree(body);
                return ReferenceEquals(nb, body) ? clause
                    : new Clause(clause.Kind,
                        new CompoundTerm(":-", new[] { head, nb }) { Position = rule.Position },
                        clause.Position);
            }
            case CompoundTerm { Functor: ":-", Args: [var dbody] } dir:
            {
                Term nb = ExpandGoalTree(dbody);
                return ReferenceEquals(nb, dbody) ? clause
                    : new Clause(clause.Kind,
                        new CompoundTerm(":-", new[] { nb }) { Position = dir.Position },
                        clause.Position);
            }
            default:
                return clause;   // fact
        }
    }

    // Recurse through the cut-transparent control constructs and apply
    // goal_expansion (C# then Prolog) to each plain body goal.
    /// <summary>Applies goal_expansion to one goal tree (control constructs
    /// descended, plain goals expanded to a bounded fixpoint). The entry the
    /// consult re-expansion pass uses for goals inside DCG <c>{ }</c> braces —
    /// a DCG rule's body is grammar-speak the clause-level expansion must not
    /// touch, but its brace goals are plain goals (clpz's <c>{ A cis_leq B }</c>
    /// must rewrite like any body goal).</summary>
    internal Term ExpandGoalTreeIn(Term goal) => ExpandGoalTree(goal);

    private Term ExpandGoalTree(Term goal)
    {
        // A VARIABLE goal is a runtime meta-call — goal_expansion must not
        // touch it: a hook's head pattern would UNIFY into the variable
        // (dcgs's `goal_expansion(phrase(B,S), phrase(B,S,[]))` turned clpz's
        // `( Repeat -> ... )` condition into an orphaned `phrase(_,_,[])`,
        // destroying the goal). Scryer's expand_goal skips vars the same way.
        if (goal is VarTerm) return goal;
        if (goal is CompoundTerm c && IsGoalControl(c.Functor, c.Args.Length))
        {
            Term[]? args = null;
            for (int i = 0; i < c.Args.Length; i++)
            {
                Term ex = ExpandGoalTree(c.Args[i]);
                if (!ReferenceEquals(ex, c.Args[i]))
                    (args ??= (Term[])c.Args.Clone())[i] = ex;
            }
            return args is null ? goal
                : new CompoundTerm(c.Functor, args) { Position = c.Position };
        }
        // Plain goal — expand (bounded fixpoint so g→g' →g'' converges).
        Term g = goal;
        for (int i = 0; i < 8; i++)
        {
            Term? next = ApplyCsGoalExpansion(g)
                ?? (HasPrologGoalExpansion && TryPrologGoalExpansion(g, out var pe) ? pe : null);
            if (next is null || next.Equals(g)) break;
            g = next;
        }
        if (ReferenceEquals(g, goal)) return goal;
        // The replacement may itself be a CONTROL construct whose subgoals
        // still need expanding — clpz's own goal_expansion rewrites
        // get_attr/3 into (var(V), get_atts(V, Access)), and that get_atts
        // needs the atts hook that bakes the calling module in. Without the
        // re-walk it resolved to the user-module fallback and read nothing.
        if (g is CompoundTerm gc && IsGoalControl(gc.Functor, gc.Args.Length))
            return ExpandGoalTree(g);
        return g;
    }

    // The control constructs whose arguments are themselves goals (walked, not
    // expanded as a unit). call/1 recurses into its goal too.
    private static bool IsGoalControl(string functor, int arity) => (functor, arity) switch
    {
        (",", 2) or (";", 2) or ("->", 2) or ("*->", 2)
            or ("\\+", 1) or ("not", 1) or ("call", 1) => true,
        _ => false,
    };

    // The Prolog goal_expansion/2 hook, mirroring TryPrologTermExpansion but for a
    // single replacement goal.
    private bool TryPrologGoalExpansion(Term input, out Term expanded)
    {
        if (!HookIndex(GoalExpansionFid, ref _geIdx).CouldMatch(input))
        {
            if (TeDiagEnabled)
                System.Console.Error.WriteLine($"[GE] index-skip: {input}");
            expanded = input;
            return false;
        }
        if (TeDiagEnabled) System.Console.Error.WriteLine($"[GE] try: {input}");
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var v = new VarTerm("$GE_Expanded");
        var goal = new CompoundTerm("goal_expansion", new Term[] { input, v });
        long t0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            foreach (var sol in QueryAll(goal))
            {
                Term? e = sol["$GE_Expanded"];
                if (e is null) break;
                expanded = RelinkInputVars(e, inputVars, sol);
                return true;
            }
            expanded = input;
            return false;
        }
        finally
        {
            if (LoadProfEnabled)
            {
                ProfGeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                ProfGeCalls++;
            }
        }
    }

    // A proper list [a,b,…] → its elements (each a clause); [] → nothing (drop);
    // anything else → itself (one clause).
    private static void FlattenExpansion(Term t, List<Term> output)
    {
        if (t is AtomTerm { Name: "[]" }) return;
        if (t is CompoundTerm { Functor: ".", Args.Length: 2 })
        {
            Term cursor = t;
            var items = new List<Term>();
            while (cursor is CompoundTerm { Functor: ".", Args: [var head, var tail] })
            {
                items.Add(head);
                cursor = tail;
            }
            if (cursor is AtomTerm { Name: "[]" }) { output.AddRange(items); return; }
            // Improper list — treat the whole term as one clause.
        }
        output.Add(t);
    }
}
