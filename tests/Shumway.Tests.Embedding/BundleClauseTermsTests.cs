using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A source-less (Release) bundle answers <c>clause/2</c> and
/// <c>listing/1</c> exactly as the consult of the same source does: the
/// linked entry carries the object's raw static clauses (the clause-terms
/// trailer), which the loader files into the module manifest. <c>--strip</c>
/// is the one way to ship a module without them.</summary>
public sealed class BundleClauseTermsTests
{
    private const string Program =
        ":- public foo/1.\n" +
        "foo(1).\n" +
        "foo(X) :- X > 1, bar(X).\n" +
        "bar(_).\n" +
        "main :- foo(1).\n";

    // Body of foo/1's clauses through clause/2, and the listing text, from
    // whichever engine: the bundle must produce the same strings as the consult.
    private const string Probe =
        "findall(B, clause(foo(_), B), Bs), " +
        "with_output_to(atom(L), listing(foo/1)), " +
        "R = Bs-L.";

    private static PrologEngine FromBundle(bool strip)
    {
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("main", 0), new PredicateRef("foo", 1) },
            StripSource = strip,
            BakePrelude = true,
            IncludeCompiledIl = false,
        }).Bytes!;
        return PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
    }

    [Fact]
    public void ReleaseBundle_ClauseAndListing_MatchTheConsult()
    {
        var consulted = new PrologEngine();
        consulted.ConsultString(Program);
        var expected = consulted.Query(Probe);
        Assert.True(expected.Success);
        string expectedText = expected.Bindings["R"].ToString()!;
        // Anti-vacuity: the consult really sees both clauses and prints a rule.
        Assert.Contains("true", expectedText);
        Assert.Contains("bar(", expectedText);
        Assert.Contains("foo(X) :-", expectedText);

        var bundled = FromBundle(strip: false);
        Assert.True(bundled.Query("main.").Success);
        var actual = bundled.Query(Probe);
        Assert.True(actual.Success);
        Assert.Equal(expectedText, actual.Bindings["R"].ToString()!);
    }

    [Fact]
    public void StrippedBundle_HasNoClausesToShow()
    {
        var stripped = FromBundle(strip: true);
        // The predicate still runs...
        Assert.True(stripped.Query("main.").Success);
        // ...but its clauses were not shipped: clause/2 has nothing.
        Assert.False(stripped.Query("clause(foo(_), _).").Success);
    }

    [Fact]
    public void PrivatePredicate_StaysPrivateOnTheConsult()
    {
        // The privacy rule is judged at clause/2 time, over the manifest the
        // bundle now fills the same way; the consult pins the rule itself.
        var consulted = new PrologEngine();
        consulted.ConsultString(Program);
        var r = consulted.Query("catch(clause(bar(_), _), error(E, _), true).");
        Assert.True(r.Success);
        Assert.Equal("permission_error(access, private_procedure, /(bar, 1))",
            r.Bindings["E"].ToString()!);
    }
}
