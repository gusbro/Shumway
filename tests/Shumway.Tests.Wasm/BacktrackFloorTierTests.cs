using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A sub-goal the engine runs in a nested driver (a wakeup drained
/// before a cut commits, a SolveOnce) owns the choice points above the B it
/// started at and none below: <see cref="Activation.BacktrackFloor"/>. A
/// promoted predicate that fails inside it must hand the failure to the host
/// at the floor. Resuming the outer computation's choice point in-chain runs
/// the rest of the outer clause inside the wakeup: under once/1 that lost the
/// environment, answered false, or nested one drain per retried choice until
/// the stack ran out. Tier-0 is the oracle.</summary>
public sealed class BacktrackFloorTierTests(ITestOutputHelper o)
{
    internal const string Corpus = """
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

    /// <summary>(query, the predicates whose promotion makes it bite).</summary>
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
    public void TierAgreesWithTier0(string query, string needed)
    {
        var t0 = new PrologEngine();
        t0.IlPromotion.Threshold = 0;
        t0.ConsultString(Corpus);
        string oracle = Run(t0, query);
        Assert.Equal("true", oracle);

        // Lazy promotion, as the page runs: the second dispatch promotes, so
        // the later runs are the ones on the tier.
        var (e, members, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 2);
        var answers = new List<string>();
        for (int i = 0; i < 3; i++) answers.Add(Run(e, query));
        o.WriteLine($"{query} tier: {string.Join(", ", answers)}");

        var promoted = members.Select(m => FunctorTable.Lookup(m.Predicate.FunctorId))
            .Select(f => $"{AtomTable.GetById(f.AtomId)?.Name}/{f.Arity}").ToHashSet();
        foreach (var p in needed.Split(' '))
            Assert.True(promoted.Contains(p), $"{p} did not promote: this would be Tier-0 again");
        Assert.All(answers, a => Assert.Equal(oracle, a));
    }
}
