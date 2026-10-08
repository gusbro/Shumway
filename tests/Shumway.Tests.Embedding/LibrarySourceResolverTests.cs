using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><see cref="PrologEngine.LibrarySourceResolver"/>: when
/// <c>use_module(library(X))</c> resolves to a source, the host may hand
/// another file to load in its place (WebShumway compiles an imported
/// library's bundle the first time a program needs it).</summary>
public sealed class LibrarySourceResolverTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-resolver-{Guid.NewGuid():N}");

    public LibrarySourceResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        Directory.CreateDirectory(Path.Combine(_dir, "built"));
        File.WriteAllText(Path.Combine(_dir, "lib", "vlib.pl"),
            ":- module(vlib, [v/1]).\nv(source).\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A bundle that defines v(bundle), so what loaded is plain.</summary>
    private string BuildBundle()
    {
        string src = Path.Combine(_dir, "built", "vlib.pl");
        File.WriteAllText(src, ":- module(vlib, [v/1]).\nv(bundle).\n");
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { src }, Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        string target = Path.Combine(_dir, "built", "vlib.shum");
        File.WriteAllBytes(target, Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null));
        return target;
    }

    [Fact]
    public void WhatTheResolverReturnsIsWhatLoads()
    {
        string bundle = BuildBundle();
        var asked = new List<(string, string)>();
        var e = new PrologEngine();
        e.AddLibraryDirectory(Path.Combine(_dir, "lib"));
        e.LibrarySourceResolver = (name, source) => { asked.Add((name, source)); return bundle; };
        e.ConsultString(":- use_module(library(vlib)).");
        Assert.True(e.Query("v(bundle).").Success);
        Assert.False(e.Query("v(source).").Success);
        var (askedName, askedSource) = Assert.Single(asked);
        Assert.Equal("vlib", askedName);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "lib", "vlib.pl")), askedSource);

        // Loaded once: a second import asks nothing.
        e.ConsultString(":- use_module(library(vlib)).");
        Assert.Single(asked);
    }

    [Fact]
    public void NullLoadsTheSource()
    {
        int asked = 0;
        var e = new PrologEngine();
        e.AddLibraryDirectory(Path.Combine(_dir, "lib"));
        e.LibrarySourceResolver = (_, _) => { asked++; return null; };
        e.ConsultString(":- use_module(library(vlib)).");
        Assert.True(e.Query("v(source).").Success);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ABundleOnThePathIsNotAskedAbout()
    {
        // WebShumway's layout: the collection's root, holding the bundles, is
        // searched before the sources under it.
        string first = Path.Combine(_dir, "first");
        Directory.CreateDirectory(first);
        File.Copy(BuildBundle(), Path.Combine(first, "vlib.shum"));
        int asked = 0;
        var e = new PrologEngine();
        e.AddLibraryDirectory(first);
        e.AddLibraryDirectory(Path.Combine(_dir, "lib"));
        e.LibrarySourceResolver = (_, _) => { asked++; return null; };
        e.ConsultString(":- use_module(library(vlib)).");
        Assert.Equal(0, asked);
        Assert.True(e.Query("v(bundle).").Success);
    }

    [Fact]
    public void ALibraryTheEngineReplacesIsNotAskedAbout()
    {
        // SWI's when.pl, recognised by its marker, gives way to the engine's own
        // when/2: nothing to compile.
        File.WriteAllText(Path.Combine(_dir, "lib", "when.pl"),
            ":- module(when, [when/2]).\nwhen(C, G) :- '$eval_when_condition'(C, G).\n");
        int asked = 0;
        var e = new PrologEngine();
        e.AddLibraryDirectory(Path.Combine(_dir, "lib"));
        e.LibrarySourceResolver = (_, _) => { asked++; return null; };
        e.ConsultString(":- use_module(library(when)).");
        Assert.Equal(0, asked);
        Assert.True(e.Query("when(nonvar(X), Y = done), X = 1, Y == done.").Success);
    }
}
