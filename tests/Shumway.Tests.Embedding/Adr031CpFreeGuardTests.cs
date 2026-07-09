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
    public void Recognizer_AcceptsIntCmpGuard_RejectsOthers()
    {
        // Direct recogniser pins (bytecode-level).
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var cp = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "loop(N):-N=<0,!. loop(N):-M is N-1,loop(M).").ReadAll().ToList());
        // Clause 0 starts after try_me_else (offset 9).
        Assert.True(IlPredicateCompiler.TryGetCpFreeNeckCutGuard(
            cp.BytecodeUnfused, 9, cp.BytecodeUnfused.Length, out int cut));
        Assert.Equal((byte)Opcode.NeckCut, cp.BytecodeUnfused[cut]);

        // A binding guard (get_value) is NOT eligible in phase 1.
        var cp2 = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "max(X,Y,X):-X>=Y,!. max(X,Y,Y).").ReadAll().ToList());
        Assert.False(IlPredicateCompiler.TryGetCpFreeNeckCutGuard(
            cp2.BytecodeUnfused, 9, cp2.BytecodeUnfused.Length, out _));
    }
}
