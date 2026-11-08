using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-057 amendment: a multi-clause predicate with no local
/// closure compiles as a region of one member, so its clause alternatives
/// take the region's choice-point model (ADR-058 inline push and restore,
/// ADR-060 held registers) instead of the helper push, the IL side stack and
/// the interpreter's resume. A single-clause root alone stays a method. The
/// region of one answers as Tier-0 does over backtracking, cut, exceptions,
/// findall, wakeups, a collection mid-enumeration and deep recursion.</summary>
// Reads the process-wide region emission log and toggles a compiler static:
// it runs alone, in the gate's exclusive pass.
[Collection("exclusive")]
public sealed class OneMemberRegionTests
{
    // Each engine names its predicates with a prefix of its own: the
    // emission log is process-wide and keyed by functor.
    private static string Program(string p) => $@"
:- use_module(library(coroutining)).
:- public {p}_mem/2.
:- public {p}_color/1.
:- public {p}_pair/2.
:- public {p}_memchk/2.
:- public {p}_cnt/2.
:- public {p}_walk/1.
:- public {p}_thrower/1.
:- public {p}_single/2.
:- public {p}_loop/1.
:- public {p}_find/2.
{p}_mem(X, [X|_]).
{p}_mem(X, [_|T]) :- {p}_mem(X, T).
{p}_color(red). {p}_color(green). {p}_color(blue).
{p}_pair(a, 1). {p}_pair(a, 2). {p}_pair(b, 3).
{p}_memchk(X, L) :- {p}_mem(X, L), !.
{p}_cnt([], 0).
{p}_cnt([_|T], N) :- {p}_cnt(T, M), N is M + 1.
{p}_walk([]).
{p}_walk([_|T]) :- {p}_walk(T).
{p}_thrower(1) :- throw({p}_one).
{p}_thrower(2).
{p}_thrower(3).
{p}_single(X, f(X)).
{p}_loop(0) :- !.
{p}_loop(N) :- {p}_find(X, [1, 2, 3]), X == 3, N1 is N - 1, {p}_loop(N1).
{p}_find(X, [X|_]) :- X > 2.
{p}_find(X, [_|T]) :- {p}_find(X, T).
";

    private static PrologEngine Engine(string prefix, int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program(prefix));
        return e;
    }

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    private static bool EmittedARegionOfOneRootedAt(int fid)
        => IlRegionCost.Emissions.Any(e => e.RootFid == fid && e.Members == 1);

    private static string Answers(PrologEngine e, string goal)
    {
        var r = e.Query($"findall(A, ({goal}), As).");
        Assert.True(r.Success, goal);
        return r["As"]!.ToString()!;
    }

    [Fact]
    public void AMultiClausePredicateAloneCompilesAsARegionOfOne()
    {
        var e = Engine("omra", 1);
        Assert.True(e.Query("omra_mem(2, [1, 2, 3]).").Success);
        Assert.True(e.Query("omra_color(green).").Success);
        Assert.True(e.Query("omra_pair(a, 2).").Success);
        Assert.True(e.Query("omra_single(1, _).").Success);
        foreach (var (name, arity) in new[] { ("omra_mem", 2), ("omra_color", 1), ("omra_pair", 2), ("omra_single", 2) })
            Assert.True(e.IlPromotion.IsPromoted(Fid(name, arity)), name);
        // A self-recursive chain and an indexed table: each a region of one.
        Assert.True(EmittedARegionOfOneRootedAt(Fid("omra_mem", 2)));
        Assert.True(EmittedARegionOfOneRootedAt(Fid("omra_pair", 2)));
        // An atom fact table is the IndexedAtomPredicate shape, which the
        // region emit does not take as a member: a method, as before.
        Assert.DoesNotContain(IlRegionCost.Emissions, em => em.RootFid == Fid("omra_color", 1));
        // A single-clause root alone stays a method: no region emission at all.
        Assert.DoesNotContain(IlRegionCost.Emissions, em => em.RootFid == Fid("omra_single", 2));
    }

    [Fact]
    public void WithTheLeverOffARootAloneStaysAMethod()
    {
        bool saved = IlPredicateCompiler.OneMemberRegions;
        IlPredicateCompiler.OneMemberRegions = false;
        try
        {
            var e = Engine("omrb", 1);
            Assert.True(e.Query("omrb_mem(2, [1, 2, 3]).").Success);
            Assert.True(e.IlPromotion.IsPromoted(Fid("omrb_mem", 2)));
            Assert.DoesNotContain(IlRegionCost.Emissions, em => em.RootFid == Fid("omrb_mem", 2));
            Assert.Equal(Answers(Engine("omrb", 0), "omrb_mem(A, [1, 2, 3])"), Answers(e, "omrb_mem(A, [1, 2, 3])"));
        }
        finally { IlPredicateCompiler.OneMemberRegions = saved; }
    }

    public static TheoryData<string> Goals() => new()
    {
        "omrc_mem(A, [1, 2, 3])",
        "omrc_color(A)",
        "omrc_pair(a, A)",
        "omrc_pair(K, V), A = K-V",
        "omrc_pair(b, A)",
        "( omrc_memchk(2, [1, 2, 3]) -> A = yes ; A = no )",
        "( omrc_memchk(9, [1, 2, 3]) -> A = yes ; A = no )",
        "( \\+ omrc_mem(4, [1, 2, 3]) -> A = absent ; A = present )",
        "omrc_mem(X, [1, 2]), omrc_mem(Y, [a, b]), A = X-Y",
        "findall(X-Y, (omrc_mem(X, [1, 2]), omrc_mem(Y, [a, b])), A)",
        "catch(omrc_thrower(A), E, A = caught(E))",
        "catch((omrc_thrower(T), T > 1, A = T), E, A = caught(E))",
        "catch((omrc_mem(X, [1, 2, 3]), X >= 2, throw(found(X))), found(A), true)",
        "freeze(X, Y = seen(X)), omrc_mem(X, [1, 2, 3]), A = X-Y",
        "dif(X, 2), omrc_mem(X, [1, 2, 3]), A = X",
        "freeze(X, X > 1), omrc_mem(X, [1, 2, 3]), A = X",
        "numlist(1, 30000, L), omrc_mem(X, L), X mod 10000 =:= 0, garbage_collect, A = X",
        "omrc_color(C), garbage_collect, numlist(1, 5000, L), length(L, N), A = C-N",
        "omrc_loop(2000), A = done",
        "omrc_find(A, [1, 2, 3, 4])",
    };

    [Theory]
    [MemberData(nameof(Goals))]
    public void TheRegionOfOneAnswersAsTierZeroDoes(string goal)
    {
        var tier0 = Engine("omrc", 0);
        var tier1 = Engine("omrc", 1);
        // Every predicate compiles at its first call: the oracle run comes
        // after a run on the same engine that promoted them.
        Answers(tier1, goal);
        // Whatever the goal called among the program's multi-clause
        // predicates became a region of one (loop/1 owns find/2: a region of two).
        var called = new[] { ("omrc_mem", 2), ("omrc_pair", 2), ("omrc_thrower", 1), ("omrc_find", 2) }
            .Where(f => goal.Contains(f.Item1 + "(", System.StringComparison.Ordinal))
            .Select(f => Fid(f.Item1, f.Item2)).ToHashSet();
        if (called.Count > 0)
            Assert.Contains(IlRegionCost.Emissions, em => em.Members == 1 && called.Contains(em.RootFid));
        Assert.Equal(Answers(tier0, goal), Answers(tier1, goal));
    }

    [Fact]
    public void DeepRecursionInARegionOfOneKeepsTheHostStackFlat()
    {
        var e = Engine("omrd", 1);
        Assert.True(e.Query("omrd_cnt([a], 1), omrd_walk([a, b]).").Success);
        Assert.True(EmittedARegionOfOneRootedAt(Fid("omrd_cnt", 2)));
        Assert.True(EmittedARegionOfOneRootedAt(Fid("omrd_walk", 1)));
        // A million frames of non-tail recursion, and a million iterations of
        // tail recursion: intra-region branches, not host calls.
        var r = e.Query("numlist(1, 1000000, L), omrd_cnt(L, N), omrd_walk(L).");
        Assert.True(r.Success);
        Assert.Equal("1000000", r["N"]!.ToString());
    }
}
