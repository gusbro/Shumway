using System;
using System.Collections.Generic;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding.Debugging;

public sealed partial class DebugService
{
    private static void ResidDiag(string message)
    {
        if (ShumwayDebugHelper.DiagEnabled)
            ShumwayDebugHelper.DiagLine("residuals: " + message);
    }

    /// <summary>Decorates a stop's frames with the residual constraints of their
    /// attributed variables — the debugger's counterpart of the REPL's
    /// <c>A in 6..9</c> answer display.
    ///
    /// <para>The projection has to RUN the per-module attribute hooks
    /// (<c>attribute_goals/4</c> / <c>attribute_goals//1</c> — Prolog), and it must not
    /// touch the suspended activation, whose attribute table an evaluation activation
    /// cannot see either. So the suspended variables are TRANSPLANTED: their attribute
    /// graph is read (pure) off the suspended activation and rebuilt as
    /// <c>ag(M, A, V)</c> triples over fresh variables inside a nested evaluation, where
    /// the prelude's <c>'$dbg_residuals'/2</c> reattaches and projects — the exact
    /// bracket discipline of a breakpoint condition: no stop the projection reaches is
    /// ever shown, and a hook that fails or hangs degrades to "no constraints", never to
    /// a broken stop.</para></summary>
    internal IReadOnlyList<PrologEngine.DebugFrame> AttachResiduals(
        Activation engine, IReadOnlyList<PrologEngine.DebugFrame> frames)
    {
        // The cheap guard: nothing attributed in any shown frame → zero cost.
        List<int>? roots = null;
        foreach (var f in frames)
            foreach (var (_, addr) in f.AttVarSlots)
                (roots ??= new List<int>()).Add(addr);
        ResidDiag("roots=" + (roots is null ? "none" : string.Join(",", roots)));
        if (roots is null) return frames;

        var projected = ProjectResiduals(engine, roots);
        ResidDiag("projected="
            + (projected is null ? "null" : projected.Value.ByOwner.Count.ToString()));
        if (projected is null) return frames;
        var (addrToCopyName, byOwner) = projected.Value;

        var result = new List<PrologEngine.DebugFrame>(frames.Count);
        foreach (var f in frames)
        {
            if (f.AttVarSlots.Count == 0) { result.Add(f); continue; }

            // Rename the projection's copy variables to THIS frame's names where the
            // frame sees the cell, and to the stable _G<addr> of the suspended cell
            // otherwise — so `X in 6..9, X #< Y` reads in the user's own vocabulary.
            var renames = new Dictionary<string, string>();
            foreach (var (addr, copyName) in addrToCopyName)
                renames[copyName] = "_G" + addr;
            foreach (var (name, addr) in f.AttVarSlots)
                if (addrToCopyName.TryGetValue(addr, out string? copyName)
                    && renames.TryGetValue(copyName, out string? current)
                    && current.StartsWith("_G", StringComparison.Ordinal))
                    renames[copyName] = name;

            List<(string, string)>? rows = null;
            var rowAddrs = new HashSet<int>();
            foreach (var (name, addr) in f.AttVarSlots)
            {
                if (!rowAddrs.Add(addr)) continue;   // aliased frame vars: one row
                if (!addrToCopyName.TryGetValue(addr, out string? copyName)
                    || !byOwner.TryGetValue(copyName, out var goals)
                    || goals.Count == 0)
                    continue;
                var parts = new List<string>(goals.Count);
                foreach (Term g in goals)
                    parts.Add(AstTermRenderer.Render(
                        ResidualProjection.SubstituteVarNames(g, renames),
                        999, _engine.Operators, quoted: true));
                (rows ??= new List<(string, string)>()).Add(
                    (name, Ellipsize(string.Join(", ", parts), 512)));
            }
            result.Add(rows is null ? f : f with { Residuals = rows });
        }
        return result;
    }

    /// <summary>Runs the transplant + projection as a nested evaluation and buckets the
    /// residual goals per owner variable. Returns null when there is nothing to show —
    /// no attributes worth projecting, or the projection failed (a hook error is the
    /// PROGRAM's business, not the stop's).</summary>
    private (Dictionary<int, string> AddrToCopyName,
             Dictionary<string, List<Term>> ByOwner)?
        ProjectResiduals(Activation engine, IReadOnlyList<int> rootAddrs)
    {
        var built = _engine.BuildResidualAttrInfo(engine, rootAddrs);
        if (built is null) return null;
        var (attrInfo, rootsList, orderedAddrs) = built.Value;

        const string GoalsVar = "_DbgResiduals";
        const string RootsVar = "_DbgRoots";
        Term goal = new CompoundTerm(",", new Term[]
        {
            new CompoundTerm("$dbg_residuals", new Term[] { attrInfo, new VarTerm(GoalsVar) }),
            new CompoundTerm("=", new Term[] { new VarTerm(RootsVar), rootsList }),
        });

        // The bracket, exactly as a breakpoint condition's (see
        // EvaluateBreakpointCondition): nothing the projection does may stop, step, or
        // leak into the suspended query's debug tables.
        var savedMode = _mode;
        var savedDepth = _lastStopDepth;
        var savedRedo = _lastStopWasRedo;
        var savedCurrent = Current;
        _mode = StepMode.Continue;
        _conditionEval = true;
        _engine.DebugTransplantSource = engine;
        var scope = _engine.BeginDebugEvaluation();
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(EvaluationTimeout);
            using var solutions = _engine.QueryAll(goal, cts.Token).GetEnumerator();
            if (!solutions.MoveNext()) { ResidDiag("projection goal failed"); return null; }
            var sol = solutions.Current;

            // Positional map: the roots list materializes with the same fresh names the
            // residual goals carry, and its order is the transplant's address order.
            var addrToCopyName = new Dictionary<int, string>();
            var owners = new List<string>();
            int i = 0;
            foreach (Term root in ResidualProjection.ListElements(sol[RootsVar]))
            {
                if (i >= orderedAddrs.Count) break;
                if (root is VarTerm v)
                {
                    addrToCopyName[orderedAddrs[i]] = v.Name;
                    owners.Add(v.Name);
                }
                i++;
            }
            ResidDiag("owners=" + string.Join(",", owners)
                + " goals=" + (sol[GoalsVar]?.ToString() ?? "null"));
            if (owners.Count == 0) return null;

            var unattached = new List<Term>();
            var byOwner = ResidualProjection.BucketByOwner(
                ResidualProjection.ListElements(sol[GoalsVar]), owners, unattached);
            if (byOwner.Count == 0) return null;
            return (addrToCopyName, byOwner);
        }
        catch (Exception ex)
        {
            ResidDiag("projection threw: " + ex);
            if (ShumwayDebugHelper.DiagEnabled)
                ShumwayDebugHelper.DiagLine("residual projection failed: " + ex.Message);
            return null;
        }
        finally
        {
            _engine.DebugTransplantSource = null;
            _engine.EndDebugEvaluation(scope);
            _conditionEval = false;
            _mode = savedMode;
            _lastStopDepth = savedDepth;
            _lastStopWasRedo = savedRedo;
            Current = savedCurrent;
        }
    }
}
