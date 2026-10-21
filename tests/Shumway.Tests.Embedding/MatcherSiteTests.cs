using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: the IL tier leaves a part's stream to the matcher only
/// when a region measured past the cap on locals compacts it, the largest
/// stream at or past the threshold first, until the overshoot is covered.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class MatcherSiteTests
{
    // A fact whose argument nests three parts past the part threshold, of
    // different sizes.
    private const string Src =
        "d(k(q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o), " +
        "q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, v, w, x), " +
        "q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s), z)).";

    private static CompiledPredicate Compile()
        => new PredicateCompiler().Compile(new ClauseReader(Src).ReadAll().ToList());

    private static int[] UnifyBlitSites(byte[] code)
    {
        var sites = new System.Collections.Generic.List<int>();
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) break;
            if (op == Opcode.UnifyBlit) sites.Add(pc);
            pc += size;
        }
        return sites.ToArray();
    }

    private static System.Collections.Generic.List<(byte[] Code, int Pc)> Streams(byte[] code)
        => UnifyBlitSites(code).Select(pc => (code, pc)).ToList();

    private static CompiledPredicate CompileOne(string src)
        => new PredicateCompiler().Compile(new ClauseReader(src).ReadAll().ToList());

    /// <summary>A callee map of the fact and its callers.</summary>
    private static System.Collections.Generic.Dictionary<int, CompiledPredicate> Callers(CompiledPredicate fact, params string[] callers)
    {
        var map = new System.Collections.Generic.Dictionary<int, CompiledPredicate> { [fact.FunctorId] = fact };
        foreach (string src in callers)
        {
            var p = CompileOne(src);
            map[p.FunctorId] = p;
        }
        return map;
    }

    [Fact]
    public void ACompactionTakesTheStreamsAtOrPastTheThreshold()
    {
        int prevMin = IlRegionCost.MatcherStreamMin;
        try
        {
            // Nothing at first: every part keeps its stream.
            IlRegionCost.MatcherStreamMin = 1;
            var code = Compile().BytecodeUnfused;
            int[] sites = UnifyBlitSites(code);
            Assert.Equal(3, sites.Length);
            Assert.Empty(IlRegionCost.MatcherSites(code));
            var fact = Compile();
            var cold = Callers(fact, "c :- d(_).");
            // A threshold between the smallest part's stream (15 arguments)
            // and the others': those two go, the small one stays.
            IlRegionCost.MatcherStreamMin = IlRegionCost.EstimateLocals(code, null) / 4;
            Assert.True(IlRegionCost.CompactStreams(Streams(fact.BytecodeUnfused), Callers(fact, "c :- d(_).")));
            var chosen = IlRegionCost.MatcherSites(fact.BytecodeUnfused);
            Assert.Equal(2, chosen.Count);
            Assert.Contains(UnifyBlitSites(fact.BytecodeUnfused)[1], chosen);
            Assert.Contains(UnifyBlitSites(fact.BytecodeUnfused)[2], chosen);
            // A lower threshold takes the rest; then there is nothing left.
            IlRegionCost.MatcherStreamMin = 1;
            Assert.True(IlRegionCost.CompactStreams(Streams(fact.BytecodeUnfused), cold));
            Assert.Equal(3, IlRegionCost.MatcherSites(fact.BytecodeUnfused).Count);
            Assert.False(IlRegionCost.CompactStreams(Streams(fact.BytecodeUnfused), cold));
            // Above every stream, nothing goes.
            IlRegionCost.MatcherStreamMin = 100000;
            var fact2 = Compile();
            Assert.False(IlRegionCost.CompactStreams(Streams(fact2.BytecodeUnfused), Callers(fact2, "c :- d(_).")));
            Assert.Empty(IlRegionCost.MatcherSites(fact2.BytecodeUnfused));
        }
        finally { IlRegionCost.MatcherStreamMin = prevMin; }
    }

    [Fact]
    public void OnlyAnArgumentEveryCallerPassesFreshIsCompacted()
    {
        int prevMin = IlRegionCost.MatcherStreamMin;
        try
        {
            IlRegionCost.MatcherStreamMin = 1;
            // A caller that may pass the argument bound: the streams stay.
            var bound = Compile();
            Assert.False(IlRegionCost.CompactStreams(Streams(bound.BytecodeUnfused), Callers(bound, "b(X) :- d(X).")));
            Assert.Empty(IlRegionCost.MatcherSites(bound.BytecodeUnfused));
            // Fresh at one site and bound at another: they stay.
            var mixed = Compile();
            Assert.False(IlRegionCost.CompactStreams(Streams(mixed.BytecodeUnfused), Callers(mixed, "c :- d(_).", "b(X) :- d(X).")));
            // No local caller at all: unknown, they stay.
            var alone = Compile();
            Assert.False(IlRegionCost.CompactStreams(Streams(alone.BytecodeUnfused), Callers(alone)));
            // Fresh everywhere, a body variable first met at the call included.
            var fresh = Compile();
            Assert.True(IlRegionCost.CompactStreams(Streams(fresh.BytecodeUnfused), Callers(fresh, "c :- d(_).", "e(Y) :- d(X), f(X, Y).")));
            Assert.Equal(3, IlRegionCost.MatcherSites(fresh.BytecodeUnfused).Count);
        }
        finally { IlRegionCost.MatcherStreamMin = prevMin; }
    }

    [Fact]
    public void TheEstimateLeavesOutTheStreamsTheMatcherStandsInFor()
    {
        int prevMin = IlRegionCost.MatcherStreamMin;
        try
        {
            IlRegionCost.MatcherStreamMin = 1;
            var fact = Compile();
            var code = fact.BytecodeUnfused;
            int whole = IlRegionCost.EstimateLocals(code, null);
            Assert.True(IlRegionCost.CompactStreams(Streams(code), Callers(fact, "c :- d(_).")));
            int matched = IlRegionCost.EstimateLocals(code, null);
            Assert.True(matched < whole, $"{matched} with the matcher, {whole} without");
        }
        finally { IlRegionCost.MatcherStreamMin = prevMin; }
    }

    [Fact]
    public void EveryPartGoesToTheMatcherUnderTheSwitch()
    {
        bool prev = IlRegionCost.MatchEveryPart;
        try
        {
            IlRegionCost.MatchEveryPart = true;
            var code = Compile().BytecodeUnfused;
            Assert.Equal(3, IlRegionCost.MatcherSites(code).Count);
        }
        finally { IlRegionCost.MatchEveryPart = prev; }
    }

    private const string Data =
        "data(k(q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o), q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, v, w, x), q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s), z)).\n";

    // A loop that takes the fact fresh every time: with the cap set under
    // the region's locals, the first emission measures past it and the
    // compaction sends the fact's streams to the matcher, the members kept.
    private const string Cold = Data + """
        loop(0) :- !.
        loop(N) :- data(_), N1 is N - 1, loop(N1).
        """;

    // A loop that reads the fact's argument bound: past the cap or not, the
    // streams stay, as the matcher's pass is the slower read.
    private const string Hot = Data + """
        hot(0, _) :- !.
        hot(N, D) :- data(D), N1 is N - 1, hot(N1, D).
        run(N) :- data(D), hot(N, D).
        """;

    [Fact]
    public void ARegionPastTheCapCompactsTheStreamsOfItsColdArguments()
    {
        int prevTarget = IlRegionCost.LocalsTarget, prevMin = IlRegionCost.MatcherStreamMin;
        try
        {
            var plain = new Shumway.Embedding.PrologEngine();
            plain.IlPromotion.Threshold = 0;
            plain.ConsultString(Cold);
            string expected = plain.Query("data(D).").Bindings["D"].ToString()!;
            IlRegionCost.LocalsTarget = 40;
            IlRegionCost.MatcherStreamMin = 1;
            int before = IlRegionCost.Compactions;
            var tiered = new Shumway.Embedding.PrologEngine();
            tiered.IlPromotion.Threshold = 1;
            tiered.IlPromotion.BackgroundCompilation = false;
            tiered.ConsultString(Cold);
            Assert.True(tiered.Query("loop(3).").Success);
            Assert.True(IlRegionCost.Compactions > before, "no region compacted its streams");
            Assert.Equal(expected, tiered.Query("data(D).").Bindings["D"].ToString());
            Assert.True(tiered.Query("loop(50), data(D), D = k(_, Q, _, _), Q = q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, v, w, x).").Success);
        }
        finally
        {
            IlRegionCost.LocalsTarget = prevTarget;
            IlRegionCost.MatcherStreamMin = prevMin;
        }
    }

    [Fact]
    public void AStreamSomeCallerReadsBoundStaysPastTheCap()
    {
        int prevTarget = IlRegionCost.LocalsTarget, prevMin = IlRegionCost.MatcherStreamMin;
        try
        {
            IlRegionCost.LocalsTarget = 40;
            IlRegionCost.MatcherStreamMin = 1;
            int before = IlRegionCost.Compactions;
            var tiered = new Shumway.Embedding.PrologEngine();
            tiered.IlPromotion.Threshold = 1;
            tiered.IlPromotion.BackgroundCompilation = false;
            tiered.ConsultString(Hot);
            Assert.True(tiered.Query("run(3).").Success);
            Assert.Equal(before, IlRegionCost.Compactions);
        }
        finally
        {
            IlRegionCost.LocalsTarget = prevTarget;
            IlRegionCost.MatcherStreamMin = prevMin;
        }
    }
}
