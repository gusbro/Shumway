using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-031 — CP-free guard commit. A non-last chain clause of the shape
/// <c>Head :- IntCmpGuard, !, Body.</c> is emitted in Tier-1 without its entry
/// choice point: guard failure branches directly to the next clause, the commit
/// pushes/tears down nothing. These tests pin the semantic differential — the
/// CP-free emission must be observationally identical to the chain-plus-cut —
/// across runtime promotion (regions), the persisted-IL bundle, and Tier-0
/// (which is untouched by this ADR and serves as the oracle).
/// </summary>
public class Adr031CpFreeGuardTests
{
    private const string Program =
        ":- public loop/1, cls/2, gsum/2, mixed/2.\n"
        // The canonical hot shape: guard FAILS every recursive iteration,
        // succeeds once at the end.
        + "loop(N) :- N =< 0, !.\n"
        + "loop(N) :- M is N - 1, loop(M).\n"
        // Guard-success commit + guard-fail fallthrough, three-clause chain
        // (clause 0 and 1 both CP-free, clause 2 the catch-all).
        + "cls(N, neg)  :- N < 0, !.\n"
        + "cls(N, zero) :- N =:= 0, !.\n"
        + "cls(_, pos).\n"
        // Post-commit body with a real call (the split emission's second half).
        + "gsum(N, R) :- N =< 1, !, base(N, R).\n"
        + "gsum(N, R) :- M is N - 1, gsum(M, S), R is S + N.\n"
        + "base(N, N).\n"
        // Guard-fail into a NONDET second clause: backtracking through the
        // directly-branched-to clause must still enumerate.
        + "mixed(N, guarded) :- N > 100, !.\n"
        + "mixed(_, a).\n"
        + "mixed(_, b).\n";

    private static PrologEngine Tier0()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;          // never promote
        e.ConsultString(Program);
        return e;
    }

    private static PrologEngine Tier1Runtime()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;          // promote on first call (region path)
        e.ConsultString(Program);
        return e;
    }

    private static PrologEngine Tier1Bundle()
    {
        var bundle = new Bundle(new[] { new BundleEntry("adr031", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        return e;
    }

    public enum Mode { Tier0, Tier1Runtime, Tier1Bundle }
    public static TheoryData<Mode> Modes => new() { Mode.Tier0, Mode.Tier1Runtime, Mode.Tier1Bundle };

    private static PrologEngine Engine(Mode m) => m switch
    {
        Mode.Tier0 => Tier0(),
        Mode.Tier1Runtime => Tier1Runtime(),
        _ => Tier1Bundle(),
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void HotRecursion_GuardFailsPerIteration_Terminates(Mode m)
    {
        var e = Engine(m);
        // 50k iterations of guard-fail → next clause → self-tail loop.
        Assert.True(e.Query("loop(50000).").Success);
        Assert.Single(e.QueryAll("loop(50000)."));   // the commit is deterministic
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ThreeClauseChain_EachGuardRoutesCorrectly(Mode m)
    {
        var e = Engine(m);
        Assert.True(e.Query("cls(-5, R), R == neg.").Success);
        Assert.True(e.Query("cls(0, R), R == zero.").Success);
        Assert.True(e.Query("cls(7, R), R == pos.").Success);
        // Commits: exactly one solution each.
        Assert.Single(e.QueryAll("cls(-5, R)."));
        Assert.Single(e.QueryAll("cls(0, R)."));
        Assert.Single(e.QueryAll("cls(7, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void PostCommitBody_WithRealCall_RunsAfterCpFreeCommit(Mode m)
    {
        var e = Engine(m);
        // The commit clause's body is a real call (base/2), exercising the split
        // emission's second half; the recursion sums 1+2+…+N.
        Assert.True(e.Query("gsum(1, R), R == 1.").Success);
        Assert.True(e.Query("gsum(4, R), R == 10.").Success);
        Assert.Single(e.QueryAll("gsum(100, R)."));
        Assert.True(e.Query("gsum(100, R), R == 5050.").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GuardFail_IntoNondetElseClauses_StillEnumerates(Mode m)
    {
        var e = Engine(m);
        // Guard fails (N=5 ≤ 100) → direct branch to clause 2 → backtracking must
        // still reach clause 3.
        var sols = e.QueryAll("mixed(5, R).").Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "a", "b" }, sols);
        // Guard succeeds → committed single solution.
        var g = e.QueryAll("mixed(200, R).").Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "guarded" }, g);
    }

    [Fact]
    public void GuardStats_CountAcceptsAndRejects()
    {
        // ADR-032 sizing counters — deterministic direct-recogniser smoke
        // (static counters are shared with concurrently-running tests, so
        // assert MOVEMENT, not exact values; promotion is background, so the
        // engine path can't be asserted synchronously).
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();

        // Tier-A accept.
        long a0 = IlPredicateCompiler.CpFreeGuardStats.AcceptTotal;
        var cpA = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "s(X,R):-X>0,!,R=a. s(_,R):-R=b.").ReadAll().ToList());
        Assert.True(IlPredicateCompiler.TryGetCpFreeGuard(
            cpA.BytecodeUnfused, 9, cpA.BytecodeUnfused.Length, 2,
            calleeMap: null, cpA.CallSites, out _));
        Assert.True(IlPredicateCompiler.CpFreeGuardStats.AcceptTotal > a0);

        // Reject: multi-clause fail-direct callee NOT immediately before the
        // cut (the multi-solution soundness rule).
        long r0 = IlPredicateCompiler.CpFreeGuardStats.RejectTotal;
        var nd = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "nd(X):-X>0. nd(X):-X<100.").ReadAll().ToList());
        var cpT = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "t(X,R):-nd(X),X>1,!,R=hit. t(_,R):-R=miss.").ReadAll().ToList());
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate>
        { [cpT.CallSites[0].CalleeFunctorId] = nd };
        Assert.False(IlPredicateCompiler.TryGetCpFreeGuard(
            cpT.BytecodeUnfused, 9, cpT.BytecodeUnfused.Length, 2,
            map, cpT.CallSites, out _));
        Assert.True(IlPredicateCompiler.CpFreeGuardStats.RejectTotal > r0);
    }

    [Fact]
    public void Recognizer_Tiers_ClassifyCorrectly()
    {
        // Direct recogniser pins (bytecode-level). Clause 0 starts after
        // try_me_else (offset 9).
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();

        // Tier A: pure comparison — no snapshot, no reg save, frameless.
        var cp = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "loop(N):-N=<0,!. loop(N):-M is N-1,loop(M).").ReadAll().ToList());
        Assert.True(IlPredicateCompiler.TryGetCpFreeGuard(
            cp.BytecodeUnfused, 9, cp.BytecodeUnfused.Length, 1,
            calleeMap: null, cp.CallSites, out var gA));
        Assert.Equal((byte)Opcode.NeckCut, cp.BytecodeUnfused[gA.CutPc]);
        Assert.False(gA.NeedsSnapshot);
        Assert.False(gA.NeedsRegSave);
        Assert.False(gA.Framed);

        // Tier B: a binding guard (get_value_x) — accepted WITH snapshot.
        var cp2 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "max(X,Y,X):-X>=Y,!. max(X,Y,Y).").ReadAll().ToList());
        Assert.True(IlPredicateCompiler.TryGetCpFreeGuard(
            cp2.BytecodeUnfused, 9, cp2.BytecodeUnfused.Length, 3,
            calleeMap: null, cp2.CallSites, out var gB));
        Assert.True(gB.NeedsSnapshot);
        Assert.False(gB.Framed);

        // Tier G: a guard CALL — eligible only when the calleeMap resolves it
        // to an inlinable single-clause leaf; without a calleeMap → rejected.
        var cp3 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "p(X):-q(X),!,r(X). p(X):-s(X).").ReadAll().ToList());
        Assert.False(IlPredicateCompiler.TryGetCpFreeGuard(
            cp3.BytecodeUnfused, 9, cp3.BytecodeUnfused.Length, 1,
            calleeMap: null, cp3.CallSites, out _));

        // With a calleeMap mapping q/1 to an inlinable leaf rule → accepted,
        // framed + deep cut + snapshot + reg save.
        var calleeQ = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "q(X):-X>0.").ReadAll().ToList());
        Assert.True(IlPredicateCompiler.IsInlinableLeafRule(calleeQ));
        int qFid = cp3.CallSites[0].CalleeFunctorId;
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate> { [qFid] = calleeQ };
        Assert.True(IlPredicateCompiler.TryGetCpFreeGuard(
            cp3.BytecodeUnfused, 9, cp3.BytecodeUnfused.Length, 1,
            map, cp3.CallSites, out var gG));
        Assert.True(gG.Framed);
        Assert.True(gG.DeepCut);
        Assert.True(gG.NeedsSnapshot);
        Assert.True(gG.NeedsRegSave);
        Assert.Equal((byte)Opcode.Cut, cp3.BytecodeUnfused[gG.CutPc]);
    }
}

/// <summary>
/// ADR-031 case B — the BINDING-guard tier. The guard may bind variables
/// (head unification / <c>=/2</c>) before failing, so the CP-free fail path
/// must restore exactly what the skipped choice point's pop would have:
/// bindings untrailed, heap reset, HB restored, queued wakeups cleared. The
/// canary throughout: after a guard binds-then-fails, the NEXT clause must see
/// the arguments exactly as they were at entry.
/// </summary>
public class Adr031BindingGuardTests
{
    private const string Program =
        ":- public umax/3, pick/2, sh/2, lh/3.\n"
        // The classic: threaded head (repeated var) — get_value_x binds when M
        // is unbound, then the comparison decides.
        + "umax(X,Y,X) :- X >= Y, !.\n"
        + "umax(_,Y,Y).\n"
        // Bind-then-fail: R=big binds FIRST, then X>5 fails → the restore must
        // UNBIND R or clause 2's R=small can never succeed.
        + "pick(X,R) :- R = big, X > 5, !.\n"
        + "pick(_,R) :- R = small.\n"
        // Structure guard (get_structure + unify_void).
        + "sh(X,R) :- X = k(_), !, R = yes.\n"
        + "sh(_,R) :- R = no.\n"
        // List guard (put_value_x temp + get_list + unify ops).
        + "lh(X,H,R) :- X = [H|_], !, R = car.\n"
        + "lh(_,_,R) :- R = nil.\n";

    private static PrologEngine Engine(Adr031CpFreeGuardTests.Mode m)
    {
        switch (m)
        {
            case Adr031CpFreeGuardTests.Mode.Tier0:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 0;
                e.ConsultString(Program);
                return e;
            }
            case Adr031CpFreeGuardTests.Mode.Tier1Runtime:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 1;
                e.ConsultString(Program);
                return e;
            }
            default:
            {
                var bundle = new Bundle(new[] { new BundleEntry("adr031b", Program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    public static TheoryData<Adr031CpFreeGuardTests.Mode> Modes => new()
    {
        Adr031CpFreeGuardTests.Mode.Tier0,
        Adr031CpFreeGuardTests.Mode.Tier1Runtime,
        Adr031CpFreeGuardTests.Mode.Tier1Bundle,
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Max_CommitAndFallthrough(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        Assert.True(e.Query("umax(7, 3, M), M == 7.").Success);
        Assert.True(e.Query("umax(2, 9, M), M == 9.").Success);
        Assert.Single(e.QueryAll("umax(7, 3, M)."));
        Assert.Single(e.QueryAll("umax(2, 9, M)."));
        // Equal values commit to clause 1 (X >= Y holds) — exactly one solution.
        Assert.True(e.Query("umax(4, 4, M), M == 4.").Success);
        Assert.Single(e.QueryAll("umax(4, 4, M)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void BindThenFail_NextClauseSeesUnboundArg(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        // Guard binds R=big then X>5 FAILS → R must be UNBOUND again for
        // clause 2 to bind R=small. A broken restore leaves R=big and the
        // query fails entirely.
        Assert.True(e.Query("pick(3, R), R == small.").Success);
        Assert.Single(e.QueryAll("pick(3, R)."));
        // Guard succeeds → committed.
        Assert.True(e.Query("pick(9, R), R == big.").Success);
        Assert.Single(e.QueryAll("pick(9, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StructAndListGuards_RouteAndRestore(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        Assert.True(e.Query("sh(k(1), R), R == yes.").Success);
        Assert.True(e.Query("sh(other, R), R == no.").Success);
        // Unbound arg: the guard BINDS X to k(_) and commits — ISO chain
        // semantics (clause 1 unifies) must be preserved, one solution.
        Assert.Single(e.QueryAll("sh(X, R)."));
        Assert.True(e.Query("sh(X, R), R == yes.").Success);

        Assert.True(e.Query("lh([a,b], H, R), H == a, R == car.").Success);
        Assert.True(e.Query("lh(nil_thing, _, R), R == nil.").Success);
        Assert.Single(e.QueryAll("lh([a,b], H, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GuardCall_InlinableLeafCallee_CommitAndFallthrough(
        Adr031CpFreeGuardTests.Mode m)
    {
        // Tier G — the canonical `p :- check(X), !, ...` with check/1 an
        // inlinable leaf rule. Guard-call failure must branch to the next
        // clause via the restore stub (frame deallocated, registers restored).
        var e = TierGEngine(m,
            ":- public p/2, mix/2, tt/2, ar/2.\n"
            + "check(X) :- X > 5.\n"
            + "p(X, big) :- check(X), !.\n"
            + "p(_, small).\n"
            // Mixed guard: call + comparison after it.
            + "mix(X, both) :- check(X), X < 100, !.\n"
            + "mix(_, nope).\n"
            // Type-test / ==/2 builtin guard (the old case E, now covered).
            + "tt(X, v) :- var(X), !.\n"
            + "tt(X, nv) :- nonvar(X).\n"
            // is/2 (a_int_bin) + cmp guard.
            + "ar(N, lowdouble) :- M is N * 2, M < 10, !.\n"
            + "ar(_, high).\n");
        Assert.True(e.Query("p(9, R), R == big.").Success);
        Assert.True(e.Query("p(3, R), R == small.").Success);
        Assert.Single(e.QueryAll("p(9, R)."));
        Assert.Single(e.QueryAll("p(3, R)."));

        Assert.True(e.Query("mix(50, R), R == both.").Success);
        Assert.True(e.Query("mix(200, R), R == nope.").Success);   // cmp after call fails
        Assert.True(e.Query("mix(2, R), R == nope.").Success);     // call itself fails
        Assert.Single(e.QueryAll("mix(50, R)."));

        Assert.True(e.Query("tt(_, R), R == v.").Success);
        Assert.True(e.Query("tt(a, R), R == nv.").Success);
        Assert.Single(e.QueryAll("tt(a, R)."));

        Assert.True(e.Query("ar(3, R), R == lowdouble.").Success);
        Assert.True(e.Query("ar(50, R), R == high.").Success);
        Assert.Single(e.QueryAll("ar(3, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GuardCall_CalleeBindsThenLaterGoalFails_Restored(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The callee BINDS an output (W = w(X)) and a LATER guard goal fails →
        // the restore stub must undo the callee's binding for clause 2.
        var e = TierGEngine(m,
            ":- public q/2.\n"
            + "bindc(X, w(X)).\n"
            + "q(X, tagged) :- bindc(X, W), W = w(9), !.\n"
            + "q(_, plain).\n");
        Assert.True(e.Query("q(9, R), R == tagged.").Success);
        Assert.True(e.Query("q(3, R), R == plain.").Success);   // W=w(3) ≠ w(9) → undo → clause 2
        Assert.Single(e.QueryAll("q(9, R)."));
        Assert.Single(e.QueryAll("q(3, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GuardCall_HotRecursion_Correct(Adr031CpFreeGuardTests.Mode m)
    {
        // The hot shape case G targets: guard call fails per iteration.
        var e = TierGEngine(m,
            ":- public gloop/1.\n"
            + "done(N) :- N =< 0.\n"
            + "gloop(N) :- done(N), !.\n"
            + "gloop(N) :- M is N - 1, gloop(M).\n");
        Assert.True(e.Query("gloop(20000).").Success);
        Assert.Single(e.QueryAll("gloop(20000)."));
    }

    private static PrologEngine TierGEngine(Adr031CpFreeGuardTests.Mode m, string program)
    {
        switch (m)
        {
            case Adr031CpFreeGuardTests.Mode.Tier0:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 0;
                e.ConsultString(program);
                return e;
            }
            case Adr031CpFreeGuardTests.Mode.Tier1Runtime:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 1;
                e.ConsultString(program);
                return e;
            }
            default:
            {
                var bundle = new Bundle(new[] { new BundleEntry("adr031g", program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void MultiSolutionCallee_LaterGuardGoalRetries_NotCpFree(
        Adr031CpFreeGuardTests.Mode m)
    {
        // SOUNDNESS REGRESSION — pick/2 has TWO solutions for the same input
        // (overlapping clauses binding B differently). The guard's later goal
        // B > 1 fails for B=1 and must RETRY pick to get B=2 — a sequential
        // chain (no CP) could never do that, so the recogniser must REJECT this
        // shape (multi-clause callee not immediately before the cut) and keep
        // the clause CP. Expected: t(5,R) → big via pick's SECOND solution.
        var e = TierGEngine(m,
            ":- public t/2.\n"
            + "pick(X, 1) :- X > 0.\n"
            + "pick(X, 2) :- X > 0.\n"
            + "t(X, R) :- pick(X, B), B > 1, !, R = big.\n"
            + "t(_, R) :- R = small.\n");
        Assert.True(e.Query("t(5, R), R == big.").Success);
        Assert.Single(e.QueryAll("t(5, R)."));
        Assert.True(e.Query("t(-1, R), R == small.").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FailDirect_RecursiveValidator_CommitAndDeepFail(
        Adr031CpFreeGuardTests.Mode m)
    {
        // G2 — the canonical shape: a self-tail-recursive det validator as the
        // guard. Failure DEEP in the walk must reach the guard's restore stub
        // (a direct branch chain), not the engine's backtracking.
        var e = TierGEngine(m,
            ":- public p/2, q/2.\n"
            + "allpos([]).\n"
            + "allpos([H|T]) :- H > 0, allpos(T).\n"
            + "p(L, ok) :- allpos(L), !.\n"
            + "p(_, bad).\n"
            // Mixed tier-B + G2 guard: R=yes binds BEFORE the walk; a deep
            // failure must unbind it for clause 2.
            + "q(L, R) :- R = yes, allpos(L), !.\n"
            + "q(_, R) :- R = no.\n");
        Assert.True(e.Query("p([1,2,3], R), R == ok.").Success);
        Assert.True(e.Query("p([1,2,-3], R), R == bad.").Success);   // fails at depth 3
        Assert.True(e.Query("p([], R), R == ok.").Success);
        Assert.True(e.Query("p(notalist, R), R == bad.").Success);
        Assert.Single(e.QueryAll("p([1,2,3], R)."));
        Assert.Single(e.QueryAll("p([1,2,-3], R)."));
        // Long walk: loop mechanics, constant C# stack, cancellation-poll path.
        Assert.True(e.Query("numlist(1, 10000, L), p(L, R), R == ok.").Success);

        Assert.True(e.Query("q([1,2], R), R == yes.").Success);
        Assert.True(e.Query("q([1,-2], R), R == no.").Success);      // undo R=yes
        Assert.Single(e.QueryAll("q([1,-2], R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FailDirect_MultiClauseCallee_AltRestoresArgs(
        Adr031CpFreeGuardTests.Mode m)
    {
        // G2 — a 3-clause callee where a PARTIAL match in clause 2 clobbers the
        // argument register (unify_variable_x writes A0) before failing; clause
        // 3 must see the original argument (the alt-entry register restore).
        var e = TierGEngine(m,
            ":- public r/2.\n"
            + "w([]).\n"
            + "w([H|_]) :- H > 5.\n"
            + "w(N) :- integer(N), N > 100.\n"
            + "r(X, hit) :- w(X), !.\n"
            + "r(_, miss).\n");
        Assert.True(e.Query("r([], R), R == hit.").Success);
        Assert.True(e.Query("r([9], R), R == hit.").Success);
        Assert.True(e.Query("r([3], R), R == miss.").Success);      // clause 2 partial, then fail
        Assert.True(e.Query("r(200, R), R == hit.").Success);       // clause 3 after 1-2 fail
        Assert.True(e.Query("r(50, R), R == miss.").Success);
        Assert.Single(e.QueryAll("r([3], R)."));
        Assert.Single(e.QueryAll("r(200, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AttvarHookFails_LazyCp_FallsToNextClause_Restored(
        Adr031CpFreeGuardTests.Mode m)
    {
        if (m == Adr031CpFreeGuardTests.Mode.Tier1Bundle)
            return; // clpfd is engine-opt-in (UseClpfd), not bundled — skip.
        var e = m == Adr031CpFreeGuardTests.Mode.Tier0
            ? new PrologEngine { }
            : new PrologEngine { };
        e.IlPromotion.Threshold = m == Adr031CpFreeGuardTests.Mode.Tier0 ? 0 : 1;
        e.UseClpfd();
        e.ConsultString(
            ":- public g/2.\n"
            + "g(X, hit) :- X = 5, !.\n"
            + "g(_, miss).\n");
        // X in 1..3: the guard's X=5 queues the clpfd verify_attributes wakeup;
        // the CP-free commit sees pending wakeups → pushes the LAZY CP with the
        // clause-entry marks → the hook FAILS (5 ∉ 1..3) → backtrack into the
        // lazy CP restores the entry state → clause 2 → miss, X's domain alive.
        var sol = e.Query("X in 1..3, g(X, R), R == miss, X = 2.");
        Assert.True(sol.Success);
        // In-domain value: hook succeeds at the commit → hit.
        var sol2 = e.Query("Y in 1..9, g(Y, R), R == hit, Y == 5.");
        Assert.True(sol2.Success);
    }
}
