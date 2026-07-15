using System.Linq;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — the linker preserves a <see cref="ShmoBuildMode.Debuggable"/> object's debug-shape
/// WAM end to end: the two whole-program LTO passes (meta-wrapper unfold, redundant-cut elision)
/// skip it, and a local <c>--entry</c>/<c>--goal</c> is promoted to public WITHOUT shifting the
/// source lines the debug stop sites are keyed to.
/// </summary>
public sealed class Adr035DebugLinkTests
{
    private static CompiledPredicate DecodeEntryPredicate(
        byte[] bundleBytes, string module, string predName, int arity)
    {
        var bundle = BundleReader.FromBytes(bundleBytes);
        var entry = bundle.Entries.Single(e => e.ModuleName == module);
        var mod = CompiledModuleCodec.Decode(entry.CompiledBytecode!);
        return mod.Predicates.Single(p =>
        {
            var (atomId, _) = FunctorTable.Lookup(p.FunctorId);
            return AtomTable.GetById(atomId)!.Name == predName && p.Arity == arity;
        });
    }

    [Fact]
    public void CutElisionLto_SkipsADebuggableObject_KeepingItsWamVerbatim()
    {
        // run/0's single-clause trailing cut prunes nothing, so the whole-program cut-elision
        // pass would elide it for a Release/Debug object. A Debuggable object keeps every cut
        // (debug codegen disables the intra-module elision, and the linker must not re-add it),
        // so its linked bytecode is byte-identical to the .shmo it came from.
        const string prog =
            ":- public run/0.\n" +
            "run :- helper, !.\n" +
            "helper.\n";
        var obj = ShmoCompiler.CompileSource(prog, "m", ShmoBuildMode.Debuggable);

        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("run", 0) },   // public → no promotion
            BakePrelude = false,
            IncludeCompiledIl = false,
        }).Bytes!;

        var entry = BundleReader.FromBytes(bytes).Entries.Single(e => e.ModuleName == "m");
        Assert.Equal(obj.Bytecode, entry.CompiledBytecode);   // passed through untouched

        // And the debug side tables rode along.
        var run = DecodeEntryPredicate(bytes, "m", "run", 0);
        Assert.NotEmpty(run.DebugStops);
    }

    [Fact]
    public void LocalEntryPromotion_DoesNotShiftStopSiteLines()
    {
        // main/0 is LOCAL, so linking it as the entry point promotes it to public. The promotion
        // must not move any line: the body goals stay on lines 3 and 4, where the debugger's
        // breakpoints are drawn. (A prepended `:- public main/0.` would slide them to 4 and 5.)
        const string prog =
            "main :-\n" +          // 1
            "    helper(X),\n" +   // 2
            "    emit(X),\n" +     // 3
            "    done.\n" +        // 4
            "helper(a).\n" +       // 5
            "emit(_).\n" +         // 6
            "done.\n";             // 7
        var obj = ShmoCompiler.CompileSource(prog, "m", ShmoBuildMode.Debuggable);

        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 0) },   // local → promoted
            BakePrelude = false,
            IncludeCompiledIl = false,
        }).Bytes!;

        var main = DecodeEntryPredicate(bytes, "m", "main", 0);

        int fileId = DebugSiteTable.InternFile("m.pl");
        var lines = main.DebugStops
            .Select(s => DebugSiteTable.Get(s.SiteId))
            .Where(site => site.FileId == fileId)
            .Select(site => site.Line)
            .ToHashSet();

        // The body goals are on lines 2, 3, 4 — unshifted. If promotion had prepended a
        // `:- public main/0.` directive, everything would have slid down one line and no stop
        // would land on line 2.
        Assert.Contains(2, lines);
        Assert.Contains(3, lines);
    }

    [Fact]
    public void DebuggableBundle_LoadsFromBakedWam_WithoutReconsult_AndBreakpointsBind()
    {
        // Task 4a — the payoff: a Debuggable bundle loaded under a debug session runs its
        // BAKED debug WAM directly (no re-consult from source, zero recompile at load), and its
        // baked stop sites still bind breakpoints.
        const string prog =
            ":- public run/1.\n" +   // 1
            "run(X) :-\n" +          // 2
            "    step(X).\n" +       // 3
            "step(_).\n";            // 4
        var obj = ShmoCompiler.CompileSource(prog, "demo", ShmoBuildMode.Debuggable);
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("run", 1) },
            BakePrelude = false,
            IncludeCompiledIl = false,
        }).Bytes!;
        var bundle = BundleReader.FromBytes(bytes);

        var engine = new PrologEngine();
        using ChannelDebugSession session = engine.EnableDebugging();

        engine.LoadBundle(bundle);

        // The baked-WAM path ran (LoadEntryFromBytecode populates PrecompiledModules); a
        // re-consult would not have.
        Assert.Contains("demo", engine.PrecompiledModules.Keys);

        // The stop site on line 3 survived compile -> link -> load and binds.
        Assert.True(engine.AddBreakpoint("demo.pl", 3) > 0,
            "a breakpoint in the baked debug WAM should bind");

        // And the program runs.
        Assert.Single(engine.QueryAll("run(1).").ToList());
    }
}
