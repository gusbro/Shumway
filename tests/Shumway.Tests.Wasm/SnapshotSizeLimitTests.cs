using System.Linq;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A dynamic predicate whose snapshot has more instructions than
/// <see cref="WasmPromotionStore.MaxSnapshotInstructions"/> runs its bytecode.
/// When the code its clauses have already puts it over, it is refused before
/// the snapshot is taken; else the compile refuses it. Once its calls cross the
/// JIT indexing threshold it is laid out indexed in place, as under the IL tier:
/// no build of the product and no new layout of the code space, so the
/// snapshots already running stay. Every answer is checked against Tier-0.</summary>
public sealed class SnapshotSizeLimitTests
{
    // t/2 has 40 facts and s/1 one: the limit sits between their snapshots.
    private static readonly string Corpus = """
        :- dynamic(t/2).
        :- dynamic(s/1).
        s(1).
        look(K, V) :- t(K, V).
        tl(0) :- !.
        tl(N) :- t(k7, 7), N1 is N - 1, tl(N1).
        sl(0) :- !.
        sl(N) :- s(1), N1 is N - 1, sl(N1).
        run(0) :- !.
        run(N) :- t(k7, 7), s(1), N1 is N - 1, run(N1).

        """ + string.Concat(Enumerable.Range(0, 40).Select(i => $"t(k{i}, {i}).\n"));

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static void Agree(PrologEngine plain, PrologEngine tiered, string goal)
    {
        Assert.True(plain.Query(goal).Success, goal + " on Tier-0");
        Assert.True(tiered.Query(goal).Success, goal + " on the tier");
    }

    // early: the limit is under what t/2's compiled clauses already come to,
    // so it is refused before its snapshot is taken. Else the limit is that
    // much, and the compile of the snapshot, which indexes it, refuses it.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ALargeSnapshotIsRefused_AndThePredicateIsLaidOutIndexedInPlace(bool compiledAside, bool early)
    {
        int saved = WasmPromotionStore.MaxSnapshotInstructions;
        try
        {
            var plain = new PrologEngine();
            plain.IlPromotion.Threshold = 0;
            plain.ConsultString(Corpus);
            var (tiered, _, world) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 4);
            tiered.JitIndexing.Threshold = 3;
            var wasm = tiered.IlPromotion.Wasm!;
            if (compiledAside)
                wasm.SnapshotCompiler = (snapshot, digest, done) =>
                    WasmBundleTier.CompileSnapshotAside(tiered, world, snapshot, digest, done, work => work());
            int t = Fid("t", 2), s = Fid("s", 1);

            Agree(plain, tiered, "sl(8).");
            Assert.True(wasm.HasShadow(s), "s/1 was not promoted as a snapshot");
            int least = tiered.SnapshotInstructionsAtLeast(t);
            Assert.True(least >= 40, "the estimate does not count t/2's clauses");
            WasmPromotionStore.MaxSnapshotInstructions = early ? least - 1 : least;
            // Called on its own, t/2's own call is the one that finds its
            // snapshot refused.
            Agree(plain, tiered, "tl(8).");
            Assert.False(wasm.HasShadow(t), "t/2 runs as a snapshot over the limit");
            Assert.True(wasm.IsUnpromotable(t));
            if (early) Assert.Contains("a snapshot of at least", wasm.RefusalReason(t));
            else if (compiledAside) Assert.Contains("a snapshot of ", wasm.RefusalReason(t));
            if (compiledAside)
                // s/1's, and t/2's unless it was refused before it was taken.
                Assert.Equal(early ? 1 : 2, wasm.SnapshotCompilesRequested);
            Assert.True(tiered.JitIndexing.IsHot(t));
            Assert.Equal(0, tiered.HotnessFlipsInPlace);

            long builds = tiered.ProgramProductBuilds, drops = tiered.PersistentInvalidations;
            Agree(plain, tiered, "look(k3, V), V == 3.");
            Assert.Equal(1, tiered.HotnessFlipsInPlace);
            Assert.Equal(builds, tiered.ProgramProductBuilds);
            Assert.Equal(drops, tiered.PersistentInvalidations);
            Assert.True(wasm.HasShadow(s), "the snapshot of s/1 went with a new layout");

            // Every caller reaches the version: the query, a static rule, and
            // tl/1 and run/1, which run in the tier.
            Agree(plain, tiered, @"\+ look(nope, _).");
            Agree(plain, tiered, "tl(8).");
            Agree(plain, tiered, "run(8).");
            Agree(plain, tiered, "findall(K, t(K, _), Ks), length(Ks, 40).");
            Agree(plain, tiered, @"retract(t(k3, 3)), \+ look(k3, _).");
            Agree(plain, tiered, "assertz(t(new, 99)), look(new, V), V == 99.");
            Agree(plain, tiered, "findall(K, t(K, _), Ks), length(Ks, 40), memberchk(new, Ks).");
            Assert.Equal(1, tiered.HotnessFlipsInPlace);
        }
        finally
        {
            WasmPromotionStore.MaxSnapshotInstructions = saved;
        }
    }
}
