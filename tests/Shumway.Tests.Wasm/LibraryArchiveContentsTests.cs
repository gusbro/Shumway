using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Shumway.Embedding;
using Shumway.WebHost;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An archive of a collection may carry, beside the source of a
/// library, the bundle of it compiled ahead (<c>x.shum</c> beside
/// <c>x.pl</c>): a page that imports the archive puts it where it would put
/// its own compile of the library, which then does not happen. It took the
/// sources and left everything else out.</summary>
public class LibraryArchiveContentsTests
{
    private const string Source = ":- module(carried, [c/1]).\nc(1).\n";

    private static byte[] Compiled() => Librarian.CreateArchive(new[]
    {
        new BundleArchiveMember("carried.shmo",
            ShmoWriter.ToBytes(ShmoCompiler.CompileSource(Source, "carried"))),
    });

    private static (string Path, byte[] Data)[] Files(byte[] bundle) => new[]
    {
        ("lib-1.0/carried.pl", Encoding.UTF8.GetBytes(Source)),
        ("lib-1.0/carried.shum", bundle),
        ("lib-1.0/deep/other.pl", Encoding.UTF8.GetBytes("o(1).\n")),
        ("lib-1.0/README.md", Encoding.UTF8.GetBytes("not kept")),
    };

    private static byte[] Zip((string Path, byte[] Data)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in files)
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(data);
            }
        return buffer.ToArray();
    }

    private static byte[] TarGz((string Path, byte[] Data)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
            foreach (var (path, data) in files)
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(data),
                });
        return buffer.ToArray();
    }

    public static TheoryData<string> Kinds => new() { "zip", "tar.gz" };

    private static byte[] Archive(string kind, byte[] bundle)
        => kind == "zip" ? Zip(Files(bundle)) : TarGz(Files(bundle));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheBundleBesideASource_ComesWithIt_AsItsBytes(string kind)
    {
        byte[] bundle = Compiled();

        var contents = LibraryArchive.Read(Archive(kind, bundle));

        // The directory that holds everything is not part of a path.
        Assert.Equal(new[] { "carried.pl", "deep/other.pl" }, contents.Sources.Select(s => s.Path));
        Assert.Equal(Source, contents.Sources[0].Text);
        var (path, bytes) = Assert.Single(contents.Bundles);
        Assert.Equal("carried.shum", path);
        Assert.Equal(bundle, bytes);
        // What a page asks before it puts one in place.
        Assert.True(BundleReader.IsCurrent(bytes));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheSources_AreWhatTheyWere(string kind)
    {
        var sources = LibraryArchive.Sources(Archive(kind, Compiled()));

        Assert.Equal(new[] { "carried.pl", "deep/other.pl" }, sources.Select(s => s.Path));
    }

    [Fact]
    public void AnArchiveOfBundlesAlone_IsACollection()
    {
        byte[] archive = Zip(new[] { ("carried.shum", Compiled()) });

        var contents = LibraryArchive.Read(archive);

        Assert.Empty(contents.Sources);
        Assert.Equal("carried.shum", Assert.Single(contents.Bundles).Path);
    }
}
