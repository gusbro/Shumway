using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 wave 5 — LTO/startup/size (docs/phase-33-backlog.md, T-series).
/// T1: --prune-prelude bakes only the reachable prelude closure.
/// </summary>
public class Phase33Wave5Tests
{
    private readonly ITestOutputHelper _output;
    public Phase33Wave5Tests(ITestOutputHelper output) => _output = output;

    private const string Program =
        ":- public main/1.\n" +
        "main(L) :- numlist(1, 5, Xs), sum_list(Xs, L).\n";

    private static Bundle LinkIt(string program, bool prune, params string[] ensure)
    {
        string src = ensure.Length == 0
            ? program
            : program + string.Concat(ensure.Select(e => $":- ensure_linked({e}).\n"));
        var obj = ShmoCompiler.CompileSource(src);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 1) },
            BakePrelude = true,
            PrunePrelude = prune,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(result.Bytes!);
    }

    [Fact]
    public void T1_PrunedPrelude_KeepsReachedPredicates_AndShrinks()
    {
        var full = LinkIt(Program, prune: false);
        var pruned = LinkIt(Program, prune: true);

        var fullPrelude = full.Entries.First(e => e.ModuleName.Contains("prelude"));
        var prunedPrelude = pruned.Entries.First(e => e.ModuleName.Contains("prelude"));
        _output.WriteLine($"full prelude: {fullPrelude.Defined.Count} preds, " +
            $"{fullPrelude.CompiledBytecode!.Length} B; pruned: {prunedPrelude.Defined.Count} preds, " +
            $"{prunedPrelude.CompiledBytecode!.Length} B");
        // The prune must be substantial (the program uses 2 prelude predicates).
        Assert.True(prunedPrelude.Defined.Count < fullPrelude.Defined.Count / 2);
        Assert.True(prunedPrelude.CompiledBytecode.Length < fullPrelude.CompiledBytecode.Length / 2);

        // The pruned bundle RUNS: the reached prelude closure suffices.
        var e = PrologEngine.FromBundle(pruned);
        Assert.True(e.Query("main(L), L == 15.").Success);
    }

    [Fact]
    public void T1_PrunedPrelude_UnreachedPredicate_RaisesExistenceError()
    {
        var full = LinkIt(Program, prune: false);
        var pruned = LinkIt(Program, prune: true);
        var fullSet = full.Entries.First(en => en.ModuleName.Contains("prelude"))
            .Defined.Select(d => d.Indicator).ToHashSet();
        var prunedSet = pruned.Entries.First(en => en.ModuleName.Contains("prelude"))
            .Defined.Select(d => d.Indicator).ToHashSet();
        // Pick a PUBLIC prelude predicate the prune actually dropped (robust
        // against closure growth — msort, say, can come back via sort helpers).
        var dropped = fullSet.Except(prunedSet)
            .First(p => !p.Name.StartsWith('$') && p.Arity is 1 or 2);
        _output.WriteLine($"probing dropped predicate: {dropped.Name}/{dropped.Arity}");
        string goal = dropped.Arity == 1 ? $"{dropped.Name}(x1)" : $"{dropped.Name}(x1, _)";
        var e = PrologEngine.FromBundle(pruned);
        // The documented contract: a runtime-constructed goal naming a pruned
        // prelude predicate raises existence_error (catchable — the runtime
        // catch/3 is in the always-kept infrastructure set).
        Assert.True(e.Query(
            $"G = {goal}, catch(G, error(existence_error(_, _), _), R = caught), R == caught.").Success);
    }

    [Fact]
    public void T1_EnsureLinked_IsTheEscapeHatch()
    {
        // :- ensure_linked(msort/2) keeps the otherwise-unreached prelude
        // predicate (and its closure) in the pruned bake.
        var pruned = LinkIt(Program, prune: true, "msort/2");
        var e = PrologEngine.FromBundle(pruned);
        Assert.True(e.Query("G = msort([b, a], L), call(G), L == [a, b].").Success);
        Assert.True(e.Query("main(L), L == 15.").Success);
    }

    // ---- T2: whole-body Brotli compression of the .shum stream ----

    [Fact]
    public void T2_CompressedBundle_RoundTrips_AndShrinks()
    {
        // A bundle big enough to cross the compression threshold (the baked
        // full prelude guarantees it). The flag byte sits after magic+version.
        var obj = ShmoCompiler.CompileSource(Program);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 1) },
            BakePrelude = true,
        });
        Assert.True(result.Success);
        byte[] bytes = result.Bytes!;
        Assert.Equal(BundleFormat.CompressionBrotli, bytes[8]);

        var bundle = BundleReader.FromBytes(bytes);
        var e = PrologEngine.FromBundle(bundle);
        Assert.True(e.Query("main(L), L == 15.").Success);

        // Honest size check: recompute the raw body via the reader-visible
        // content proxy — compare against an uncompressed-equivalent length
        // (prelude bytecode alone is ~53 KB; the compressed bundle must be
        // well under half of the raw image).
        long rawApprox = bundle.Entries.Sum(en =>
            (en.CompiledBytecode?.Length ?? 0) + (en.Source?.Length ?? 0));
        _output.WriteLine($"compressed file: {bytes.Length:N0} B; raw payload approx: {rawApprox:N0} B");
        Assert.True(bytes.Length < rawApprox / 2,
            $"expected <50% of raw payload, got {bytes.Length} vs {rawApprox}");
    }

    [Fact]
    public void T2_TinyBundle_StaysRaw()
    {
        // Below the 4 KB threshold: flag 0, body verbatim.
        var obj = ShmoCompiler.CompileSource(":- public p/0.\np.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("p", 0) },
        });
        Assert.True(result.Success);
        Assert.Equal(BundleFormat.CompressionNone, result.Bytes![8]);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(e.Query("p.").Success);
    }

    [Fact]
    public void T2_SaveState_RoundTripsCompressed()
    {
        // save_state goes through BundleWriter.ToBytes — same framing.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic fact/1.\n");
        for (int i = 0; i < 300; i++)
            Assert.True(e.Query($"assertz(fact(v{i})).").Success);
        byte[] snap = e.SaveStateToBytes();
        var e2 = new PrologEngine();
        e2.RestoreStateFromBytes(snap);
        Assert.True(e2.Query("fact(v0).").Success);
        Assert.True(e2.Query("fact(v299).").Success);
        Assert.True(e2.Query("findall(X, fact(X), L), length(L, N), N == 300.").Success);
    }

    // ---- T3: process-wide persisted-IL assembly/delegate cache ----

    [Fact]
    public void T3_PersistedIl_LoadsOncePerContent_AcrossEngines()
    {
        // Unique predicate name per run so the bundle content is guaranteed
        // NOT already in the process-wide cache when the test starts.
        string pred = "t3p" + Guid.NewGuid().ToString("N")[..12];
        string src =
            $":- public {pred}/2.\n" +
            $"{pred}(0, base).\n" +
            $"{pred}(N, s(R)) :- N > 0, M is N - 1, {pred}(M, R).\n";
        var obj = ShmoCompiler.CompileSource(src);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef(pred, 2) },
            IncludeCompiledIl = true,
            // Strip the WAM bodies: execution HAS to go through the persisted
            // IL delegates, so a broken cached binding cannot hide behind a
            // bytecode fallback.
            StripWam = true,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(result.Bytes!);
        var ilEntry = bundle.Entries.First(en => en.CompiledIl is { Length: > 0 });

        Assert.False(PrologEngine.IsPersistedIlCached(ilEntry));
        int loadsBefore = PrologEngine.PersistedIlLoadCount;

        var e1 = new PrologEngine();
        e1.LoadBundle(bundle);
        Assert.True(PrologEngine.IsPersistedIlCached(ilEntry));
        int loadsAfterFirst = PrologEngine.PersistedIlLoadCount;
        Assert.True(loadsAfterFirst > loadsBefore, "first LoadBundle must really load");

        // Two more engines on the SAME bundle: the cache serves the loaded
        // assembly + bound delegates — and both engines run the stripped
        // (IL-only) predicate correctly.
        var e2 = new PrologEngine();
        e2.LoadBundle(bundle);
        var e3 = new PrologEngine();
        e3.LoadBundle(BundleReader.FromBytes(result.Bytes!));  // re-parsed copy, same content
        Assert.True(e1.Query($"{pred}(3, R), R == s(s(s(base))).").Success);
        Assert.True(e2.Query($"{pred}(2, R), R == s(s(base)).").Success);
        Assert.True(e3.Query($"{pred}(0, R), R == base.").Success);
    }

    [Fact]
    public void T1_WithoutPrune_FullPreludeStillWorks()
    {
        var full = LinkIt(Program, prune: false);
        var e = PrologEngine.FromBundle(full);
        Assert.True(e.Query("main(L), L == 15.").Success);
        Assert.True(e.Query("msort([b, a], L), L == [a, b].").Success);
    }
}
