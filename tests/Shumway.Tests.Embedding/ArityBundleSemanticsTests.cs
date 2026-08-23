using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A bundle linked from Arity-compiled modules must keep Arity CALL
/// semantics at runtime: a call to an undefined (or abolished) predicate
/// FAILS. The <c>.shmo</c> carries a per-module ArityCompat bit; the
/// linker ORs it into <see cref="Bundle.ArityCompat"/>, both bundle
/// writers persist it, and <see cref="PrologEngine.LoadBundle(Bundle)"/>
/// sets <c>unknown=fail</c> — WITHOUT flipping the <c>arity_compat</c>
/// consult mode (that would leak Arity directive-skipping into
/// unrelated files consulted after the bundle).
/// </summary>
public class ArityBundleSemanticsTests
{
    private static Bundle LinkArityProgram()
    {
        var r1 = ShmoCompiler.TryCompileSource(
            ":- dynamic f/1.\n"
            + ":- public main/0.\n"
            + "main :- assertz(f(1)), abolish(f/1).\n",
            "am1", arityCompat: true);
        Assert.True(r1.Success, string.Join("; ", r1.Errors));
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { r1.Object! },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);
        return result.Bundle!;
    }

    [Fact]
    public void ArityBit_SurvivesTheLink()
    {
        Assert.True(LinkArityProgram().ArityCompat);
    }

    [Fact]
    public void ArityBit_RoundTripsThroughBundleBytes()
    {
        byte[] bytes = BundleWriter.ToBytes(LinkArityProgram());
        Assert.True(BundleReader.FromBytes(bytes).ArityCompat);
    }

    [Fact]
    public void LoadedArityBundle_UndefinedAndAbolishedCallsFail()
    {
        var engine = new PrologEngine();
        engine.LoadBundle(LinkArityProgram());
        // unknown=fail came from the bundle; arity_compat consult mode
        // did NOT (later consults stay standard).
        Assert.True(engine.Query(
            "current_prolog_flag(unknown, fail), "
            + "current_prolog_flag(arity_compat, false).").Success);
        // main asserts then abolishes f/1 — the subsequent call FAILS
        // (Arity semantics), and a plain undefined call fails too.
        Assert.True(engine.Query("main.").Success);
        Assert.False(engine.Query("f(_).").Success);
        Assert.False(engine.Query("totally_undefined_xyz(1).").Success);
        // Re-assert revives the predicate.
        Assert.True(engine.Query("assertz(f(9)), f(X), X == 9.").Success);
    }

    [Fact]
    public void NonArityBundle_KeepsIsoUnknownError()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- public main/0.\nmain.\n", "pm");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);
        Assert.False(result.Bundle!.ArityCompat);
        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.True(engine.Query("current_prolog_flag(unknown, error).").Success);
    }
}
