using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>The IL tier's half of Shumway.Tests.Wasm's BacktrackFloorTierTests,
/// with the same shapes: a promoted predicate that fails inside a nested
/// driver's sub-goal (a wakeup drained before once/1's cut commits) hands the
/// failure back at <see cref="Activation.BacktrackFloor"/> and never resumes
/// the outer computation's choice point. Tier-0 is the oracle.</summary>
public sealed class BacktrackFloorIlTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(coroutining)).
        :- public ok/1.
        :- public gen/1.
        :- public sl1/2.
        :- public len/2.
        :- public same_length1/2.
        w(X) :- freeze(X, ok(X)).
        ok(a).
        gen(b).
        gen(a).
        t1 :- w(X), gen(X).
        sl(X, Y) :- freeze(X, sl1(X, Y)).
        sl1([], []).
        sl1([_|X], [_|Y]) :- sl(X, Y).
        len([], 0).
        len([_|T], N) :- len(T, N0), N is N0 + 1.
        t2(N) :- numlist(1, N, Z), sl(Y, Z), len(Y, _).
        same_length(X, Y) :- when((nonvar(X), nonvar(Y)), same_length1(X, Y)).
        same_length1([], []).
        same_length1([_|X], [_|Y]) :- same_length(X, Y).
        t3(N) :- numlist(1, N, Z), same_length(Y, Z), length(Y, _).
        """;

    public static TheoryData<string, string> Shapes() => new()
    {
        { "once(t1).", "ok/1 gen/1" },
        { "once(t2(3)).", "sl1/2 len/2" },
        { "once(t3(5)).", "same_length1/2" },
    };

    private static string Run(PrologEngine e, string query)
    {
        try { return e.Query(query).Success ? "true" : "false"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void IlAgreesWithTier0(string query, string needed)
    {
        var t0 = new PrologEngine();
        t0.IlPromotion.Threshold = 0;
        t0.ConsultString(Corpus);
        string oracle = Run(t0, query);
        Assert.Equal("true", oracle);

        var il = new PrologEngine();
        il.IlPromotion.Threshold = 2;
        il.ConsultString(Corpus);
        var answers = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            answers.Add(Run(il, query));
            Assert.True(il.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        o.WriteLine($"{query} il: {string.Join(", ", answers)}");

        foreach (var p in needed.Split(' '))
        {
            var (name, arity) = (p[..p.IndexOf('/')], int.Parse(p[(p.IndexOf('/') + 1)..]));
            int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
            Assert.True(il.IlPromotion.IsPromoted(fid), $"{p} did not promote: this would be Tier-0 again");
        }
        Assert.All(answers, a => Assert.Equal(oracle, a));
    }
}
