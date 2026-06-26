using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Source-less precompiled bytecode carries module-local pool ids for float /
/// string / bigint literals; at load they must be remapped into the engine's one
/// shared <c>_literalPools</c> (RemapPrecompiledLiterals), else a static literal
/// reads whatever value sits at that id in the merged pool. The classic trigger:
/// a Release (source-stripped) bundle with TWO floats where a static
/// <c>X =:= 2.5</c> read the OTHER float (the linked --exe bug).
/// </summary>
public class FloatArithLinkRepro
{
    private static PrologEngine LinkAndLoad(string src, ShmoBuildMode mode, bool bakePrelude)
    {
        var obj = ShmoCompiler.CompileSource(src, "m", mode);
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            BakePrelude = bakePrelude,
            StripSource = mode == ShmoBuildMode.Release,
        }).Bytes!;
        // The --exe path is PrologEngine.FromBundle (bare-loaded, source-less).
        return PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
    }

    // Two float facts; a static `=:= 2.5` must resolve to 2.5, not the other float.
    private const string TwoFloats =
        ":- public main/0.\n:- dynamic temp/2.\ntemp(mon, 1.5).\ntemp(tue, 2.5).\n" +
        "main :- temp(tue, X), X =:= 2.5.\n";

    [Fact]
    public void TwoFloats_ReleaseSourceStripped()
        => Assert.True(LinkAndLoad(TwoFloats, ShmoBuildMode.Release, bakePrelude: true)
            .Query("main.").Success);

    [Fact]
    public void TwoFloats_Debug()
        => Assert.True(LinkAndLoad(TwoFloats, ShmoBuildMode.Debug, bakePrelude: false)
            .Query("main.").Success);

    [Fact]
    public void TwoFloats_Consult_Control()
    {
        var e = new PrologEngine();
        e.ConsultString(TwoFloats);
        Assert.True(e.Query("main.").Success);
    }

    // The float literal inside an if-then-else helper (MetaTransform lowers it to a
    // separate static predicate — exactly the flt2 --exe shape).
    [Fact]
    public void TwoFloats_ArithLiteral_InIfThenElse_Release()
    {
        var e = LinkAndLoad(
            ":- public main/0.\n:- dynamic temp/2.\ntemp(mon, 1.5).\ntemp(tue, 2.5).\n" +
            "main :- temp(tue, X), ( X =:= 2.5 -> R = ok ; R = wrong ), R == ok.\n",
            ShmoBuildMode.Release, bakePrelude: true);
        Assert.True(e.Query("main.").Success);
    }
}
