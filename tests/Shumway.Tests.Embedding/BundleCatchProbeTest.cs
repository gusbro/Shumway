using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression: a bundled program using a meta-builtin (catch/3 is
/// the canonical case; assertz / findall / call/N have the same
/// shape) used to raise <c>existence_error/2</c> on the meta-
/// builtin's functor when loaded via
/// <see cref="PrologEngine.LoadBundle(Bundle)"/> after a
/// <c>shumway-link</c>. Root cause: <see cref="ShmoCompiler"/>
/// only called <c>StandardBuiltins.EnsureRegistered</c>, so the
/// meta-builtins were invisible to the WAM compiler at compile
/// time and it emitted <c>Execute</c> opcodes for them instead of
/// <c>CallBuiltin</c>. The runtime IL path then tried to resolve
/// them as user predicates and failed.
/// </summary>
public class BundleMetaBuiltinDispatchTests
{
    [Fact]
    public void Catch3_InBundleLoadedProgram_Works()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- public test/0.\n"
            + "test :- catch(throw(boom), E, (write(caught), write(E))).\n",
            "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("test", 0) },
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.True(engine.Query("test.").Success);
    }

    [Fact]
    public void Assertz_InBundleLoadedProgram_Works()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- dynamic fact/1.\n"
            + ":- public seed/0.\n"
            + "seed :- assertz(fact(1)), assertz(fact(2)).\n",
            "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("seed", 0) },
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.True(engine.Query("seed.").Success);
        Assert.True(engine.Query("fact(1).").Success);
        Assert.True(engine.Query("fact(2).").Success);
    }

    [Fact]
    public void Findall_InBundleLoadedProgram_Works()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- public collect/1.\n"
            + ":- public item/1.\n"
            + "item(a). item(b). item(c).\n"
            + "collect(L) :- findall(X, item(X), L).\n",
            "m");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("collect", 1) },
        });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var sol = engine.Query("collect(L).");
        Assert.True(sol.Success);
    }
}
