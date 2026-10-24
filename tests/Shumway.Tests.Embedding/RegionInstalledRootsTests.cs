using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A callee that has compiled code of its own when a caller is
/// promoted is called from the caller's region, not copied into it, unless it
/// is small (<see cref="IlRegionCost.ReinlineLimit"/>). Sets the limit, a
/// process-wide static, so it runs alone.</summary>
[Collection("exclusive")]
public sealed class RegionInstalledRootsTests
{
    // Each test names its predicates with a prefix of its own: the region
    // emission log is process-wide and keyed by functor.
    private static string Program(string p) => $@"
:- public {p}_loop/2.
:- public {p}_loop2/2.
:- public {p}_big/2.
:- public {p}_small/2.
{p}_loop(0, []) :- !.
{p}_loop(N, [Y|Ys]) :- {p}_big(N, Y), N1 is N - 1, {p}_loop(N1, Ys).
{p}_loop2(0, []) :- !.
{p}_loop2(N, [Y|Ys]) :- {p}_small(N, Y), N1 is N - 1, {p}_loop2(N1, Ys).
:- public {p}_loop3/2.
{p}_loop3(0, []) :- !.
{p}_loop3(N, [Y|Ys]) :- {p}_big(N, Y), N1 is N - 1, {p}_loop3(N1, Ys).
{p}_small(N, s(N)).
{p}_big(N, p(N, A, B)) :- A is N * 2, B is A + 1.
{p}_big(N, q(N, A, B, C)) :- A is N + 3, B is A * A, C is B - N.
{p}_big(N, r(f(N), g(N, N), h(N, N, N))) :- N > 0.
{p}_big(N, t(N, u(N), v(N, N))) :- N < 0.
";

    // Synchronous, threshold 1: a predicate compiles at its first call, so
    // the order of the first calls is the order of the promotions.
    private static PrologEngine Engine(string prefix, int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program(prefix));
        return e;
    }

    // A chain: outer calls mid, mid calls big, and nothing else calls them,
    // unless another caller of big is asked for (never called itself).
    private static string Chain(string p, bool anotherCallerOfBig) => $@"
:- public {p}_outer/2.
:- public {p}_mid/2.
:- public {p}_big/2.
:- public {p}_other/2.
{p}_outer(0, []) :- !.
{p}_outer(N, [Y|Ys]) :- {p}_mid(N, Y), N1 is N - 1, {p}_outer(N1, Ys).
{p}_mid(N, m(Y, Z)) :- {p}_big(N, Y), Z is N + 1.
{p}_big(N, p(N, A, B)) :- A is N * 2, B is A + 1.
{p}_big(N, q(N, A, B, C)) :- A is N + 3, B is A * A, C is B - N.
{p}_big(N, r(f(N), g(N, N), h(N, N, N))) :- N > 0.
{p}_big(N, t(N, u(N), v(N, N))) :- N < 0.
" + (anotherCallerOfBig ? $"{p}_other(N, Y) :- {p}_big(N, Y).\n" : $"{p}_other(_, none).\n");

    // A diamond: top calls a and b, both call big, and nothing else does.
    private static string Diamond(string p) => $@"
:- public {p}_top/2.
:- public {p}_big/2.
{p}_top(0, []) :- !.
{p}_top(N, [Y, Z|Ys]) :- {p}_a(N, Y), {p}_b(N, Z), N1 is N - 1, {p}_top(N1, Ys).
{p}_a(N, a(Y)) :- {p}_big(N, Y).
{p}_b(N, b(Y)) :- {p}_big(N, Y).
{p}_big(N, p(N, A, B)) :- A is N * 2, B is A + 1.
{p}_big(N, q(N, A, B, C)) :- A is N + 3, B is A * A, C is B - N.
{p}_big(N, r(f(N), g(N, N), h(N, N, N))) :- N > 0.
{p}_big(N, t(N, u(N), v(N, N))) :- N < 0.
";

    private static PrologEngine ChainEngine(string prefix, bool anotherCallerOfBig, int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Chain(prefix, anotherCallerOfBig));
        return e;
    }

    private static int MembersOfTheRegionRootedAt(int fid)
        => IlRegionCost.Emissions.Where(e => e.RootFid == fid).Select(e => e.Members).DefaultIfEmpty(0).Max();

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    private static bool EmittedARegionRootedAt(int fid) => RegionsRootedAt(fid) > 0;

    private static int RegionsRootedAt(int fid)
        => IlRegionCost.Emissions.Count(e => e.RootFid == fid && e.Members >= 2);

    // Six roots over the one big callee.
    private static string Burst(string p)
    {
        var sb = new System.Text.StringBuilder(Program(p));
        for (int i = 1; i <= 6; i++)
            sb.Append($@"
:- public {p}_r{i}/2.
{p}_r{i}(0, []) :- !.
{p}_r{i}(N, [Y|Ys]) :- {p}_big(N, Y), N1 is N - 1, {p}_r{i}(N1, Ys).
");
        return sb.ToString();
    }

    private static string Answers(PrologEngine e, string goal)
    {
        var r = e.Query($"findall(L, {goal}, Ls).");
        Assert.True(r.Success, goal);
        return r["Ls"]!.ToString()!;
    }

    private static void WithLimit(int limit, System.Action body)
    {
        int saved = IlRegionCost.ReinlineLimit;
        IlRegionCost.ReinlineLimit = limit;
        try { body(); }
        finally { IlRegionCost.ReinlineLimit = saved; }
    }

    [Fact]
    public void ACalleeWithCodeOfItsOwnIsCalledNotCopied() => WithLimit(0, () =>
    {
        var e = Engine("ria", 1);
        Assert.True(e.Query("ria_big(1, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("ria_big", 2)));
        Assert.True(e.Query("ria_loop(5, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("ria_loop", 2)));
        // The loop's only callee had code, so the loop became a method of its
        // own, no region; the callee keeps its own code.
        Assert.False(EmittedARegionRootedAt(Fid("ria_loop", 2)));
        Assert.Null(e.IlPromotion.RegionRootOf(Fid("ria_big", 2)));
        // Backtracking into the callee through the call gives Tier-0's answers.
        Assert.Equal(Answers(Engine("ria", 0), "ria_loop(3, L)"), Answers(e, "ria_loop(3, L)"));
    });

    [Fact]
    public void ASmallCalleeIsStillCopied() => WithLimit(150, () =>
    {
        var e = Engine("rib", 1);
        Assert.True(e.Query("rib_small(1, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("rib_small", 2)));
        Assert.True(e.Query("rib_loop2(5, _).").Success);
        Assert.True(EmittedARegionRootedAt(Fid("rib_loop2", 2)));
        Assert.Equal(Answers(Engine("rib", 0), "rib_loop2(3, L)"), Answers(e, "rib_loop2(3, L)"));
    });

    [Fact]
    public void WithNoLimitTheCalleeIsCopiedAsBefore() => WithLimit(int.MaxValue, () =>
    {
        var e = Engine("ric", 1);
        Assert.True(e.Query("ric_big(1, _).").Success);
        Assert.True(e.Query("ric_loop(5, _).").Success);
        Assert.True(EmittedARegionRootedAt(Fid("ric_loop", 2)));
    });

    [Fact]
    public void AMemberAliasedToAnEarlierRegionIsCalledNotCopied() => WithLimit(0, () =>
    {
        var e = Engine("rie", 1);
        Assert.True(e.Query("rie_loop(5, _).").Success);
        Assert.True(EmittedARegionRootedAt(Fid("rie_loop", 2)));
        Assert.Equal(Fid("rie_loop", 2), e.IlPromotion.RegionRootOf(Fid("rie_big", 2)));
        // A later caller of the member calls the alias; the member stays the
        // first region's.
        Assert.True(e.Query("rie_loop3(5, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("rie_loop3", 2)));
        Assert.False(EmittedARegionRootedAt(Fid("rie_loop3", 2)));
        Assert.Equal(Fid("rie_loop", 2), e.IlPromotion.RegionRootOf(Fid("rie_big", 2)));
        Assert.Equal(Answers(Engine("rie", 0), "rie_loop3(3, L)"), Answers(e, "rie_loop3(3, L)"));
    });

    [Fact]
    public void RootsQueuedTogetherCopyACalleeOnce() => WithLimit(0, () =>
    {
        // Background compilation: the six roots queue before any is installed.
        // The worker compiles them in order; from the second on, the callee
        // has code coming (the first root's member, or its own compile).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Burst("rig"));
        Assert.True(e.Query("rig_r1(2, _), rig_r2(2, _), rig_r3(2, _), rig_r4(2, _), rig_r5(2, _), rig_r6(2, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        var roots = Enumerable.Range(1, 6).Select(i => Fid($"rig_r{i}", 2)).ToHashSet();
        for (int i = 1; i <= 6; i++) Assert.True(e.IlPromotion.IsPromoted(Fid($"rig_r{i}", 2)), $"rig_r{i}");
        Assert.InRange(IlRegionCost.Emissions.Count(em => roots.Contains(em.RootFid) && em.Members >= 2), 0, 1);
        var tier0 = new PrologEngine();
        tier0.IlPromotion.Threshold = 0;
        tier0.ConsultString(Burst("rig"));
        Assert.Equal(Answers(tier0, "rig_r6(3, L)"), Answers(e, "rig_r6(3, L)"));
    });

    [Fact]
    public void ACalleeTheRegionOwnsIsCopiedAlthoughItHasCode() => WithLimit(0, () =>
    {
        // The leaf gets hot first, then its one caller, then that one's: each
        // region copies the chain below it, and the outer one holds it all.
        var e = ChainEngine("rih", anotherCallerOfBig: false, threshold: 1);
        Assert.True(e.Query("rih_big(1, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("rih_big", 2)));
        Assert.True(e.Query("rih_mid(1, _).").Success);
        Assert.Equal(2, MembersOfTheRegionRootedAt(Fid("rih_mid", 2)));
        Assert.True(e.Query("rih_outer(5, _).").Success);
        Assert.Equal(3, MembersOfTheRegionRootedAt(Fid("rih_outer", 2)));
        Assert.Equal(Answers(ChainEngine("rih", false, 0), "rih_outer(3, L)"), Answers(e, "rih_outer(3, L)"));
    });

    [Fact]
    public void ACalleeWithACallerOutsideTheRegionIsCalled() => WithLimit(0, () =>
    {
        // The same chain, with one more caller of the leaf that never runs:
        // the leaf is not the outer region's, the middle one still is.
        var e = ChainEngine("rii", anotherCallerOfBig: true, threshold: 1);
        Assert.True(e.Query("rii_big(1, _).").Success);
        Assert.True(e.Query("rii_mid(1, _).").Success);
        Assert.Equal(0, MembersOfTheRegionRootedAt(Fid("rii_mid", 2)));
        Assert.True(e.Query("rii_outer(5, _).").Success);
        Assert.Equal(2, MembersOfTheRegionRootedAt(Fid("rii_outer", 2)));
        Assert.Equal(Answers(ChainEngine("rii", true, 0), "rii_outer(3, L)"), Answers(e, "rii_outer(3, L)"));
    });

    [Fact]
    public void ACalleeReachedFromTwoMembersIsCopiedOnce() => WithLimit(0, () =>
    {
        // The leaf has code; the region reaches it from two members and owns
        // it through both. One block for it, or the emit marks its label twice.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Diamond("rij"));
        Assert.True(e.Query("rij_big(1, _).").Success);
        Assert.True(e.Query("rij_top(5, _).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("rij_top", 2)));
        Assert.Equal(4, MembersOfTheRegionRootedAt(Fid("rij_top", 2)));
        var tier0 = new PrologEngine();
        tier0.IlPromotion.Threshold = 0;
        tier0.ConsultString(Diamond("rij"));
        Assert.Equal(Answers(tier0, "rij_top(3, L)"), Answers(e, "rij_top(3, L)"));
    });

    [Fact]
    public void AMemberQueuedForCodeOfItsOwnRunsTheRegionInstead() => WithLimit(0, () =>
    {
        // Background compilation: the chain's three get hot in one query,
        // outer first. The worker emits outer's region holding the other two
        // and skips their own compiles; each enters the region at its block.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Chain("rik", anotherCallerOfBig: false));
        Assert.True(e.Query("rik_outer(50, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions());
        int outer = Fid("rik_outer", 2), mid = Fid("rik_mid", 2), big = Fid("rik_big", 2);
        Assert.True(e.IlPromotion.IsPromoted(outer));
        Assert.Equal(3, MembersOfTheRegionRootedAt(outer));
        Assert.Equal(outer, e.IlPromotion.RegionRootOf(mid));
        Assert.Equal(outer, e.IlPromotion.RegionRootOf(big));
        Assert.Equal(0, RegionsRootedAt(mid));
        Assert.Equal(Answers(ChainEngine("rik", false, 0), "rik_outer(3, L)"), Answers(e, "rik_outer(3, L)"));
    });

    [Fact]
    public void ACalleeChangedByAConsultIsSeenThroughTheCall() => WithLimit(0, () =>
    {
        var e = Engine("rid", 1);
        Assert.True(e.Query("rid_big(1, _).").Success);
        Assert.True(e.Query("rid_loop(5, _).").Success);
        const string more = ":- public rid_big/2.\nrid_big(N, z(N)).\n";
        e.ConsultString(more);
        var tier0 = Engine("rid", 0);
        tier0.ConsultString(more);
        Assert.Equal(Answers(tier0, "rid_loop(2, L)"), Answers(e, "rid_loop(2, L)"));
        Assert.Contains("z(", Answers(e, "rid_loop(1, L)"));
    });
}
