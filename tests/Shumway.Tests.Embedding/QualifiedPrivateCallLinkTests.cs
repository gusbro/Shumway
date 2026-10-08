using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-056: a written <c>M:Goal</c> reaches M's private predicates,
/// in a linked program as in the REPL (and in SICStus and Scryer). The
/// linker accepts it, and counts the predicate it reaches as callable by name
/// from outside: link-time pruning must keep its standalone form, since the
/// call comes from another module.</summary>
public sealed class QualifiedPrivateCallLinkTests
{
    private const string Lib = """
        :- module(qplib, [pub_p/1]).
        pub_p(X) :- helper_p(X).
        helper_p(from_helper).
        """;

    private static LinkResult Link(string caller, bool stripWam = false)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(Lib, "qplib"),
                ShmoCompiler.CompileSource(caller, "qpapp"),
            },
            EntryPoints = new[] { new PredicateRef("main", 1) },
            StripWam = stripWam,
        });

    private const string Caller = """
        :- module(qpapp, [main/1]).
        main(X) :- qplib:helper_p(X).
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQualifiedCallToAPrivateLinksAndRuns(bool stripWam)
    {
        var link = Link(Caller, stripWam);
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(link.Bundle!);
        var sol = e.Query("main(X).");
        Assert.True(sol.Success);
        Assert.Equal("from_helper", sol["X"]!.ToString());
    }

    [Fact]
    public void ThePrivateItReachesKeepsAStandaloneForm()
    {
        var link = Link(Caller);
        Assert.Contains(new QualifiedPredicateRef("qplib", "helper_p", 1),
            link.ExternallyReachableSeeds);
    }

    [Fact]
    public void AQualifiedCallToAPredicateTheModuleDoesNotDefineStillFails()
    {
        var link = Link("""
            :- module(qpapp, [main/1]).
            main(X) :- qplib:nowhere(X).
            """);
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics,
            d => d.Code == "missing_predicate" && d.Message.Contains("does not define nowhere/1"));
    }
}
