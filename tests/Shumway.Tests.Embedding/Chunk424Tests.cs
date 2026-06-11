using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 424 — backtrackable builtins and runtime meta-calls INSIDE region
/// members (the chunk-385 exclusion lifted). The planner allocates a
/// <c>BuiltinResume</c> cursor per such site; the emit threads the chunk-218
/// <c>BuiltinReturnPc</c> marker (backtrackable) / chunk-182 <c>Cp</c> marker
/// (meta-call) with the REGION's fid+cursor, so a backtrack or a callee's
/// proceed re-enters the region method at the post-site label. Every test
/// forces Tier-1 promotion (Threshold=1) so the region path actually runs;
/// regions are default-ON since chunk 418.
/// </summary>
public class Chunk424Tests
{
    private static PrologEngine MakeT1(string src)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(src);
        return e;
    }

    [Fact]
    public void BetweenInsideMember_EnumeratesViaRegionResume()
    {
        // gen/1 (a member) holds between/3: the builtin's CP resume must
        // re-enter the REGION method at the BuiltinResume cursor.
        var e = MakeT1(
            "go(X) :- mid(X).\n" +
            "mid(X) :- gen(X), check(X).\n" +
            "gen(X) :- between(1, 3, X).\n" +
            "check(_).\n");
        for (int warm = 0; warm < 2; warm++)
        {
            var all = e.QueryAll("go(X).").ToList();
            Assert.Equal(new[] { "1", "2", "3" },
                all.Select(s => s["X"]!.ToString()).ToArray());
        }
    }

    [Fact]
    public void MemberChoicePoints_ComposeWithBuiltinResume()
    {
        // A chain member's clause CPs (pick) interleave with a builtin's
        // CPs (between in bw): full cross product in standard order.
        var e = MakeT1(
            "s(R) :- pick(P), bw(X), mk(P, X, R).\n" +
            "pick(a).\npick(b).\n" +
            "bw(X) :- between(1, 2, X).\n" +
            "mk(P, X, P-X).\n");
        var all = e.QueryAll("s(R).").Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "-(a, 1)", "-(a, 2)", "-(b, 1)", "-(b, 2)" },
            all.ToArray());
    }

    [Fact]
    public void RetractInsideMember_EnumeratesAndMutates()
    {
        // grab/1 (a member) holds retract/1 — backtrackable AND mutating.
        var e = MakeT1(
            ":- dynamic d/1.\n" +
            "take(X) :- grab(X).\n" +
            "grab(X) :- retract(d(X)).\n");
        Assert.True(e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).").Success);
        var all = e.QueryAll("take(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "2", "3" }, all.ToArray());
        Assert.False(e.Query("d(_).").Success);   // all consumed
    }

    [Fact]
    public void AppendSplitInsideMember_Backtracks()
    {
        var e = MakeT1(
            "splits(A-B) :- sp(A, B).\n" +
            "sp(A, B) :- append(A, B, [1, 2]).\n");
        var all = e.QueryAll("splits(R).").ToList();
        Assert.Equal(3, all.Count);   // []-[1,2], [1]-[2], [1,2]-[]
    }

    [Fact]
    public void MetaCallInsideMember_NonTail()
    {
        // wrap/2 (a member) meta-calls a runtime goal NON-tail (post/1
        // follows), with the called predicate leaving choice points.
        var e = MakeT1(
            "run(G, R) :- wrap(G, R).\n" +
            "wrap(G, R) :- call(G, X), post(X, R).\n" +
            "post(X, X).\n" +
            "alt(1).\nalt(2).\n");
        var all = e.QueryAll("run(alt, R).").Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "2" }, all.ToArray());
    }

    [Fact]
    public void MetaCallInsideMember_Tail()
    {
        // wrapt/1's call/1 is the LAST goal: the tail path leaves Cp alone
        // (the called goal's proceed returns to the region's caller).
        var e = MakeT1(
            "runt(G) :- wrapt(G).\n" +
            "wrapt(G) :- call(G).\n" +
            "okp.\n");
        Assert.True(e.Query("runt(okp).").Success);
        Assert.False(e.Query("runt(fail).").Success);
        // Backtrackable goal through the tail meta-call:
        var e2 = MakeT1(
            "runt(G, X) :- wrapt(G, X).\n" +
            "wrapt(G, X) :- call(G, X).\n" +
            "alt(a).\nalt(b).\n");
        var all = e2.QueryAll("runt(alt, X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "a", "b" }, all.ToArray());
    }

    [Fact]
    public void MixedBody_CallsAndBuiltinResumeInterleave()
    {
        // One member body holds: an intra-region call, between/3, and another
        // intra-region call — the plan's cursor map must key each site by pc.
        var e = MakeT1(
            "top(Y) :- m(2, Y).\n" +
            "m(X, Y) :- pre(X), between(1, X, Y), post(Y).\n" +
            "pre(_).\n" +
            "post(_).\n");
        var all = e.QueryAll("top(Y).").Select(s => s["Y"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "2" }, all.ToArray());
    }

    [Fact]
    public void CutAfterBacktrackableMember_PrunesBuiltinCp()
    {
        // once-like commit: the cut after the member call must prune the
        // builtin's CP inside the region (chunk-367 barrier scoping).
        var e = MakeT1(
            "first(X) :- gen(X), !.\n" +
            "gen(X) :- between(1, 5, X).\n");
        var all = e.QueryAll("first(X).").ToList();
        Assert.Single(all);
        Assert.Equal("1", all[0]["X"]!.ToString());
    }

    [Fact]
    public void CallerChoicePoint_SurvivesMemberBuiltinEnumeration()
    {
        // The discriminating soundness case (extra-backtracking-not-sound):
        // a CP created BEFORE entering the region must survive the member's
        // builtin enumeration and cut.
        var e = MakeT1(
            "outer(S-X) :- seed(S), first(X).\n" +
            "seed(s1).\nseed(s2).\n" +
            "first(X) :- gen(X), !.\n" +
            "gen(X) :- between(1, 3, X).\n");
        var all = e.QueryAll("outer(R).").Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "-(s1, 1)", "-(s2, 1)" }, all.ToArray());
    }
}
