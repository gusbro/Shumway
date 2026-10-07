using System.Linq;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The wasm tier's half of Shumway.Tests.Embedding's
/// DeepGoalBodyTests, with the same shapes: a meta-called body as deep as
/// the program makes it, run on a small stack (the browser's is), must not
/// take a C# frame per level in any walk of the call/N boundary.</summary>
public sealed class DeepGoalBodyTierTests(ITestOutputHelper o)
{
    private const int Depth = 2000;
    private const int SmallStack = 256 * 1024;

    private const string Corpus = """
        :- use_module(library(coroutining)).
        :- public run/1.
        run(G) :- call(G).
        pad(0, G, G) :- !.
        pad(N, G, (B, true)) :- N1 is N - 1, pad(N1, G, B).
        rconj(1, G, G) :- !.
        rconj(N, G, (G, B)) :- N1 is N - 1, rconj(N1, G, B).
        thens(0, G, G) :- !.
        thens(N, G, (true -> T)) :- N1 is N - 1, thens(N1, G, T).
        frozen_n(0, _) :- !.
        frozen_n(N, X) :- freeze(X, bump), N1 is N - 1, frozen_n(N1, X).
        bump :- nb_getval(bumps, C0), C is C0 + 1, nb_setval(bumps, C).
        """;

    public static TheoryData<string, string> Shapes() => new()
    {
        { $"pad({Depth}, true, B), run(B).",
          "a left-nested conjunction" },
        { $"rconj({Depth}, true, B), run(B).",
          "a right-nested conjunction" },
        { $"pad({Depth}, (member(Y, [1,2,3]), X, Y >= 2), B), run((X = !, B)), Y == 2.",
          "a variable deep in the body converts to call(V): its ! cuts only itself" },
        { $"pad({Depth}, (member(Y, [1,2,3]), X, Y >= 2), B), X = !, \\+ run(B).",
          "a variable bound before the call is the goal it holds: its ! cuts the body" },
        { $"thens({Depth}, X = 1, T), run(user:T), X == 1.",
          "a module distributed into nested if-then-elses" },
        { $"nb_setval(bumps, 0), frozen_n({Depth}, X), X = 1, nb_getval(bumps, {Depth}).",
          "a variable with that many frozen goals is bound" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OnTheTier(string query, string what)
    {
        var (e, members) = TieredEngine.Build(Corpus);
        o.WriteLine(what);
        Assert.True(e.Query("run(true).").Success);
        int run = FunctorTable.Intern(AtomTable.Intern("run").Id, 1);
        Assert.True(members.Any(m => m.Predicate.FunctorId == run),
            "run/1 did not promote: this would be Tier-0 again");

        bool ok = false;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { ok = e.Query(query).Success; }
            catch (Exception ex) { error = ex; }
        }, SmallStack);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.True(ok, query);
    }
}
