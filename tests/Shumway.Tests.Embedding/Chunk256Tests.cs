using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 256: listing prints something when the user spelled a
/// predicate that doesn't exist (instead of silently returning
/// <c>true.</c>), and finds local (mangled) predicates by their
/// source-spelling name.
/// </summary>
public class Chunk256Tests
{
    private static string CaptureListing(string source, string query)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query(query);
        return sw.ToString();
    }

    [Fact]
    public void Listing_NameOnly_NoMatch_ShowsMessage()
    {
        var output = CaptureListing(
            ":- public foo/0.\nfoo.\n",
            "listing(pepe).");
        Assert.Contains("no predicate matches pepe", output);
    }

    [Fact]
    public void Listing_Indicator_NotDefined_ShowsMessage()
    {
        var output = CaptureListing(
            ":- public foo/0.\nfoo.\n",
            "listing(pepe/3).");
        Assert.Contains("pepe/3 not defined", output);
    }

    [Fact]
    public void Listing_LocalPredicate_FoundByDisplayName()
    {
        // helper/1 isn't `:- public` so ModuleRewrite stores it as
        // user$helper/1. listing(helper) must still find it.
        var output = CaptureListing(
            ":- public main/0.\n"
            + "main :- helper(X), write(X).\n"
            + "helper(hello).\n"
            + "helper(world).\n",
            "listing(helper).");
        // Head + body must be demangled in the output.
        Assert.Contains("helper(hello)", output);
        Assert.Contains("helper(world)", output);
        Assert.DoesNotContain("user$", output);
    }

    [Fact]
    public void Listing_LocalPredicate_BodyDemanglesNestedCalls()
    {
        // main's body calls helper (local) — must show as
        // `helper(X)` in the listing, not `user$helper(X)`.
        var output = CaptureListing(
            ":- public main/0.\n"
            + "main :- helper(X), write(X).\n"
            + "helper(hello).\n",
            "listing(main).");
        Assert.Contains("main", output);
        Assert.Contains("helper(X)", output);
        Assert.DoesNotContain("user$", output);
    }

    [Fact]
    public void Listing_NoArg_EnumeratesLocalsWithDisplayNames()
    {
        var output = CaptureListing(
            ":- public top/0.\n"
            + "top :- aux(X), write(X).\n"
            + "aux(value).\n",
            "listing.");
        Assert.Contains("top", output);
        Assert.Contains("aux(value)", output);
        Assert.DoesNotContain("user$", output);
    }

    [Fact]
    public void Listing_PublicPredicate_NotAffectedByDemangle()
    {
        // Public predicates aren't mangled — verify they still
        // round-trip correctly through the demangle path (the
        // helper passes non-mangled names through unchanged).
        var output = CaptureListing(
            ":- public ping/1.\nping(pong).\n",
            "listing(ping).");
        Assert.Contains("ping(pong)", output);
    }

    [Fact]
    public void Demangle_HelperRespectsModulePrefix()
    {
        // Direct test of the demangle helper.
        Assert.Equal("helper", PrologEngine.DemangleLocalName("user$helper"));
        Assert.Equal("foo", PrologEngine.DemangleLocalName("mymodule$foo"));
        // No prefix → unchanged.
        Assert.Equal("plain", PrologEngine.DemangleLocalName("plain"));
        // Multiple $: only the first segment is stripped (so a
        // user predicate named `foo$bar` survives).
        Assert.Equal("foo$bar", PrologEngine.DemangleLocalName("user$foo$bar"));
    }
}
