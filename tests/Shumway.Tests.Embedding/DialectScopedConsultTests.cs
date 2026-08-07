using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Consulting a library's SOURCE the way its own system reads it.
///
/// <para>A library from Scryer or SWI means what THAT system says it means —
/// `double_quotes` above all. Reading it as ISO gets it wrong quietly, since
/// most of a file parses either way and only the string literals differ. The
/// loader has always applied the dialect; this is the same thing for a host
/// that consults the text itself, which is what the browser's editor does when
/// a library file is open in it.</para>
/// </summary>
public sealed class DialectScopedConsultTests
{
    private static PrologEngine Engine() => new() { Out = new StringWriter() };

    /// <summary>Scryer reads "abc" as a list of chars; ISO reads it as codes.</summary>
    private const string Probe = "kind(K) :- ( \"a\" = [X], atom(X) -> K = chars ; K = codes ).\n";

    [Fact]
    public void WithoutADialectTheTextIsReadAsIso()
    {
        var e = Engine();
        e.ConsultString(Probe);
        Assert.True(e.Query("kind(codes).").Success);
    }

    [Fact]
    public void UnderScryerTheSameTextReadsAsScryerDoes()
    {
        var e = Engine();
        e.WithLibraryDialect("scryer", () => { e.ConsultString(Probe); return true; });
        Assert.True(e.Query("kind(chars).").Success);
    }

    [Fact]
    public void TheDialectIsScopedToTheLoad()
    {
        var e = Engine();
        e.WithLibraryDialect("scryer", () => { e.ConsultString(Probe); return true; });
        // The flag it set must not leak: a later consult is the user's own
        // program again.
        e.ConsultString("after(K) :- ( \"a\" = [X], atom(X) -> K = chars ; K = codes ).\n");
        Assert.True(e.Query("after(codes).").Success);
    }

    [Fact]
    public void AnUnknownOrEmptyDialectIsNotAnError()
    {
        // The caller passes whatever a library was tagged with, including
        // nothing at all — checking first would be the caller's job otherwise.
        var e = Engine();
        Assert.True(e.WithLibraryDialect("", () => true));
        Assert.True(e.WithLibraryDialect(null, () => true));
        Assert.True(e.WithLibraryDialect("no_such_dialect", () => true));
    }
}
