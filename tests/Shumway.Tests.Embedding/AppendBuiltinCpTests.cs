using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>append/3</c>'s split modes push a builtin choice point, and the Tier-1 IL
/// emit has to set up the resume marker for a call that can do that. When it
/// does not, the cursor resumes at the wrong address and the engine executes
/// something that was never a goal.
/// </summary>
public class AppendBuiltinCpTests
{
    private const string Program = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).

        mk(0, []) :- !.
        mk(N, [N|T]) :- N1 is N - 1, mk(N1, T).

        % The goal arrives as a VARIABLE, so findall/3 is reached by the
        % runtime meta-call rather than rewritten at compile time. The
        % failure-driven loop puts it inside a synthesised disjunction helper,
        % which is a predicate of its own and gets promoted like any other.
        loop(G, R) :- ( between(1, R, _), \+ \+ call(G), fail ; true ).
        """;

    private static PrologEngine Promoting(int threshold = 32)
    {
        var e = new PrologEngine();
        // Promotion is what exposes it: on Tier-0 alone the enumeration is
        // correct however long it runs. 32 is the top level's own threshold.
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Program);
        return e;
    }

    [Theory]
    [InlineData(300, 60)]
    [InlineData(1000, 60)]
    [InlineData(100, 200)]
    public void SplitEnumerationSurvivesPromotion(int n, int rounds)
    {
        var e = Promoting();
        Assert.True(e.Query($"mk({n}, L), loop(findall(t, append(_, _, L), _), {rounds}).").Success);
    }

    [Fact]
    public void EveryRoundSeesEverySplit()
    {
        // Not just "no error": a resume that lands wrong can also lose
        // solutions silently, which is the failure mode the IL emit's
        // backtrackable-builtin handling exists to prevent.
        var e = Promoting();
        var sol = e.Query(
            "mk(40, L), findall(N, ( between(1, 60, _), "
            + "findall(t, append(_, _, L), Ts), length(Ts, N) ), Ns), "
            + "sort(Ns, [41]).");
        Assert.True(sol.Success);
    }

    [Theory]
    // Every backtrackable builtin reached through the meta-call is exposed the
    // same way, so each one that enumerates is worth a round.
    [InlineData("atom_concat(_, _, abcdefghijklmnop)", 17)]
    [InlineData("sub_atom(abcdefgh, _, 2, _, _)", 7)]
    [InlineData("between(1, 40, _)", 40)]
    [InlineData("nth0(_, [a,b,c,d,e,f,g], _)", 7)]
    public void OtherBacktrackableBuiltinsEnumerateWholeUnderPromotion(string goal, int count)
    {
        var e = Promoting();
        var sol = e.Query(
            $"loop(( findall(t, {goal}, Ts), length(Ts, {count}) ), 200).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void TheOpenModeSurvivesPromotionToo()
    {
        // append(-, +, -): the unbounded cursor, cut off by once/1.
        var e = Promoting();
        Assert.True(e.Query(
            "loop(( once(append(X, [a], _)), X == [] ), 60).").Success);
    }
}
