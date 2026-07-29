using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The C-linker symbol model for same-name predicates across linked
/// modules: public + public is a hard error (duplicate_public, elsewhere);
/// public + LOCAL is legal — the local wins inside its own module, like a C
/// `static` shadowing a global — reported only as an opt-in
/// <c>--warn-shadow</c> warning, and always listed in the <c>--map</c>.</summary>
public sealed class LinkShadowTests
{
    private const string ModA =
        ":- module(moda).\n:- public pepe/2.\npepe(from_a, 1).\n";
    private const string ModB =
        ":- module(modb).\n:- public runb/1.\n" +
        "pepe(from_b, 2).\nrunb(X) :- pepe(X, _).\n";
    private const string Main =
        ":- module(mainm).\n:- public main/2.\n" +
        "main(B, A) :- runb(B), pepe(A, _).\n";

    private static LinkResult Link(bool warnShadow)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(ModA, "moda"),
                ShmoCompiler.CompileSource(ModB, "modb"),
                ShmoCompiler.CompileSource(Main, "mainm"),
            },
            EntryPoints = new[] { new PredicateRef("main", 2) },
            WarnShadow = warnShadow,
        });

    [Fact]
    public void LocalShadowingAPublic_LinksSilently_LocalWinsInsideItsModule()
    {
        var r = Link(warnShadow: false);
        Assert.True(r.Success);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "local_shadows_public");

        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        var sol = engine.Query("main(B, A).");
        Assert.True(sol.Success);
        Assert.Equal("from_b", sol["B"]!.ToString());   // B's local wins inside B
        Assert.Equal("from_a", sol["A"]!.ToString());   // others see A's public
    }

    [Fact]
    public void WarnShadow_EmitsTheOptInWarning()
    {
        var r = Link(warnShadow: true);
        Assert.True(r.Success);   // still a warning, never an error
        var diag = Assert.Single(
            r.Diagnostics, d => d.Code == "local_shadows_public");
        Assert.Equal(LinkSeverity.Warning, diag.Severity);
        Assert.Contains("modb", diag.Message);
        Assert.Contains("moda", diag.Message);
    }

    [Fact]
    public void MapFile_AlwaysListsTheShadow()
    {
        var r = Link(warnShadow: false);
        string map = ShmoBundleMap.GenerateText(
            r.LinkedObjects,
            new[] { new PredicateRef("main", 2) },
            r);
        Assert.Contains("shadowing a public", map);
        Assert.Contains("pepe/2", map);
        Assert.Contains("modb", map);
        Assert.Contains("moda", map);
    }
}
