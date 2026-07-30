using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-040 Components 1/2/4 — the multi-dialect shim registry, dialect
/// selection, and per-load double_quotes scoping. Coexistence is the default: a
/// name unique to one dialect resolves regardless of the active dialect; the
/// active dialect only disambiguates a name two packs both define.</summary>
public sealed class DialectRegistryTests
{
    [Fact]
    public void ScryerNames_ResolveByDefault_BackwardCompatible()
    {
        // The former flat CompatLibraries entries are the scryer pack; an
        // undeclared dialect still resolves them (registry falls back to any pack).
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(dcgs)).");
        Assert.True(e.Query("phrase(seq([a, b]), [a, b]).").Success);
    }

    [Fact]
    public void UnknownLibrary_StillUnresolved()
    {
        var e = new PrologEngine();
        // Not in any pack, not on the search path → not a compat library.
        Assert.False(e.UseCompatLibrary("no_such_library_anywhere"));
    }

    [Fact]
    public void SwiOnlyName_Resolves_ProvingASecondPackCoexists()
    {
        // `apply` is an SWI-pack name (prelude-covered no-op). It resolves even
        // with no dialect declared — the second pack coexists with scryer.
        var e = new PrologEngine();
        Assert.True(e.UseCompatLibrary("apply"));
    }

    [Fact]
    public void LibraryDialectFlag_SetAndRead()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("current_prolog_flag(library_dialect, auto).").Success);
        Assert.True(e.Query("set_prolog_flag(library_dialect, swi).").Success);
        Assert.True(e.Query("current_prolog_flag(library_dialect, swi).").Success);
        // The read-only ISO `dialect` flag is unaffected (system identity).
        Assert.True(e.Query("current_prolog_flag(dialect, shumway).").Success);
    }

    [Fact]
    public void UnknownDialect_IsADomainError()
    {
        var e = new PrologEngine();
        Assert.Throws<Shumway.Embedding.ShumwayPrologException>(
            () => e.Query("set_prolog_flag(library_dialect, klingon)."));
    }

    [Fact]
    public void SetLibraryDialect_Api()
    {
        var e = new PrologEngine();
        e.SetLibraryDialect("swi");
        Assert.Equal("swi", e.ActiveLibraryDialect);
        Assert.Throws<System.ArgumentException>(() => e.SetLibraryDialect("klingon"));
    }
}
