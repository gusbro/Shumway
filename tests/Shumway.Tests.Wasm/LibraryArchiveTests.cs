using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Shumway.WebHost;

namespace Shumway.Tests.Wasm;

/// <summary>The Prolog sources a page imports from an archive of a library
/// collection (ADR-070, O3).</summary>
public sealed class LibraryArchiveTests
{
    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] Tar(bool gzip, params (string Name, string Text)[] entries)
        => Tar(gzip, TarEntryFormat.Ustar, entries);

    private static byte[] Tar(bool gzip, TarEntryFormat format, params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (Stream target = gzip ? new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true) : buffer)
        using (var tar = new TarWriter(target, format, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                TarEntry entry = format switch
                {
                    TarEntryFormat.Pax => new PaxTarEntry(TarEntryType.RegularFile, name),
                    TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, name),
                    _ => new UstarTarEntry(TarEntryType.RegularFile, name),
                };
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(entry);
            }
        }
        return buffer.ToArray();
    }

    private static string[] Paths(byte[] archive)
        => LibraryArchive.Sources(archive).Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    [Fact]
    public void AZip_GivesItsSources_WithoutTheDirectoryThatHoldsThemAll()
    {
        byte[] archive = Zip(
            ("lib-1.0/greeting.pl", ":- module(greeting, [])."),
            ("lib-1.0/dcg/basics.pl", ":- module(basics, [])."),
            ("lib-1.0/README.md", "not a source"),
            ("lib-1.0/old.PRO", "% a source too"));

        Assert.Equal(new[] { "dcg/basics.pl", "greeting.pl", "old.PRO" }, Paths(archive));
        Assert.Equal(":- module(greeting, []).",
            LibraryArchive.Sources(archive).Single(s => s.Path == "greeting.pl").Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATar_GzippedOrNot_GivesItsSources(bool gzip)
    {
        byte[] archive = Tar(gzip,
            ("pack/prolog/one.pl", "one."),
            ("pack/prolog/two.prolog", "two."),
            ("pack/LICENSE", "text"));

        // Only the outermost directory is taken off: prolog/ is the library's.
        Assert.Equal(new[] { "prolog/one.pl", "prolog/two.prolog" }, Paths(archive));
    }

    // The archive is read without System.Formats.Tar, which the browser does
    // not have: each format keeps a name past 100 characters its own way.
    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public void ATarEntryWithALongName_KeepsAllOfIt(TarEntryFormat format)
    {
        string deep = string.Join("/", Enumerable.Repeat("directory", 12)) + "/deep.pl";
        byte[] archive = Tar(true, format, ("pack/" + deep, "deep."), ("pack/short.pl", "short."));

        Assert.Equal(new[] { deep, "short.pl" }, Paths(archive));
        Assert.Equal("deep.", LibraryArchive.Sources(archive).Single(s => s.Path == deep).Text);
    }

    [Fact]
    public void SourcesAtDifferentDepths_KeepTheirPaths()
    {
        byte[] archive = Zip(("top.pl", "top."), ("sub/inner.pl", "inner."));

        Assert.Equal(new[] { "sub/inner.pl", "top.pl" }, Paths(archive));
    }

    [Fact]
    public void AnEntryThatLeavesTheArchive_IsLeftOut()
    {
        byte[] archive = Zip(("../outside.pl", "x."), ("/rooted.pl", "y."), ("inside.pl", "z."));

        Assert.Equal(new[] { "inside.pl" }, Paths(archive));
    }

    [Fact]
    public void AByteOrderMark_IsNotPartOfTheText()
    {
        byte[] archive = Zip(("bom.pl", "﻿fact."));

        Assert.Equal("fact.", LibraryArchive.Sources(archive).Single().Text);
    }

    [Fact]
    public void AnArchiveWithNoSource_IsRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => LibraryArchive.Sources(Zip(("README.md", "no"))));
        Assert.Contains("no Prolog sources", ex.Message);
    }

    [Fact]
    public void BytesThatAreNoArchive_AreRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => LibraryArchive.Sources(Encoding.UTF8.GetBytes(":- module(m, []).")));
        Assert.Contains("not a .zip", ex.Message);
    }
}
