using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>get_attr/3 answered inside the module, out of the attribute image.
///
/// <para>Answers alone prove NOTHING here, and that is the lesson this file
/// carries: when the type tests were first open-coded every test stayed green
/// while the modules kept exiting to the host for all of them, because a
/// builtin exit returns the same answer the inline path does. Only a COUNT
/// tells the two apart.</para>
///
/// <para>A count of zero is just as easily a program that never reached the
/// tier, so each corpus below calls atom_length/2 in the SAME clause as the
/// get_attr. atom_length is not open-coded, so it must show up in the tally --
/// that is the anti-vacuity guard, measured on the very clause under test
/// rather than somewhere else in the program.</para></summary>
public sealed class InlineGetAttrTests(ITestOutputHelper o)
{
    private const string Corpus = """
        walk(0, _, 0).
        walk(N, X, S) :- N > 0, atom_length(ab, _), get_attr(X, m, V),
                         N1 is N - 1, walk(N1, X, S1), S is S1 + V.
        miss(0, _, 0).
        miss(N, X, C) :- N > 0, atom_length(ab, _), N1 is N - 1,
                         miss(N1, X, C1),
                         ( get_attr(X, nosuch, _) -> C is C1 + 1 ; C = C1 ).
        """;

    private static long ExitsFor(string name, int arity)
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == name && a == arity) return hits;
        return 0;
    }

    private void Report(string what)
        => o.WriteLine($"{what}: get_attr/3={ExitsFor("get_attr", 3)} "
            + $"atom_length/2={ExitsFor("atom_length", 2)} "
            + $"entries={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");

    [DiagFact]
    public void AHitIsAnsweredWithoutLeavingTheModule()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();

        Assert.True(engine.Query("put_attr(X, m, 7), walk(50, X, S), S == 350.").Success,
            "the tiered answer is wrong");
        Report("hit");

        // The guard: the clause DID run on the tier, 50 times over.
        Assert.True(ExitsFor("atom_length", 2) >= 50,
            $"the clause never ran on the tier ({ExitsFor("atom_length", 2)} exits)");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
        Assert.Equal(0L, ExitsFor("get_attr", 3));
    }

    [DiagFact]
    public void AMissFailsInsideTheModuleToo()
    {
        var (engine, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();

        // All 50 lookups miss: the variable carries m, never nosuch. The miss
        // is what the image was BUILT for -- open-coding only the failing path
        // would have erased under a tenth of the exits, so a miss that still
        // left the module would make the whole design pointless.
        Assert.True(engine.Query("put_attr(X, m, 7), miss(50, X, C), C == 0.").Success,
            "a miss must fail the get_attr, not the program");
        Report("miss");

        Assert.True(ExitsFor("atom_length", 2) >= 50,
            $"the clause never ran on the tier ({ExitsFor("atom_length", 2)} exits)");
        Assert.Equal(0L, ExitsFor("get_attr", 3));
    }

    /// <summary>The shapes the module does not cover still reach the host, and
    /// the host still decides them.</summary>
    [DiagFact]
    public void ANonAttvarStillReachesTheHost()
    {
        var (engine, _) = TieredEngine.Build("""
            probe(0, _, _).
            probe(N, X, M) :- N > 0, atom_length(ab, _),
                              ( get_attr(X, M, _) -> true ; true ),
                              N1 is N - 1, probe(N1, X, M).
            """);
        WasmTierDelegate.ResetDiag();

        // A plain variable is not an attributed variable, so the inline path
        // declines and the host fails it.
        Assert.True(engine.Query("probe(20, _, m).").Success);
        Report("plain var");

        Assert.True(ExitsFor("atom_length", 2) >= 20, "never ran on the tier");
        Assert.True(ExitsFor("get_attr", 3) >= 20,
            "a non-attvar was answered inside the module");
    }

    /// <summary>An unbound module is an ERROR, not a failure, and the inline
    /// path must have no opinion about it: it declines and the host raises.
    /// </summary>
    [DiagFact]
    public void AnUnboundModuleIsStillTheHostsError()
    {
        var (engine, _) = TieredEngine.Build("""
            ask(0, _, _).
            ask(N, X, M) :- N > 0, atom_length(ab, _), get_attr(X, M, _),
                            N1 is N - 1, ask(N1, X, M).
            """);
        WasmTierDelegate.ResetDiag();

        var r = engine.Query(
            "put_attr(X, m, 1), catch(ask(5, X, _), E, true), nonvar(E).");
        Assert.True(r.Success, "the error never arrived");
        Report("unbound module");

        Assert.True(ExitsFor("get_attr", 3) > 0,
            "an unbound module was decided inside the module");
    }

    /// <summary>The counts say the inline path ran; this says it was right.
    /// Put, get, delete, get again, over two modules on one variable.</summary>
    [Fact]
    public void TheTieredAnswerMatchesTheInterpreter()
    {
        const string Program = """
            story(X, F) :- put_attr(X, m, 7), put_attr(X, n, 5),
                           get_attr(X, m, A), get_attr(X, n, B), C is A + B,
                           del_attr(X, m),
                           ( get_attr(X, m, _) -> D = yes ; D = no ),
                           get_attr(X, n, E), F = f(C, D, E).
            """;

        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("story(_, F), F == f(12, no, 5).").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("story(_, F), F == f(12, no, 5).").Success,
            "the tier disagrees with the interpreter");
    }
}
