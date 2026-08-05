using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    // Re-entrant host→Prolog solve. A foreign predicate ([PrologPredicate]) runs
    // mid-execution with the live Activation in hand; SolveOnce lets it call a
    // Prolog goal back on THAT activation — reusing the already-linked program,
    // heap and trail — instead of building a fresh top-level query. This is the
    // cheap path for the C#→main→C#→predX embedding pattern: the outer query's
    // one-time setup is paid once, and each re-entrant crossing is a nested
    // semidet solve (like call/1), not another SetupQueryFromTerm.
    //
    // Bindings the goal makes persist on the shared heap/trail, so the output
    // variables in `goal` are bound in the returned Solution AND visible to the
    // outer computation after the foreign method returns (and correctly undone if
    // the outer computation later backtracks past the foreign call). Once-
    // semantics: any choice points the goal leaves are discarded, so it yields a
    // single solution.

    /// <summary>Solves <paramref name="goal"/> once on the currently-executing
    /// <paramref name="engine"/> (a foreign predicate's live activation), returning
    /// its first solution's bindings. Reuses the running machine — no new top-level
    /// query. Throws if <paramref name="engine"/> is not mid-query (use
    /// <see cref="QueryAll(Term)"/> for a top-level goal).</summary>
    public bool SolveOnce(Activation engine, Term goal, out Solution solution)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(goal);
        var solve = engine.ReentrantSolve
            ?? throw new InvalidOperationException(
                "SolveOnce requires an activation currently executing a query — call it "
                + "from within a foreign predicate. For a top-level goal use QueryAll.");

        var varMap = engine.RentReentrantVarMap();
        try
        {
            Cell goalCell = Materializer.MaterializeAsCellSharing(engine, goal, varMap);
            if (!solve(goalCell))
            {
                solution = new Solution(success: false,
                    bindings: ImmutableDictionary<string, Term>.Empty, engine: this);
                return false;
            }

            var names = new List<string>(varMap.Count);
            var idx = new int[varMap.Count];
            int i = 0;
            foreach (var kv in varMap) { names.Add(kv.Key); idx[i++] = kv.Value; }
            solution = BuildSolution(names, idx, engine, isLast: true, host: this);
            return true;
        }
        finally { engine.ReturnReentrantVarMap(varMap); }
    }

    /// <summary>Lean single-output re-entrant solve: solves <paramref name="goal"/> once
    /// and, on success, reads the variable named <paramref name="outVar"/> converted to
    /// <typeparamref name="T"/> — with NO <see cref="Solution"/> and no bindings
    /// dictionaries (the ~800-byte-per-call cost of the <c>out Solution</c> form). The
    /// common foreign-predicate shape: compute one value from a Prolog goal. Put a
    /// <see cref="VarTerm"/> named <paramref name="outVar"/> in <paramref name="goal"/>
    /// for the result.</summary>
    public bool SolveOnce<T>(Activation engine, Term goal, string outVar, out T value)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(outVar);
        var solve = engine.ReentrantSolve
            ?? throw new InvalidOperationException(
                "SolveOnce requires an activation currently executing a query — call it "
                + "from within a foreign predicate. For a top-level goal use QueryAll.");

        var varMap = engine.RentReentrantVarMap();
        try
        {
            Cell goalCell = Materializer.MaterializeAsCellSharing(engine, goal, varMap);
            if (!solve(goalCell)) { value = default!; return false; }
            if (!varMap.TryGetValue(outVar, out int heapIdx))
                throw new ArgumentException(
                    $"goal has no variable named '{outVar}'", nameof(outVar));
            value = FromTerm<T>(TermReader.Materialize(engine, heapIdx));
            return true;
        }
        finally { engine.ReturnReentrantVarMap(varMap); }
    }

    /// <summary>Solves <paramref name="goal"/> once on the live
    /// <paramref name="engine"/> as a semidet check — no bindings read back. Cheaper
    /// than the <c>out Solution</c> form: materializes with the pooled scratch map (no
    /// per-call variable map) and skips <see cref="BuildSolution"/> entirely. Any
    /// bindings the goal makes still persist on the shared heap (visible to the outer
    /// computation), they are just not surfaced as a Solution.</summary>
    public bool SolveOnce(Activation engine, Term goal)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(goal);
        var solve = engine.ReentrantSolve
            ?? throw new InvalidOperationException(
                "SolveOnce requires an activation currently executing a query — call it "
                + "from within a foreign predicate. For a top-level goal use QueryAll.");
        return solve(Materializer.MaterializeAsCell(engine, goal));
    }

    /// <summary>Convenience: build <c>functor(args…)</c> and
    /// <see cref="SolveOnce(Activation, Term, out Solution)"/> it. Put a
    /// <see cref="VarTerm"/> in <paramref name="args"/> for each output and read it
    /// off the returned <see cref="Solution"/>.</summary>
    public bool SolveOnce(Activation engine, string functor, Term[] args, out Solution solution)
    {
        ArgumentNullException.ThrowIfNull(functor);
        ArgumentNullException.ThrowIfNull(args);
        Term goal = args.Length == 0 ? new AtomTerm(functor) : new CompoundTerm(functor, args);
        return SolveOnce(engine, goal, out solution);
    }
}
