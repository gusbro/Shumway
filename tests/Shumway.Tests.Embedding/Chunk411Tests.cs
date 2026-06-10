using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 411 (Phase 29, task: link-time unfold) — the LTO architecture: V4
/// <c>.shmo</c> objects always carry the module's raw static clauses
/// (<see cref="ShmoObject.ClauseTerms"/>, the user's fat-object decision; IP
/// stripping applies to the shipped <c>.shum</c>/exe, not the intermediate),
/// and the linker's <c>CrossModuleUnfold</c> pass detects PUBLIC meta-wrapper
/// templates across modules, rewrites caller modules' call sites against
/// (own locals ∪ global publics), and recompiles affected callers from their
/// clause terms — covering the real multi-module case the chunk-407
/// module-local driver can't see.
/// </summary>
public class Chunk411Tests
{
    // Module 'lib' exports the Arity-style ifthen/2 wrapper (PUBLIC).
    private const string LibSource =
        ":- module(lib).\n"
        + ":- public ifthen/2.\n"
        + "ifthen(X,Y) :- X -> !, Y.\n"
        + "ifthen(_,_) :- !.\n";

    // Module 'app' calls it with statically-known goals — the cross-module
    // unfold target. Note app has NO local ifthen.
    private const string AppSource =
        ":- module(app).\n"
        + ":- public run/2.\n"
        + ":- dynamic hit/1.\n"
        + "run(N, R) :- ifthen(N > 1, assertz(hit(N))), check(N, R).\n"
        + "check(N, big) :- hit(N), !.\n"
        + "check(_, small).\n";

    private static LinkResult LinkBoth()
    {
        var lib = ShmoCompiler.CompileSource(LibSource, "lib");
        var app = ShmoCompiler.CompileSource(AppSource, "app");
        return ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { lib, app },
            EntryPoints = new[] { new PredicateRef("run", 2) },
        });
    }

    [Fact]
    public void V4Shmo_CarriesClauseTerms_ReleaseIncluded()
    {
        var obj = ShmoCompiler.CompileSource(LibSource, "lib", ShmoBuildMode.Release);
        Assert.True(obj.ClauseTerms.Count >= 2);          // the two wrapper clauses
        Assert.Equal("", obj.Source);                      // text still stripped in release
        // Round-trips through the V4 writer/reader.
        var back = ShmoReader.FromBytes(ShmoWriter.ToBytes(obj));
        Assert.Equal(obj.ClauseTerms.Count, back.ClauseTerms.Count);
    }

    [Fact]
    public void CrossModuleUnfold_RewritesCaller_AndRuns()
    {
        var result = LinkBoth();
        Assert.True(result.Success);
        // The pass reports the recompile of 'app'.
        Assert.Contains(result.Diagnostics, d => d.Code == "lto_unfold");

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        // Semantics across the unfold: condition true -> side effect + big;
        // condition false -> no side effect + small.
        Assert.True(engine.Query("run(5, R), R == big.").Success);
        Assert.True(engine.Query("run(0, R), R == small.").Success);
    }

    [Fact]
    public void CrossModuleUnfold_RemovesTheWrapperCallSite()
    {
        var result = LinkBoth();
        Assert.True(result.Success);
        // Structural proof the unfold fired: app's recompiled bytecode has no
        // remaining call edge to ifthen/2 (the goal became inline control
        // flow); the wrapper's standalone predicate still exists in lib.
        var bundle = BundleReader.FromBytes(result.Bytes!);
        var appEntry = bundle.Entries.First(e => e.ModuleName == "app");
        var module = CompiledModuleCodec.Decode(appEntry.CompiledBytecode!);
        int ifthenAid = Shumway.Core.AtomTable.Intern("ifthen", permanent: true).Id;
        int ifthenFid = Shumway.Core.FunctorTable.Intern(ifthenAid, 2);
        bool anyCallToWrapper = module.Predicates
            .SelectMany(p => p.CallSites)
            .Any(cs => cs.CalleeFunctorId == ifthenFid);
        Assert.False(anyCallToWrapper,
            "app should no longer statically call ifthen/2 after the unfold");
    }

    [Fact]
    public void RuntimeMetaCall_AcrossModules_StillReachesWrapper()
    {
        // A runtime-built goal must still dispatch to lib's standalone wrapper
        // (it is public; the unfold never removes the wrapper itself).
        var result = LinkBoth();
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        Assert.True(engine.Query(
            "G = ifthen(true, true), call(G).").Success);
    }

    [Fact]
    public void LocalWrapper_ShadowsPublicOne()
    {
        // app2 defines its OWN local ifthen/2 with DIFFERENT semantics (always
        // runs Y regardless of X — not a known template, so it is NOT unfolded
        // and must keep shadowing the public wrapper for app2's own calls).
        var lib = ShmoCompiler.CompileSource(LibSource, "lib");
        var app2 = ShmoCompiler.CompileSource(
            ":- module(app2).\n"
            + ":- public go/1.\n"
            + ":- dynamic mark/1.\n"
            + "ifthen(_, Y) :- call(Y).\n"          // 1 clause, body not a control template
            + "go(N) :- ifthen(fail, assertz(mark(N))).\n",
            "app2");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { lib, app2 },
            EntryPoints = new[] { new PredicateRef("go", 1) },
        });
        Assert.True(result.Success);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        // With app2's local semantics, the action runs even though the
        // condition fails — proving the public template was NOT applied.
        Assert.True(engine.Query("go(7).").Success);
        Assert.True(engine.Query("mark(7).").Success);
    }
}
