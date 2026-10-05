using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A cut that discards a setup_call_cleanup/3 scope runs the cleanup
/// before the goal after the cut, in the wasm tier as in the interpreter and
/// in Tier-1 IL (CompiledCutCleanupTests): the cleanup's bindings and effects
/// are that goal's to see.</summary>
public sealed class CutRunsTheCleanupTests(ITestOutputHelper o)
{
    // scope_cut/1: the cleanup's binding is read by the goal after the cut.
    // then_branch/1: the cut is an if-then-else's. seen/1: the cleanup's
    // effect is read by the next goal.
    private const string Corpus = """
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        scope_cut(R) :- setup_call_cleanup(true, (mem(X, [1, 2, 3]), X > 1), Done = yes), !, R = X-Done.
        then_branch(R) :- ( setup_call_cleanup(true, mem(X, [1, 2]), Done = yes) -> R = X-Done ; R = none ).
        seen(R) :- b_setval(k, before), setup_call_cleanup(true, mem(_, [1, 2]), b_setval(k, cleaned)), !,
            b_getval(k, R).
        """;

    [Theory]
    [InlineData("scope_cut", "-(2, yes)")]
    [InlineData("then_branch", "-(1, yes)")]
    [InlineData("seen", "cleaned")]
    public void TheCleanupRunsAtTheCut(string goal, string expected)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Answer(plain, goal);
        // ANTI-VACUITY: the interpreter's answer is the one the cleanup makes.
        Assert.Equal(expected, p0);

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = "";
        for (int round = 0; round < 3; round++) t = Answer(tiered, goal);
        o.WriteLine($"{goal}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query($"{goal}(R).");
        if (!r.Success) return "failed";
        foreach (var b in r.Bindings) if (b.Key == "R") return b.Value.ToString()!;
        return "?";
    }
}
