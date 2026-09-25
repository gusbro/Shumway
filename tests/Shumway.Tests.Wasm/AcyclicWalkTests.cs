using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>acyclic_term/1 walked by the module itself, the way ground/1
/// already is: the check clp(Z) makes on every expression it parses (3,000
/// exits on queens). A shared subterm is not a cycle; a term reaching one of
/// its own ancestors is; a packed string is the host's.</summary>
public sealed class AcyclicWalkTests(ITestOutputHelper o)
{
    private const string Corpus = """
        loop(0, _) :- !.
        loop(N, T) :- acyclic_term(T), N1 is N - 1, loop(N1, T).
        shared(N) :- S = f(A, B, [A|B]), T = g(S, S, h(S)), loop(N, T).
        attv(N) :- put_attr(X, m, 1), T = X + 3 * Y - f(Y, X), loop(N, T).
        cyc :- X = f(X), \+ acyclic_term(X).
        cyclist :- L = [a, b | L], \+ acyclic_term(L).
        deep :- X = f(g(h(Y))), Y = k(X), \+ acyclic_term(X).
        notcyc :- X = f(Y), Y = g(Z), Z = h(a), acyclic_term(X).
        loopc(0, _) :- !.
        loopc(N, T) :- \+ acyclic_term(T), N1 is N - 1, loopc(N1, T).
        cycles(N) :- X = f(a, g(X)), loopc(N, X).
        packed(N) :- atom_chars(abc, Cs), loop(N, f(Cs)).
        """;

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        foreach (string goal in new[] { "shared(20).", "attv(20).", "cyc.", "cyclist.", "deep.",
                                        "notcyc.", "cycles(20).", "packed(20)." })
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    private static long AcyclicExits()
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "acyclic_term" && a == 1) return hits;
        return 0;
    }

    /// <summary>The counters: the walk stays in the module for shared and
    /// attributed terms and for cyclic ones alike; a packed string leaves
    /// (the anti-vacuity: the site still exits when it must).</summary>
    [DiagFact]
    public void TheWalkStaysInTheModule()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        foreach (string goal in new[] { "shared(3).", "attv(3).", "cycles(3).", "packed(3)." })
            Assert.True(engine.Query(goal).Success, goal);

        void Check(string goal, string what, bool leaves)
        {
            WasmTierDelegate.ResetDiag();
            Assert.True(engine.Query(goal).Success, goal);
            o.WriteLine($"{what}: acyclic exits={AcyclicExits()} deopts={WasmTierDelegate.DiagDeopts}");
            if (leaves)
                Assert.True(AcyclicExits() >= 300, $"{what}: {AcyclicExits()} exits, the host was not asked");
            else
                Assert.True(AcyclicExits() == 0, $"{what}: {AcyclicExits()} exits, the module did not walk");
        }
        Check("shared(300).", "shared subterms", leaves: false);
        Check("attv(300).", "an attributed variable inside", leaves: false);
        Check("cycles(300).", "a cyclic term", leaves: false);
        Check("packed(300).", "a packed string", leaves: true);
    }
}
