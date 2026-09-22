using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>call/1 of a goal known only at run time, dispatched inside the
/// module instead of stepping aside.
///
/// <para>This was the largest deopt source there is. Every call the emitter
/// bakes names its callee by a MARKER, and a marker is interned by the host
/// from a (functor, address) pair, so it cannot be computed from a functor a
/// module meets at run time -- a meta-call had no target and always stepped
/// aside. Measured in a browser, clpfd's propagation loop
/// (`clpfd_run([P|Ps]) :- call(P), clpfd_run(Ps)`) did that 16,378 times in
/// one queens 12 run: 86% of every deopt, and half of all its chains, since a
/// deopt closes the chain and the re-entry pays a fresh staging.</para>
///
/// <para>Correctness first. A meta-call is a CALL: the goal's arguments have
/// to arrive in the right registers, the continuation has to come back to the
/// instruction after it, and the cut barrier has to be the callee's own.
/// Those are the three ways this can be subtly wrong while still answering
/// correctly most of the time, so each has a test that would fail if only
/// that one were broken.</para></summary>
public sealed class InlineMetaCallTests(ITestOutputHelper o)
{
    private const string Corpus = """
        p1(X) :- X = one.
        p2(A, B) :- B = A.
        p3(A, B, C) :- C = f(A, B).
        wide(A, B, C, D, E, F, G, H) :- H = h(A, B, C, D, E, F, G).
        run([]).
        run([G|Gs]) :- call(G), run(Gs).
        tailcall(G) :- call(G).
        after(G, R) :- call(G), R = returned.
        """;

    /// <summary>The arguments have to land in the right registers, in order.
    /// A goal that merely SUCCEEDS proves nothing -- p3 reports what it
    /// received, so a copy that shifted or reversed the arguments shows
    /// up.</summary>
    [Fact]
    public void TheGoalsArgumentsArriveInOrder()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("run([p3(a, b, R)]), R == f(a, b).").Success,
            "the arguments did not arrive in order");

        var (t2, _) = TieredEngine.Build(Corpus);
        Assert.True(t2.Query(
            "run([wide(1, 2, 3, 4, 5, 6, 7, R)]), R == h(1, 2, 3, 4, 5, 6, 7).").Success,
            "the widest meta-call the module takes lost an argument");
    }

    /// <summary>Execution has to CONTINUE after the meta-call. A jump that
    /// forgot the continuation would leave the goal's success looking like
    /// the clause's.</summary>
    [Fact]
    public void ExecutionContinuesAfterTheGoal()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("after(p1(_), R), R == returned.").Success,
            "the continuation after call/1 was lost");

        // Several in a row, each seeing what the last one bound.
        var (t2, _) = TieredEngine.Build(Corpus);
        Assert.True(t2.Query(
            "run([p2(a, X), p2(X, Y), p2(Y, Z)]), Z == a.").Success,
            "a chain of meta-calls lost a binding");
    }

    /// <summary>The shapes the module declines still answer the way the
    /// builtin does: an ATOM goal (no functor id to look up), an unbound goal
    /// (an error), a goal of a predicate that does not exist.</summary>
    [Fact]
    public void TheShapesItDeclinesStillAnswer()
    {
        var (tiered, _) = TieredEngine.Build(Corpus + "\natom_goal.\n");
        Assert.True(tiered.Query("run([atom_goal]).").Success,
            "an atom goal must still run");

        var (t2, _) = TieredEngine.Build(Corpus);
        Assert.False(t2.Query("catch(run([nosuch_goal_here(1)]), _, fail).").Success);

        var (t3, _) = TieredEngine.Build(Corpus);
        Assert.True(t3.Query("catch(run([_]), E, true), nonvar(E).").Success,
            "an unbound goal must raise, and the host must be the one to do it");
    }

    /// <summary>Backtracking THROUGH a meta-call. The goal leaves a choice
    /// point; redoing it has to come back into the caller correctly.</summary>
    [Fact]
    public void BacktrackingThroughAMetaCallWorks()
    {
        const string Program = """
            pick(a).
            pick(b).
            pick(c).
            collect(Gs) :- call(Gs).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("findall(X, collect(pick(X)), L), L == [a, b, c].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query(
            "findall(X, collect(pick(X)), L), L == [a, b, c].").Success,
            "backtracking through a meta-call lost solutions");
    }

    /// <summary>Backtracking INTO a goal reached through a meta-call in a
    /// clause that has no environment frame of its own.
    ///
    /// <para>This is the pairing the frame fix has to survive. A clause whose
    /// only call is a builtin gets no Allocate, so the module builds a frame
    /// around the meta-call to hold CP -- and pops it when control returns.
    /// If the goal left a choice point, that pop happens while the frame is
    /// still reachable: redoing the goal restores an environment the module
    /// already deallocated. Deallocate only reclaims when no choice point
    /// protects the frame, which is exactly the case being leaned on here,
    /// and leaning on it is not the same as having tested it.</para>
    ///
    /// <para>The deterministic version of this passes either way. Only the
    /// solutions AFTER the first say whether the frame survived.</para>
    /// </summary>
    [DiagFact]
    public void BacktrackingIntoAFramelessMetaCall()
    {
        const string Program = """
            pick(a).
            pick(b).
            pick(c).
            bare(G) :- call(G).
            drive(L) :- findall(X, bare(pick(X)), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("drive(L), L == [a, b, c].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        // Warm: the first call of the pair is a cache miss by construction,
        // so a measured run has to come after one.
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query("drive(L), L == [a, b, c].").Success,
            "backtracking through a frameless meta-call lost solutions");
        o.WriteLine($"frameless redo: deopts={WasmTierDelegate.DiagDeopts} "
            + $"chains={WasmTierDelegate.DiagEntries}");
        // The point of the test is the INLINE path. A run that stepped aside
        // would prove the interpreter right and say nothing about the frame.
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>The frame the module built must survive being written OVER.
    ///
    /// <para>The plain redo tests do not discriminate, and the reason is worth
    /// stating: reclaiming a frame only lowers the stack top, so a frame
    /// popped too eagerly still HOLDS its contents and a redo reads them back
    /// intact. The fault only surfaces when something allocates over that
    /// space first. So this one calls a predicate with an environment of its
    /// own between the meta-call and the redo -- which is what lands on the
    /// reclaimed slot -- and only then backtracks in.</para></summary>
    [DiagFact]
    public void AFramelessMetaCallsFrameSurvivesAnAllocationOverIt()
    {
        const string Program = """
            pick(a).
            pick(b).
            pick(c).
            deep(0, []).
            deep(N, [N|T]) :- N > 0, N1 is N - 1, deep(N1, T).
            bare(G) :- call(G).
            one(X) :- bare(pick(X)), deep(12, L), length(L, 12).
            drive(Xs) :- findall(X, one(X), Xs).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("drive(L), L == [a, b, c].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query("drive(L), L == [a, b, c].").Success,
            "a frame the module built was clobbered before the redo");
        o.WriteLine($"overwritten: deopts={WasmTierDelegate.DiagDeopts} "
            + $"chains={WasmTierDelegate.DiagEntries}");
    }

    /// <summary>The same, one level deeper and with a binding to undo: the
    /// goal binds, fails, and the next solution must see the binding gone.
    /// A frame popped too eagerly shows up here as a stale binding rather
    /// than as a missing solution.</summary>
    [DiagFact]
    public void BacktrackingUndoesBindingsAcrossAFramelessMetaCall()
    {
        const string Program = """
            val(1).
            val(2).
            val(3).
            bare(G) :- call(G).
            check(X, Y) :- bare(val(X)), Y is X * 10, Y > 15.
            drive(L) :- findall(X-Y, check(X, Y), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("drive(L), L == [2-20, 3-30].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query("drive(L), L == [2-20, 3-30].").Success,
            "a binding survived backtracking through a frameless meta-call");
        o.WriteLine($"frameless undo: deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>Two frameless meta-calls nested, each leaving choice points:
    /// the frames stack, and the inner redo has to find the outer one still
    /// standing.</summary>
    [DiagFact]
    public void NestedFramelessMetaCallsBacktrackTogether()
    {
        const string Program = """
            a(1).
            a(2).
            b(x).
            b(y).
            bare(G) :- call(G).
            both(I, J) :- bare(a(I)), bare(b(J)).
            drive(L) :- findall(I-J, both(I, J), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query(
            "drive(L), L == [1-x, 1-y, 2-x, 2-y].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query(
            "drive(L), L == [1-x, 1-y, 2-x, 2-y].").Success,
            "nested frameless meta-calls lost the cross product");
        o.WriteLine($"nested: deopts={WasmTierDelegate.DiagDeopts}");
    }

    /// <summary>A cut inside the meta-called goal cuts the GOAL, not the
    /// caller: call/1 is opaque to cut. If the barrier were inherited instead
    /// of refreshed, the cut would prune the caller's choice points too.
    /// </summary>
    [Fact]
    public void ACutInsideTheGoalIsOpaque()
    {
        const string Program = """
            two(1).
            two(2).
            cutter(X) :- two(X), !.
            outer(X, Y) :- two(X), call(cutter(Y)).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query(
            "findall(X-Y, outer(X, Y), L), L == [1-1, 2-1].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query(
            "findall(X-Y, outer(X, Y), L), L == [1-1, 2-1].").Success,
            "the cut inside a meta-called goal pruned the caller");
    }

    /// <summary>A resolution belongs to the ADDRESS MAP it was made against,
    /// and the cache is dropped when that map changes.
    ///
    /// <para>This is the failure answers would never show, and the worst
    /// kind: a stale entry names a predicate that is still on the tier and
    /// still takes that many arguments, so the module jumps to it and
    /// ANSWERS. The arity check does not save it -- the arities match. Only
    /// the lifetime does, which is why the engine's own meta-route cache
    /// carries the same stamp (see MetaRoute.cs).</para>
    ///
    /// <para>Tested at the mechanism rather than end to end, deliberately:
    /// in this harness a redefined predicate is stale for a DIRECT call too,
    /// so an end-to-end test would pass or fail on the harness's eviction
    /// rather than on this stamp, and would keep passing if the stamp were
    /// deleted.</para></summary>
    [Fact]
    public void AResolutionDoesNotOutliveTheMapItWasMadeAgainst()
    {
        var table = new Shumway.Core.WasmResumeTable();
        object mapA = new object();
        object mapB = new object();

        table.NoteMetaResolution(mapA, moduleAtomId: 7, goalKey: 11, appended: 0, resolvedFid: 42);
        Assert.Equal(42, table.MetaLookup(7, 11));

        // A second resolution against the SAME map joins it.
        table.NoteMetaResolution(mapA, moduleAtomId: 7, goalKey: 12, appended: 0, resolvedFid: 43);
        Assert.Equal(42, table.MetaLookup(7, 11));
        Assert.Equal(43, table.MetaLookup(7, 12));

        // A new map drops everything the old one said -- including the pair
        // that is not being re-resolved right now, which is exactly the one
        // that would otherwise answer wrongly later.
        table.NoteMetaResolution(mapB, moduleAtomId: 7, goalKey: 12, appended: 0, resolvedFid: 99);
        Assert.Equal(99, table.MetaLookup(7, 12));
        Assert.Equal(-1, table.MetaLookup(7, 11));
    }

    /// <summary>The counters: the propagation shape stops deopting. The
    /// program below is clpfd_run/1 with the library taken out of it.
    ///
    /// <para>A cache is COLD once. The module cannot resolve a module-tagged
    /// goal, so the first call of each distinct (module, goal functor) pair
    /// steps aside, the host resolves it -- which is what it was doing every
    /// time before -- and fills the cache on its way through. Three distinct
    /// goals, three cold misses, and then none: that shape is the test, and
    /// asserting a flat zero would only have hidden it.</para></summary>
    [DiagFact]
    public void ThePropagationLoopStopsSteppingAside()
    {
        const string Goals = "run([p1(_), p2(a, _), p3(a, b, _), p1(_), p2(b, _), "
            + "p3(c, d, _), p1(_), p2(e, _), p3(f, g, _), p1(_)]).";
        var (engine, _) = TieredEngine.Build(Corpus);

        // Cold: one step aside per distinct pair, no more. Ten goals, three
        // distinct -- so a cache that never filled would show ten.
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query(Goals).Success);
        long cold = WasmTierDelegate.DiagDeopts;
        o.WriteLine($"cold: deopts={cold} hops={WasmTierDelegate.DiagInWasmHops}");
        Assert.True(WasmTierDelegate.DiagEntries > 0, "never reached the tier");
        Assert.Equal(3L, cold);

        // Warm: none at all, and the crossings are in-wasm hops now.
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query(Goals).Success);
        o.WriteLine($"warm: deopts={WasmTierDelegate.DiagDeopts} "
            + $"chains={WasmTierDelegate.DiagEntries} "
            + $"hops={WasmTierDelegate.DiagInWasmHops}");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
        // The goals did not stop running: they cross INSIDE wasm now, which
        // is the whole point. Zero deopts with zero hops would mean the
        // meta-calls vanished, not that they got faster.
        Assert.True(WasmTierDelegate.DiagInWasmHops >= 10,
            $"only {WasmTierDelegate.DiagInWasmHops} in-wasm hops for 10 goals");
    }

    /// <summary>An ATOM goal still steps aside, and the count says so: a zero
    /// here would mean the module took a shape it cannot resolve.</summary>
    [DiagFact]
    public void AnAtomGoalStillStepsAside()
    {
        var (engine, _) = TieredEngine.Build(Corpus + "\natom_goal.\n");
        WasmTierDelegate.ResetDiag();

        Assert.True(engine.Query(
            "run([atom_goal, atom_goal, atom_goal, atom_goal, atom_goal]).").Success);

        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(WasmTierDelegate.DiagDeopts > 0,
            "an atom goal was resolved inside the module");
    }
}
