using Shumway.Embedding;
using Shumway.WebHost;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A library that comes to a page as its bundle alone gets the
/// source the bundle carries of it written beside it: a module compiled in
/// debug mode keeps its text, and the link keeps it unless it strips the
/// bundle. The module is the bundle's only one, or the one named as the
/// library is.</summary>
public sealed class BundleSourceTests
{
    private const string Alpha = ":- module(alpha, [a/1]).\n% the comment stays\na(1).\n";
    private const string Beta = ":- module(beta, [b/1]).\nb(2).\n";

    private static ShmoObject Compiled(string source, string module, ShmoBuildMode mode)
        => ShmoCompiler.CompileSource(source, module, mode);

    private static byte[] Linked(bool strip, params ShmoObject[] objects)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
            StripSource = strip,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        return BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true);
    }

    private static byte[] Archived(params ShmoObject[] objects)
        => Librarian.CreateArchive(objects
            .Select(o => new BundleArchiveMember(o.ModuleName + ".shmo", ShmoWriter.ToBytes(o)))
            .ToList());

    [Fact]
    public void ALinkOfAModuleCompiledInDebugMode_CarriesItsText()
    {
        byte[] bundle = Linked(false, Compiled(Alpha, "alpha", ShmoBuildMode.Debug));
        Assert.Equal(Alpha, LibraryArchive.SourceOfBundle(bundle, "alpha"));
        // Its only module is the library's, whatever the library is called.
        Assert.Equal(Alpha, LibraryArchive.SourceOfBundle(bundle, "sub/renamed"));
    }

    [Fact]
    public void AnArchiveOfModulesCompiledInDebugMode_CarriesTheirText()
    {
        byte[] bundle = Archived(Compiled(Alpha, "alpha", ShmoBuildMode.Debug),
                                 Compiled(Beta, "beta", ShmoBuildMode.Debug));
        Assert.Equal(Beta, LibraryArchive.SourceOfBundle(bundle, "beta"));
        Assert.Equal(Alpha, LibraryArchive.SourceOfBundle(bundle, "lib/alpha"));
    }

    [Fact]
    public void OfSeveralModules_NoneNamedAsTheLibrary_IsNoSource()
    {
        byte[] bundle = Linked(false, Compiled(Alpha, "alpha", ShmoBuildMode.Debug),
                               Compiled(Beta, "beta", ShmoBuildMode.Debug));
        Assert.Null(LibraryArchive.SourceOfBundle(bundle, "gamma"));
        Assert.Equal(Alpha, LibraryArchive.SourceOfBundle(bundle, "alpha"));
    }

    [Theory]
    [InlineData(false, false)]   // compiled in release mode: no text
    [InlineData(true, true)]     // in debug mode, and stripped
    public void NoTextCarried_IsNoSource(bool debug, bool strip)
    {
        var mode = debug ? ShmoBuildMode.Debug : ShmoBuildMode.Release;
        byte[] bundle = Linked(strip, Compiled(Alpha, "alpha", mode));
        Assert.Null(LibraryArchive.SourceOfBundle(bundle, "alpha"));
    }

    [Fact]
    public void BytesThisEngineCannotRead_AreNoSource()
    {
        byte[] bundle = Linked(false, Compiled(Alpha, "alpha", ShmoBuildMode.Debug));
        bundle[9] ^= 0xFF;                       // another stamp
        Assert.Null(LibraryArchive.SourceOfBundle(bundle, "alpha"));
        Assert.Null(LibraryArchive.SourceOfBundle(new byte[32], "alpha"));
    }
}
