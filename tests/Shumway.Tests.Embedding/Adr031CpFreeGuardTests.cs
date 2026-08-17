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

    private static PrologEngine Activation(Mode m) => m switch
    {
        Mode.Tier0 => Tier0(),
        Mode.Tier1Runtime => Tier1Runtime(),
        _ => Tier1Bundle(),
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void HotRecursion_GuardFailsPerIteration_Terminates(Mode m)
    {
        var e = Activation(m);
        // 50k iterations of guard-fail → next clause → self-tail loop.
        Assert.True(e.Query("loop(50000).").Success);
        Assert.Single(e.QueryAll("loop(50000)."));   // the commit is deterministic
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ThreeClauseChain_EachGuardRoutesCorrectly(Mode m)
    {
        var e = Activation(m);
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
        var e = Activation(m);
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
        var e = Activation(m);
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

    private static PrologEngine Activation(Adr031CpFreeGuardTests.Mode m)
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
        var e = Activation(m);
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
        var e = Activation(m);
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
        var e = Activation(m);
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
    public void GuardCallStaging_FreshVarCompoundAndList_Widened(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The corpus-ranked staging widenings: put_variable_y (a fresh OUTPUT
        // argument for the guard call, read post-cut), put_structure and
        // put_list (compound/list arguments built for the call). Each shape
        // must commit correctly and — the canary — fully undo on guard failure.
        var e = TierGEngine(m,
            ":- public p/2, q/2, r/3.\n"
            // Fresh-var output arg (put_variable_y), used both in a later
            // guard goal AND post-cut.
            + "dbl(X, Y) :- Y is X * 2.\n"
            + "p(X, R) :- dbl(X, D), D > 5, !, R = big(D).\n"
            + "p(_, R) :- R = small.\n"
            // Compound argument (put_structure).
            + "tagok(f(X)) :- X > 0.\n"
            + "q(X, R) :- tagok(f(X)), !, R = pos.\n"
            + "q(_, R) :- R = neg.\n"
            // List argument (put_list).
            + "firstpos([H|_]) :- H > 0.\n"
            + "r(X, Y, R) :- firstpos([X,Y]), !, R = yes.\n"
            + "r(_, _, R) :- R = no.\n");
        Assert.True(e.Query("p(4, R), R == big(8).").Success);
        Assert.True(e.Query("p(2, R), R == small.").Success);    // 4 > 5 fails → undo D
        Assert.Single(e.QueryAll("p(4, R)."));
        Assert.Single(e.QueryAll("p(2, R)."));

        Assert.True(e.Query("q(3, R), R == pos.").Success);
        Assert.True(e.Query("q(-3, R), R == neg.").Success);     // undo the f(X) build
        Assert.Single(e.QueryAll("q(3, R)."));

        Assert.True(e.Query("r(1, 9, R), R == yes.").Success);
        Assert.True(e.Query("r(-1, 9, R), R == no.").Success);   // undo the list build
        Assert.Single(e.QueryAll("r(1, 9, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void CalleeCut_CommitsClauseSelection_AllRoutes(
        Adr031CpFreeGuardTests.Mode m)
    {
        var e = TierGEngine(m,
            ":- public p/2, w/2, d/2.\n"
            // Det classify-style callee (all-but-last commit): inlined with the
            // cut splitting each clause's fail routing.
            + "cls(X, neg) :- X < 0, !.\n"
            + "cls(X, zero) :- X =:= 0, !.\n"
            + "cls(_, pos).\n"
            + "p(X, R) :- cls(X, C), !, R = C.\n"
            + "p(_, R) :- R = none.\n"
            // POST-cut failure exits the callee (no later alternatives tried):
            // cc(5): clause 1 commits, then 5 > 100 fails → cc FAILS (clause 2
            // must NOT run) → outer clause 2.
            + "cc(X) :- X > 0, !, X > 100.\n"
            + "cc(X) :- X < -1000.\n"
            + "w(X, R) :- cc(X), !, R = big.\n"
            + "w(_, R) :- R = small.\n"
            // A DET callee may sit MID-guard (a later fallible goal is fine:
            // det ⇒ no second solution to retry).
            + "d(X, R) :- cls(X, C), C == neg, !, R = yes.\n"
            + "d(_, R) :- R = no.\n");
        Assert.True(e.Query("p(-5, R), R == neg.").Success);
        Assert.True(e.Query("p(0, R), R == zero.").Success);
        Assert.True(e.Query("p(7, R), R == pos.").Success);
        Assert.Single(e.QueryAll("p(-5, R)."));
        Assert.Single(e.QueryAll("p(7, R)."));

        Assert.True(e.Query("w(5, R), R == small.").Success);      // post-cut fail
        Assert.True(e.Query("w(200, R), R == big.").Success);      // post-cut pass
        Assert.True(e.Query("w(-2000, R), R == big.").Success);    // pre-cut fail → clause 2
        Assert.True(e.Query("w(-5, R), R == small.").Success);     // both fail
        Assert.Single(e.QueryAll("w(5, R)."));

        Assert.True(e.Query("d(-3, R), R == yes.").Success);
        Assert.True(e.Query("d(3, R), R == no.").Success);         // C==pos ≠ neg → undo → clause 2
        Assert.Single(e.QueryAll("d(-3, R)."));
        Assert.Single(e.QueryAll("d(3, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void CalleeAltRestore_HeadBindingUndone_BetweenAlternatives(
        Adr031CpFreeGuardTests.Mode m)
    {
        // SOUNDNESS REGRESSION (the missing per-alternative untrail): clause 1
        // BINDS the unbound argument (Y := a) in its head, then its body fails —
        // clause 2 must see Y UNBOUND again to bind Y := b. Without the
        // callee-entry-marks untrail, Y stays a and cb fails entirely.
        var e = TierGEngine(m,
            ":- public p/2.\n"
            + "cb(a, R) :- R = 1, 2 > 3.\n"          // head binds, body fails
            + "cb(b, 2).\n"
            + "p(Y, N) :- cb(Y, N), !.\n"
            + "p(_, none).\n");
        Assert.True(e.Query("p(Y, N), Y == b, N == 2.").Success);
        Assert.Single(e.QueryAll("p(Y, N)."));
        Assert.True(e.Query("p(a, N), N == none.").Success);   // bound a: body fails, b mismatch
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void NonLastRecursiveClause_WithoutCut_KeepsCp(
        Adr031CpFreeGuardTests.Mode m)
    {
        // SOUNDNESS REGRESSION (self-tail in a non-last clause): when a deeper
        // iteration fails, real backtracking returns to THIS iteration's later
        // alternatives — the in-place loop can't, so the shape must keep its
        // CP. f([9,-1]): clause 1 recurses into [-1] which fails both clauses;
        // real backtracking then tries clause 2 on [9,-1] → SUCCEEDS.
        var e = TierGEngine(m,
            ":- public p/2.\n"
            + "f([H|T]) :- H > 0, f(T).\n"           // recursive, NON-last, no cut
            + "f([9|_]).\n"
            + "p(L, R) :- f(L), !, R = yes.\n"
            + "p(_, R) :- R = no.\n");
        Assert.True(e.Query("p([9,-1], R), R == yes.").Success);
        Assert.Single(e.QueryAll("p([9,-1], R)."));
        Assert.True(e.Query("p([1,-1], R), R == no.").Success);
        // With a committing cut the loop IS sound (clause 2 pruned).
        var e2 = TierGEngine(m,
            ":- public q/2.\n"
            + "g([H|T]) :- H > 0, !, g(T).\n"
            + "g([]).\n"
            + "q(L, R) :- g(L), !, R = yes.\n"
            + "q(_, R) :- R = no.\n");
        Assert.True(e2.Query("q([1,2], R), R == yes.").Success);
        Assert.True(e2.Query("q([1,-2], R), R == no.").Success);
        Assert.Single(e2.QueryAll("q([1,2], R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void G3_NestedFailDirectCallees_InlineTransitively(
        Adr031CpFreeGuardTests.Mode m)
    {
        var e = TierGEngine(m,
            ":- public p/2, q/2.\n"
            // val/1 calls h1/1 (leaf rule, det) NON-tail then tests more.
            + "h1(X) :- X > 3.\n"
            + "val(X) :- h1(X), X < 100.\n"
            + "p(X, R) :- val(X), !, R = inr.\n"
            + "p(_, R) :- R = outr.\n"
            // wrap/2 calls a DET multi-clause inner (cls2, all-but-last-cut)
            // mid-body — allowed by the nested det rule.
            + "cls2(X, neg) :- X < 0, !.\n"
            + "cls2(_, pos).\n"
            + "wrap(X, C) :- cls2(X, C), C == pos.\n"
            + "q(X, R) :- wrap(X, C0), !, R = ok(C0).\n"
            + "q(_, R) :- R = none.\n");
        Assert.True(e.Query("p(50, R), R == inr.").Success);
        Assert.True(e.Query("p(2, R), R == outr.").Success);      // inner h1 fails
        Assert.True(e.Query("p(200, R), R == outr.").Success);    // outer test fails
        Assert.Single(e.QueryAll("p(50, R)."));
        Assert.Single(e.QueryAll("p(2, R)."));

        Assert.True(e.Query("q(5, R), R == ok(pos).").Success);
        Assert.True(e.Query("q(-5, R), R == none.").Success);     // cls2→neg, C==pos fails, undo
        Assert.Single(e.QueryAll("q(5, R)."));
        Assert.Single(e.QueryAll("q(-5, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void G3_DeepCutCallee_CallThenCut_Inlines(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The has_loadevent shape: each callee clause CALLS an (inlinable)
        // inner then commits with a DEEP cut (AllocateGetLevel + Cut slot +
        // the fused cut_deallocate_proceed epilogue). The inline emission
        // treats the deep cut as the same flush-only split as a neck cut —
        // the inlined inner pushed no choice points.
        var e = TierGEngine(m,
            ":- public p/2.\n"
            + "info(X) :- X > 10.\n"
            + "hasit(X) :- info(X), !.\n"
            + "hasit(X) :- X < -10, !.\n"
            + "p(X, R) :- hasit(X), !, R = yes.\n"
            + "p(_, R) :- R = no.\n");
        Assert.True(e.Query("p(50, R), R == yes.").Success);      // clause 1 commits
        Assert.True(e.Query("p(-50, R), R == yes.").Success);     // clause 1 fails → 2
        Assert.True(e.Query("p(0, R), R == no.").Success);        // both fail → outer 2
        Assert.Single(e.QueryAll("p(50, R)."));
        Assert.Single(e.QueryAll("p(-50, R)."));
        Assert.Single(e.QueryAll("p(0, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Adr033_ContinuationStack_SharedCopies_SameAnswers(
        Adr031CpFreeGuardTests.Mode m)
    {
        // ADR-033 — the continuation-stack mechanism (ONE shared copy per
        // callee + push/pop routing) must be observationally identical to the
        // per-site duplication. Two guards call the SAME fail-direct callee
        // (the sharing case), plus a self-tail-recursive callee (the loop
        // inside the shared copy), plus deep-fail undo.
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = TierGEngine(m,
                ":- public p/2, q/2, r/2.\n"
                + "cls(X, neg) :- X < 0, !.\n"
                + "cls(X, zero) :- X =:= 0, !.\n"
                + "cls(_, pos).\n"
                // TWO sites calling cls/2 → one shared copy, two continuations.
                + "p(X, R) :- cls(X, C), !, R = a(C).\n"
                + "p(_, R) :- R = none.\n"
                + "q(X, R) :- cls(X, C), C == neg, !, R = b.\n"
                + "q(_, R) :- R = nb.\n"
                // Self-tail loop inside the shared copy + deep-fail restore.
                + "allp([]).\n"
                + "allp([H|T]) :- H > 0, allp(T).\n"
                + "r(L, R) :- R = t, allp(L), !.\n"
                + "r(_, R) :- R = f.\n");
            Assert.True(e.Query("p(-5, R), R == a(neg).").Success);
            Assert.True(e.Query("p(0, R), R == a(zero).").Success);
            Assert.True(e.Query("p(7, R), R == a(pos).").Success);
            Assert.Single(e.QueryAll("p(-5, R)."));
            Assert.True(e.Query("q(-3, R), R == b.").Success);
            Assert.True(e.Query("q(3, R), R == nb.").Success);
            Assert.Single(e.QueryAll("q(-3, R)."));
            Assert.True(e.Query("r([1,2,3], R), R == t.").Success);
            Assert.True(e.Query("r([1,-2], R), R == f.").Success);   // undo R=t
            Assert.Single(e.QueryAll("r([1,-2], R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Adr033_ThrowMidCopy_CatchRebalancesStack(
        Adr031CpFreeGuardTests.Mode m)
    {
        // Exception safety: the guard callee THROWS mid-copy (arith on an
        // atom) with a continuation entry pushed; catch/3 must truncate the
        // stack (SnapGuardContTop) so subsequent guard calls stay balanced.
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = TierGEngine(m,
                ":- public s/2, driver/2.\n"
                + "chk(X, lo) :- X < 10, !.\n"
                + "chk(_, hi).\n"
                + "s(X, R) :- chk(X, C), !, R = C.\n"
                + "s(_, R) :- R = err.\n"
                + "driver(X, R) :- catch(s(X, R), _, R = caught).\n");
            // The throw: X = an atom → chk's X < 10 raises type_error INSIDE
            // the shared copy (entry pushed, never popped) → catch truncates.
            Assert.True(e.Query("driver(banana, R), R == caught.").Success);
            // The stack is balanced again: further guard calls behave.
            Assert.True(e.Query("driver(3, R), R == lo.").Success);
            Assert.True(e.Query("driver(50, R), R == hi.").Success);
            Assert.True(e.Query("driver(banana, R2), driver(4, R), R == lo.").Success);
            Assert.Single(e.QueryAll("driver(3, R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Adr033_CrossTail_ComposesThroughSharedCopies(
        Adr031CpFreeGuardTests.Mode m)
    {
        // ADR-033 cross-tail — the Arity helper-chain idiom: valid/1 tail-calls
        // range/1 (LCO). Under continuations the tail is a `br` to range's
        // shared copy, INHERITING the guard's continuations: range's success
        // returns to the guard, its failure lands in the guard's restore stub.
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = TierGEngine(m,
                ":- public p/2, q/2, w/2.\n"
                + "range(X) :- X > 0, X < 100.\n"          // det leaf-ish target
                + "valid(X) :- integer(X), range(X).\n"    // cross-tail (last clause)
                + "p(X, R) :- valid(X), !, R = ok.\n"
                + "p(_, R) :- R = bad.\n"
                // Cross-tail in a CUT-COMMITTED non-last clause.
                + "cls2(X, low) :- X < 50, !, range(X).\n"
                + "cls2(_, hi).\n"
                + "q(X, R) :- cls2(X, C), !, R = C.\n"
                + "q(_, R) :- R = none.\n"
                // Det target chain → the CALLER stays det → allowed MID-guard.
                + "w(X, R) :- valid(X), X > 10, !, R = big.\n"
                + "w(_, R) :- R = small.\n");
            Assert.True(e.Query("p(5, R), R == ok.").Success);
            Assert.True(e.Query("p(500, R), R == bad.").Success);   // range fails in tail
            Assert.True(e.Query("p(foo, R), R == bad.").Success);   // integer/1 fails pre-tail
            Assert.Single(e.QueryAll("p(5, R)."));
            Assert.Single(e.QueryAll("p(500, R)."));

            Assert.True(e.Query("q(5, R), R == low.").Success);
            Assert.True(e.Query("q(80, R), R == hi.").Success);
            // Committed clause 1, then range(-5) fails in the tail → cls2
            // FAILS (clause 2 must NOT run — selection was committed) → outer.
            Assert.True(e.Query("q(-5, R), R == none.").Success);
            Assert.Single(e.QueryAll("q(5, R)."));
            Assert.Single(e.QueryAll("q(-5, R)."));

            Assert.True(e.Query("w(50, R), R == big.").Success);
            Assert.True(e.Query("w(5, R), R == small.").Success);   // mid-guard retry-free
            Assert.Single(e.QueryAll("w(50, R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Fact]
    public void G3_MutualRecursion_Rejected_ByDescribe()
    {
        // DUPLICATION mode: mutual recursion cannot be statically inlined —
        // the visiting set rejects the cycle (describe-level check; running
        // it would loop). Under CONTINUATIONS the same TAIL cycle is accepted
        // (see Adr033_TailCycle_AcceptedByDescribe_UnderContinuations).
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var ma = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "ma(X):-mb(X).").ReadAll().ToList());
        var mb = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "mb(X):-ma(X).").ReadAll().ToList());
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate>
        {
            [ma.FunctorId] = ma,
            [mb.FunctorId] = mb,
        };
        Assert.False(IlPredicateCompiler.TryDescribeFailDirectCallee(
            ma, map, out _, out var rej));
        Assert.Equal(IlPredicateCompiler.FailDirectReject.HasCalls, rej);
    }

    [Fact]
    public void Adr033_TailCycle_AcceptedByDescribe_UnderContinuations()
    {
        // Deep G3 v1 — a TAIL cycle (mutual tail recursion) composes through
        // the shared copies: `br` into the in-flight participant's copy, LCO.
        // The cycle edge's det is conservatively FALSE.
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var ma = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "ma(X):-mb(X).").ReadAll().ToList());
        var mb = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "mb(X):-ma(X).").ReadAll().ToList());
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate>
        {
            [ma.FunctorId] = ma,
            [mb.FunctorId] = mb,
        };
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            Assert.True(IlPredicateCompiler.TryDescribeFailDirectCallee(
                ma, map, out var cls, out _));
            Assert.Single(cls!);
            Assert.Equal(mb.FunctorId, cls![0].CrossTailFid);
            // Cycle edge → conservative nondet → the caller stays out of
            // mid-guard positions.
            Assert.False(IlPredicateCompiler.FailDirectCalleeIsDet(cls!));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Fact]
    public void Adr033_NonTailCycle_StillRejected_UnderContinuations()
    {
        // Deep G3 v1 — a NON-tail cycle stays rejected even under
        // continuations: a re-entered copy's IL locals would clobber the
        // outer activation's entry marks (needs real frames — deferred).
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var na = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "na(X):-nb(X),X>0.").ReadAll().ToList());
        var nb = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "nb(X):-na(X),X<100.").ReadAll().ToList());
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate>
        {
            [na.FunctorId] = na,
            [nb.FunctorId] = nb,
        };
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            Assert.False(IlPredicateCompiler.TryDescribeFailDirectCallee(
                na, map, out _, out var rej));
            Assert.Equal(IlPredicateCompiler.FailDirectReject.HasCalls, rej);
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Fact]
    public void Adr033_DeepChain_FreshBudgetPerCopy_UnderContinuations()
    {
        // Deep G3 v1 — a 4-level chain whose CUMULATIVE size exceeds
        // FailDirectMaxTotalBytes while every level fits the per-callee caps:
        // duplication rejects (the budget bounds per-site growth); the
        // continuation mechanism accepts (one shared copy per callee — no
        // cumulative duplication), each copy still bounded by the caps.
        string Bulk(string name, string next)
        {
            var args = string.Join(",", System.Linq.Enumerable.Range(1, 80));
            return next.Length == 0
                ? $"{name}(X):-X=f({args})."
                : $"{name}(X):-{next}(Y),X=f({args}),Y=X.";
        }
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var b5 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            Bulk("b5", "")).ReadAll().ToList());
        var b4 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            Bulk("b4", "b5")).ReadAll().ToList());
        var b3 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            Bulk("b3", "b4")).ReadAll().ToList());
        var b2 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            Bulk("b2", "b3")).ReadAll().ToList());
        var b1 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            Bulk("b1", "b2")).ReadAll().ToList());
        // Each level must individually fit the caps or the test proves
        // nothing. The DUP budget consumes b1..b4 (the leaf b5 classifies as
        // an inlinable leaf rule and is budget-free), so 4 budgeted levels
        // must exceed the cumulative cap.
        Assert.True(b1.BytecodeUnfused.Length <= IlPredicateCompiler.FailDirectMaxBytes);
        Assert.True(b1.BytecodeUnfused.Length * 4
                    > IlPredicateCompiler.FailDirectMaxTotalBytes);
        var map = new System.Collections.Generic.Dictionary<
            int, Shumway.Compiler.Wam.CompiledPredicate>
        {
            [b1.FunctorId] = b1, [b2.FunctorId] = b2,
            [b3.FunctorId] = b3, [b4.FunctorId] = b4,
            [b5.FunctorId] = b5,
        };
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = false;
        try
        {
            Assert.False(IlPredicateCompiler.TryDescribeFailDirectCallee(
                b1, map, out _, out var rej));
            Assert.Equal(IlPredicateCompiler.FailDirectReject.HasCalls, rej);
            IlPredicateCompiler.CpFreeGuardContinuations = true;
            Assert.True(IlPredicateCompiler.TryDescribeFailDirectCallee(
                b1, map, out _, out _));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Adr033_MutualTailRecursion_RunsThroughSharedCopies(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The even/odd idiom — mutual TAIL recursion as a guard callee. Under
        // continuations the copies compose by `br` (LCO): O(1) continuation
        // stack regardless of depth. All-var heads keep the chains unindexed.
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = TierGEngine(m,
                ":- public p/2.\n"
                + "even(N) :- N =:= 0.\n"
                + "even(N) :- N > 0, M is N - 1, odd(M).\n"
                + "odd(N) :- N > 0, M is N - 1, even(M).\n"
                + "p(N, R) :- even(N), !, R = e.\n"
                + "p(_, R) :- R = o.\n");
            Assert.True(e.Query("p(10, R), R == e.").Success);
            Assert.True(e.Query("p(7, R), R == o.").Success);
            Assert.True(e.Query("p(0, R), R == e.").Success);
            Assert.True(e.Query("p(-4, R), R == o.").Success);
            Assert.Single(e.QueryAll("p(10, R)."));
            Assert.Single(e.QueryAll("p(7, R)."));
            // Deep: tail cycles push nothing on the continuation stack.
            Assert.True(e.Query("p(20000, R), R == e.").Success);
            Assert.True(e.Query("p(20001, R), R == o.").Success);
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Adr033_SingleClauseWrapper_InheritsCrossTailMultiplicity(
        Adr031CpFreeGuardTests.Mode m)
    {
        // MULTIPLICITY REGRESSION — c/2 is a SINGLE-clause wrapper that
        // cross-tails a MULTI-solution target. A ClauseCount==1 "trivially
        // det" shortcut would let c sit mid-guard, committing to m's first
        // solution (B=1) — B > 1 then fails and the correct second solution
        // (B=2) is never tried. The det check must follow CrossTailDet.
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = TierGEngine(m,
                ":- public t/2.\n"
                + "m(X, R) :- X > 0, R = 1.\n"
                + "m(X, R) :- X > 0, R = 2.\n"
                + "c(X, B) :- m(X, B).\n"
                + "t(X, R) :- c(X, B), B > 1, !, R = big.\n"
                + "t(_, R) :- R = small.\n");
            Assert.True(e.Query("t(5, R), R == big.").Success);   // via m's 2nd solution
            Assert.True(e.Query("t(-5, R), R == small.").Success);
            Assert.Single(e.QueryAll("t(5, R)."));
            Assert.Single(e.QueryAll("t(-5, R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
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
        e.ConsultString("""
            :- public g/2.
            g(X, hit) :- X = 5, !.
            g(_, miss).
            """);
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
