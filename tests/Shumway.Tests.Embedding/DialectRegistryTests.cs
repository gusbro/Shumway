using System.IO;
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
    public void SwiDirectives_MetaPredicate_And_Autoload_ParseAsNoOps()
    {
        // ADR-040 — SWI-style :- meta_predicate (needs the fx 1150 prefix
        // operator to parse) and :- autoload (a no-op directive) previously
        // blocked an SWI library from loading as the first library (before any
        // op-defining consult). Both are now accepted; the predicate works.
        var e = new PrologEngine();
        e.ConsultString(
            ":- meta_predicate mymap(1, ?), mymap(2, ?, ?).\n"
            + ":- autoload(library(lists), [append/3]).\n"
            + ":- autoload(library(apply)).\n"
            + "mymap(_, []).\n");
        Assert.True(e.Query("mymap(_, []).").Success);
    }

    [Fact]
    public void CompatLibraryLoad_ScopesAndRestoresDoubleQuotes()
    {
        // Component 4 — a pack library is consulted with its dialect's
        // double_quotes, then the flag is restored to whatever the engine had.
        var e = new PrologEngine();
        Assert.True(e.Query("set_prolog_flag(double_quotes, codes).").Success);
        // dcgs is a scryer-pack library (double_quotes = chars while it loads).
        e.ConsultString(":- use_module(library(dcgs)).");
        // The engine's flag is back to what the program set — the pack's chars
        // did not leak out.
        Assert.True(e.Query("current_prolog_flag(double_quotes, codes).").Success);
    }

    [Fact]
    public void SetLibraryDialect_Api()
    {
        var e = new PrologEngine();
        e.SetLibraryDialect("swi");
        Assert.Equal("swi", e.ActiveLibraryDialect);
        Assert.Throws<System.ArgumentException>(() => e.SetLibraryDialect("klingon"));
    }

    [Fact]
    public void DirectorySpec_ParsesTrailingDialectTag_DriveLetterSafe()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway-spec-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // "ab" → [a,b] only if the dir was tagged scryer (chars). The spec
            // form "scryer:<dir>" is a LEADING dialect prefix — drive-letter safe
            // (the "C" of a Windows path is never a dialect).
            File.WriteAllText(Path.Combine(dir, "slib.pl"),
                ":- module(slib, [sval/1]).\nsval(\"ab\").\n");
            var e = new PrologEngine();
            e.SetLibraryDialect("swi");                 // default would be codes
            e.AddLibraryDirectorySpec("scryer:" + dir); // leading tag wins
            e.ConsultString(":- use_module(library(slib)).");
            Assert.True(e.Query("slib:sval([a, b]).").Success);   // parsed as chars
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void DirectorySpec_PlainPath_NoDialect_IsUnaffected()
    {
        // A spec whose leading token (before the first colon) is not a known
        // dialect is added verbatim — a Windows drive letter is never a dialect.
        var e = new PrologEngine();
        e.AddLibraryDirectorySpec(@"C:/some/plain/path");   // "C" not a dialect
        e.AddLibraryDirectorySpec("/unix/plain/path");
        // A leading notadialect: is part of the path, not a tag.
        e.AddLibraryDirectorySpec("notadialect:/dir");
    }

    [Fact]
    public void PerSearchPathDialect_LoadsEachLibraryInItsDirsDialect()
    {
        // D5.2 — two search dirs tagged with different dialects. A library that
        // writes "ab" parses to [a,b] (char atoms) under scryer/chars and to
        // [97,98] (codes) under swi/codes — in the SAME engine. If per-dir
        // threading did not work, both would parse the same (the engine default)
        // and one assertion would fail.
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway-d52-" + System.Guid.NewGuid().ToString("N"));
        string sdir = Path.Combine(dir, "scryerlib");
        string wdir = Path.Combine(dir, "swilib");
        Directory.CreateDirectory(sdir);
        Directory.CreateDirectory(wdir);
        try
        {
            File.WriteAllText(Path.Combine(sdir, "slib.pl"),
                ":- module(slib, [sval/1]).\nsval(\"ab\").\n");
            File.WriteAllText(Path.Combine(wdir, "wlib.pl"),
                ":- module(wlib, [wval/1]).\nwval(\"ab\").\n");

            var e = new PrologEngine();
            e.AddLibraryDirectory(sdir, "scryer");
            e.AddLibraryDirectory(wdir, "swi");
            e.ConsultString(":- use_module(library(slib)).");
            e.ConsultString(":- use_module(library(wlib)).");

            Assert.True(e.Query("slib:sval([a, b]).").Success);      // chars
            Assert.True(e.Query("wlib:wval([0'a, 0'b]).").Success);  // codes (97,98)
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
