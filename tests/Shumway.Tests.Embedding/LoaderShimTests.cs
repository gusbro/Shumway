using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>library(loader)</c> is Scryer's bootstrap module. It used to
/// be a no-op shim, on the grounds that the one predicate real libraries
/// import from it (<c>strip_module/3</c>) is already in the prelude.
///
/// <para>But Scryer's <c>dcgs.pl</c> does not call it bare, it calls
/// <c>loader:strip_module(...)</c>, and a MODULE-QUALIFIED call needs a
/// module of that name to have the predicate. With the no-op there was no
/// such module, and the failure cascaded a long way: dcgs raised
/// <c>existence_error: loader/0</c>, <c>library(atts)</c> failed with it,
/// the <c>attribute</c> operator was therefore never declared, and Scryer's
/// clpz.pl then failed to PARSE at its <c>:- attribute clpz/1, ...</c> line.
/// A whole constraint library lost to an empty shim, with consult reporting
/// no error at all.</para></summary>
public sealed class LoaderShimTests
{
    private static PrologEngine Engine()
        => new() { Warnings = new System.IO.StringWriter() };

    /// <summary>THE CASE: the qualified call dcgs.pl actually makes.</summary>
    [Fact]
    public void TheQualifiedCallResolves()
    {
        var e = Engine();
        e.ConsultString(":- use_module(library(loader), [strip_module/3]).");
        Assert.True(e.Query("loader:strip_module(m:g, M, G), M == m, G == g.").Success,
            "loader:strip_module/3 does not resolve: dcgs.pl cannot load");
    }

    /// <summary>Its semantics are the prelude's, which is what dcgs expects:
    /// an unqualified goal keeps its default module.</summary>
    [Fact]
    public void AnUnqualifiedGoalDefaultsToUser()
    {
        var e = Engine();
        e.ConsultString(":- use_module(library(loader), [strip_module/3]).");
        Assert.True(e.Query("loader:strip_module(plain, M, G), M == user, G == plain.").Success);
        // A bound module is left alone rather than overwritten.
        Assert.True(e.Query("loader:strip_module(plain, mine, G), G == plain.").Success);
    }

    /// <summary>The import list works too, so the bare name is available to
    /// an importer that asked for it -- dcgs.pl imports it explicitly even
    /// though it then calls it qualified.</summary>
    [Fact]
    public void TheImportedNameIsUsableUnqualified()
    {
        var e = Engine();
        e.ConsultString(
            ":- use_module(library(loader), [strip_module/3]).\n"
            + "p(M, G) :- strip_module(a:b, M, G).");
        Assert.True(e.Query("p(M, G), M == a, G == b.").Success);
    }

    /// <summary>ANTI-VACUITY for the cascade: without a resolvable
    /// loader:strip_module/3 the error is the one that started it all, so
    /// this test names what regressing would look like.</summary>
    [Fact]
    public void TheModuleReallyExistsRatherThanBeingANoOp()
    {
        var e = Engine();
        e.ConsultString(":- use_module(library(loader)).");
        // A no-op shim leaves NO module, and the qualified call then raises
        // existence_error on loader/0 rather than failing.
        Assert.True(e.Query(
            "catch(loader:strip_module(x:y, _, _), E, true), var(E).").Success,
            "loader is a no-op again: the qualified call raises");
    }
}
