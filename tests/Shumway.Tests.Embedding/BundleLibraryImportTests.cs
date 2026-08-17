using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// An export-qualified module imported from a BUNDLE, not from source.
///
/// <para>Compiling a library to a <c>.shum</c> is what makes a slow one
/// usable — Scryer's clpz loads about five times faster that way. It did not
/// work: the module loaded (its operators even arrived, so the program
/// PARSED) but its predicates resolved to nothing, because two things only
/// the source path did were missing. The importer learns which module it
/// imported from "the module this consult declared", which a bundle never
/// sets; and a module's exports are matched against the clauses it defines,
/// which a bundle has none of — it has compiled code.</para>
/// </summary>
public sealed class BundleLibraryImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_libbundle_" + Guid.NewGuid().ToString("N"));

    public BundleLibraryImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Compiles a one-module library to a .shum in the library dir.</summary>
    private string BuildLibrary(string name, string source)
    {
        var obj = ShmoCompiler.CompileSource(source, name);
        byte[] bytes = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember(name + ".shmo", ShmoWriter.ToBytes(obj)),
        });
        string path = Path.Combine(_dir, name + ".shum");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ItsExportedPredicatesAreCallable()
    {
        BuildLibrary("greetlib",
            ":- module(greetlib, [greet/1]).\ngreet(hello).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString("""
            :- use_module(library(greetlib)).
            use(X) :- greet(X).
            """);

        Assert.True(e.Query("use(hello).").Success);
    }

    [Fact]
    public void WhatItDoesNotExportStaysItsOwn()
    {
        BuildLibrary("hidelib",
            ":- module(hidelib, [shown/1]).\nshown(yes).\nhidden(yes).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- use_module(library(hidelib)).");

        Assert.True(e.Query("shown(yes).").Success);
        Assert.False(e.Query("catch(hidden(yes), _, fail).").Success);
    }

    [Fact]
    public void SelectedImportsWorkToo()
    {
        BuildLibrary("piecelib",
            ":- module(piecelib, [one/1, two/1]).\none(1).\ntwo(2).\n");

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- use_module(library(piecelib), [one/1]).");

        Assert.True(e.Query("one(1).").Success);
        Assert.False(e.Query("catch(two(2), _, fail).").Success);
    }
}
