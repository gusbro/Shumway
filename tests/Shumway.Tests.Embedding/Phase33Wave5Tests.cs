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

    [Fact]
    public void T1_WithoutPrune_FullPreludeStillWorks()
    {
        var full = LinkIt(Program, prune: false);
        var e = PrologEngine.FromBundle(full);
        Assert.True(e.Query("main(L), L == 15.").Success);
        Assert.True(e.Query("msort([b, a], L), L == [a, b].").Success);
    }
}
