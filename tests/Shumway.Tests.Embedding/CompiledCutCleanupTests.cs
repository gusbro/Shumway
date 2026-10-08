#if !NETFRAMEWORK
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A cut that discards a setup_call_cleanup/3 scope runs the cleanup
/// before the goal after the cut. Bytecode does it after each cut
/// instruction; compiled code left the cleanup queued until the interpreter
/// next ran a goal, or until the query ended, where it ran on a copy and its
/// bindings were lost. Regions and continuation methods, against the
/// interpreter.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CompiledCutCleanupTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // scope_cut/1: the cleanup's binding is read by the goal after the cut.
    // then_branch/1: the cut is an if-then-else's. caught/1: the cleanup
    // throws. seen/1: the cleanup's effect is read by the next goal, with
    // no call between the cut and it.
    private const string Program = """
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        scope_cut(R) :- setup_call_cleanup(true, (mem(X, [1, 2, 3]), X > 1), Done = yes), !, R = X-Done.
        then_branch(R) :- ( setup_call_cleanup(true, mem(X, [1, 2]), Done = yes) -> R = X-Done ; R = none ).
        caught(R) :- catch(thrower(R), oops, R = caught).
        thrower(R) :- setup_call_cleanup(true, mem(_, [1, 2]), throw(oops)), !, R = not_thrown.
        seen(R) :- b_setval(k, before), setup_call_cleanup(true, mem(_, [1, 2]), b_setval(k, cleaned)), !,
            b_getval(k, R).
        runs(0) :- !.
        runs(N) :- scope_cut(_), then_branch(_), caught(_), seen(_), M is N - 1, runs(M).
        """;

    private static readonly (string Name, int Arity)[] Compiled =
    {
        ("scope_cut", 1), ("then_branch", 1), ("caught", 1), ("thrower", 1), ("seen", 1),
    };

    private static readonly string[] Goals =
    {
        "scope_cut(R).", "then_branch(R).", "caught(R).", "seen(R).",
        "findall(A-B, (scope_cut(A), then_branch(B)), R).",
    };

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern("user$" + n, permanent: true).Id, a);

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.UseCoroutining();   // user's predicates carry their module's prefix
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Program);
        return e;
    }

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCleanupRunsAtTheCut(bool continuationMethods)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        var plain = Engine(0);
        var e = Engine(1);
        for (int round = 0; round < 4; round++)
        {
            Assert.True(e.Query("runs(40).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: the predicates that cut are compiled, in the form asked for.
        foreach (var (n, a) in Compiled)
        {
            Assert.True(e.IlPromotion.IsPromoted(Fid(n, a)), $"{n}/{a} is not compiled");
            Assert.Equal(continuationMethods, e.IlPromotion.TryGetCps(Fid(n, a)) is not null);
        }
        // ANTI-VACUITY: the interpreter's answers are the ones the cleanups make.
        Assert.Equal("R = -(2, yes)", Answer(plain, "scope_cut(R)."));
        Assert.Equal("R = cleaned", Answer(plain, "seen(R)."));
        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(e, g));
    }
}
#endif
