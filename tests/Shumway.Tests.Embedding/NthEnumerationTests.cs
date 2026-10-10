using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>nth0/3 and nth1/3 with a variable index enumerate the positions on
/// backtracking. Each step goes on from where the previous one stopped, so
/// reaching position n costs n steps; walking from the list's start each time
/// cost n²/2. Each query runs against a deadline far above the linear cost and
/// far below the quadratic one.</summary>
public sealed class NthEnumerationTests
{
    private static bool Holds(PrologEngine e, string query)
    {
        bool? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = e.Query(query).Success; }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), $"did not terminate in time: {query}");
        if (error is not null) throw new Xunit.Sdk.XunitException($"{query} raised {error}");
        return result!.Value;
    }

    [Theory]
    [InlineData("numlist(1, 300000, L), nth1(I, L, 300000), I == 300000.")]
    [InlineData("numlist(0, 299999, L), nth0(I, L, 299999), I == 299999.")]
    // A partial list grows a cell per position.
    [InlineData("nth0(I, L, _), I == 300000, !, '$skip_list'(N, L, T), N == 300001, var(T).")]
    // A cyclic list has a position for every index.
    [InlineData("L = [a, b|L], nth0(I, L, b), I > 300000, !, I == 300001.")]
    public void ReachingPositionNTakesNSteps(string query)
        => Assert.True(Holds(new PrologEngine(), query), query);

    [Fact]
    public void TheRestOfTheListSurvivesACollectionBetweenRetries()
    {
        // The garbage below the list makes the collection slide the list's
        // cells down, the rest-of-list cell the choice point holds with them.
        // A collection per retry: a few hundred retries, or a loaded machine
        // runs past the deadline.
        var e = new PrologEngine();
        e.ConsultString("junk :- numlist(1, 20000, _).");
        Assert.True(Holds(e,
            "junk, numlist(1, 200, L), nth1(I, L, E), garbage_collect, E == 150, I == 150."));
        Assert.True(Holds(e,
            "junk, numlist(1, 200, L), findall(I-E, (nth0(I, L, E), garbage_collect, E mod 50 =:= 0), Ps), "
            + "Ps == [49-50, 99-100, 149-150, 199-200]."));
    }

    [Theory]
    // An attributed index is a variable: each position binds it and its
    // constraints decide, as in SICStus.
    [InlineData("X in 0..1, findall(X-E, nth0(X, [a, b, c], E), Ps), Ps == [0-a, 1-b].")]
    [InlineData("X #> 1, findall(X-E, nth1(X, [a, b, c], E), Ps), Ps == [2-b, 3-c].")]
    public void AnAttributedIndexEnumerates(string query)
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        Assert.True(Holds(e, query), query);
    }
}
