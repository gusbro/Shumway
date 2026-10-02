using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The engine's own libraries load from bundles baked at build
/// time, and the linker's library mode that bakes them: every predicate a
/// library defines is a root (a program link prunes from its entry
/// points), a clause for a predicate the prelude declares dynamic is a
/// dynamic clause in the object too, and a library loads once however
/// many times it is asked for.</summary>
public sealed class EngineLibraryBundleTests
{
    private static LinkResult Link(ShmoObject obj, bool library)
        => ShmoLinker.Link(new LinkConfig { Objects = new[] { obj }, Library = library });

    private const string TwoPreds = """
        :- module(twop).
        :- public one/1.
        one(1).
        other(2).
        """;

    [Fact]
    public void LibraryLink_RootsEveryDefinedPredicate()
    {
        var r = Link(ShmoCompiler.CompileSource(TwoPreds, "twop"), library: true);
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        Assert.Contains("twop", r.ReachedModules);
        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        Assert.True(engine.Query("one(1).").Success);
        // Nothing referenced other/2; a library keeps it anyway.
        Assert.True(engine.Query("twop:other(2).").Success);
    }

    [Fact]
    public void ProgramLink_WithNoEntryPoints_ReachesNothing()
    {
        // The counter-proof: the same object as a program has no roots.
        var r = Link(ShmoCompiler.CompileSource(TwoPreds, "twop"), library: false);
        Assert.DoesNotContain("twop", r.ReachedModules);
        Assert.Contains("twop", r.UnreachableModules);
    }

    private const string HookModule = """
        :- module(hooks).
        :- public own/1.
        own(x).
        attribute_goals(hooks, a, V, G) :- ( V == 1 -> G = one ; G = other ).
        portray(secret).
        :- dynamic(own_dyn/1).
        own_dyn(y).
        """;

    [Fact]
    public void ObjectCompiler_TreatsPreludeDeclaredDynamicsAsDynamic()
    {
        var obj = ShmoCompiler.CompileSource(HookModule, "hooks");
        var seeds = obj.DynamicSeeds.Select(s => s.Indicator).ToList();
        Assert.Contains(new PredicateRef("attribute_goals", 4), seeds);
        Assert.Contains(new PredicateRef("portray", 1), seeds);
        // ...listed exactly like a predicate the object itself declares
        // dynamic, and unlike its static own/1.
        Assert.Contains(new PredicateRef("own_dyn", 1), seeds);
        Assert.DoesNotContain(new PredicateRef("own", 1), seeds);
        Assert.Contains(obj.Defined, d => d.Indicator == new PredicateRef("own", 1));
    }

    [Fact]
    public void BundleWithPreludeDeclaredDynamicClauses_DispatchesThem()
    {
        var r = Link(ShmoCompiler.CompileSource(HookModule, "hooks"), library: true);
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var engine = new PrologEngine();
        // The hook exists, clauseless, before the bundle: the load is a
        // mutation of a predicate the engine already compiled.
        Assert.False(engine.Query("attribute_goals(_, _, _, _).").Success);
        engine.LoadBundle(r.Bundle!);
        var q = engine.Query("attribute_goals(hooks, a, 1, G).");
        Assert.True(q.Success);
        Assert.Equal("one", q.Bindings["G"].ToString());
        Assert.True(engine.Query("attribute_goals(hooks, a, 2, other).").Success);
        Assert.True(engine.Query("portray(secret).").Success);
        Assert.True(engine.Query("predicate_property(attribute_goals(_, _, _, _), dynamic).").Success);
        Assert.True(engine.Query("clause(portray(X), true), X == secret.").Success);
    }

    [Fact]
    public void ClpfdFromTheBundle_ProjectsResiduals()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        Assert.True(engine.Query("X #> 3, copy_term(X, _, Gs), Gs = [_ in L], L == 4..sup.").Success);
        Assert.False(engine.Query("X #> 3, copy_term(X, _, []).").Success);
    }

    [Fact]
    public void EngineLibrary_LoadsOnce_HoweverAsked()
    {
        var engine = new PrologEngine();
        engine.UseCoroutining();
        Assert.True(engine.Query("use_module(library(coroutining)).").Success);
        engine.UseCoroutining();
        engine.ConsultString(":- use_module(library(coroutining)).\nrun(X) :- dif(X, a), X = b.\n");
        Assert.True(engine.Query("run(b).").Success);
        Assert.False(engine.Query("dif(X, a), X = a.").Success);
        // One definition of dif/2, not one per request.
        var n = engine.Query("findall(B, clause(dif(_, _), B), Bs), length(Bs, N).");
        Assert.True(n.Success);
        Assert.Equal("1", n.Bindings["N"].ToString());
    }

    [Fact]
    public void AllThreeLibraries_ShareAnEngine()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        engine.UseClpr();
        engine.UseCoroutining();
        var q = engine.Query("X in 1..3, dif(Y, z), {Z = 2 * 1.5}, X #> 2, Y = w, Z =:= 3.");
        Assert.True(q.Success);
        Assert.Equal("3", q.Bindings["X"].ToString());
    }
}
