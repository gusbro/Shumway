using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A bundle and an object file carry a stamp of their format, right
/// after their version (<see cref="BundleFormat.Stamp"/>): the instruction set
/// and its encoding, the cell tags, the layouts of the two files. A reader
/// refuses a file with another stamp before it reads anything else. Without
/// it a file of another format was read as this one: a version number that
/// does not move before the first release told the two apart, which is to
/// say nothing did.</summary>
public sealed class BundleStampTests
{
    private const string Source = ":- module(stamped, [p/1]).\np(1).\np(2).\n";

    private static byte[] Object() => ShmoWriter.ToBytes(ShmoCompiler.CompileSource(Source, "stamped"));

    private static byte[] Library() => Librarian.CreateArchive(new[]
    {
        new BundleArchiveMember("stamped.shmo", Object()),
    });

    private static ulong StampOf(byte[] image) => BitConverter.ToUInt64(image, 8);

    [Fact]
    public void ABundleAndAnObjectFile_CarryTheStampAfterTheirVersion()
    {
        byte[] bundle = Library(), obj = Object();

        Assert.Equal(BundleFormat.Stamp, StampOf(bundle));
        Assert.Equal(BundleFormat.Stamp, StampOf(obj));
        Assert.True(BundleReader.IsCurrent(bundle));
        // They load.
        Assert.Single(BundleReader.FromBytes(bundle).ArchiveMembers);
        Assert.Equal("stamped", ShmoReader.FromBytes(obj).ModuleName);
    }

    [Fact]
    public void ABundleWithAnotherStamp_IsRefusedBeforeAnythingElseIsRead()
    {
        byte[] bundle = Library();
        bundle[8] ^= 0xFF;
        // Nothing after the header is looked at: it need not even be there.
        byte[] headerOnly = bundle.AsSpan(0, 16).ToArray();

        Assert.False(BundleReader.IsCurrent(bundle));
        var whole = Assert.Throws<InvalidDataException>(() => BundleReader.FromBytes(bundle));
        var cut = Assert.Throws<InvalidDataException>(() => BundleReader.FromBytes(headerOnly));
        Assert.Contains("another format of compiled code", whole.Message);
        Assert.Contains("Compile its source, or link it, again.", whole.Message);
        Assert.Equal(whole.Message, cut.Message);
    }

    [Fact]
    public void AnObjectFileWithAnotherStamp_IsRefused()
    {
        byte[] obj = Object();
        obj[15] ^= 0x01;

        var refused = Assert.Throws<InvalidDataException>(() => ShmoReader.FromBytes(obj));

        Assert.Contains("another format of compiled code", refused.Message);
    }

    /// <summary>A bundle as they were written before the stamp: its
    /// compression flag and its body sit where the stamp is read. It is
    /// refused as any bundle of another format, not read askew.</summary>
    [Fact]
    public void ABundleWrittenBeforeTheStamp_IsRefusedWithTheSameMessage()
    {
        byte[] bundle = Library();
        byte[] before = new byte[bundle.Length - 8];
        Array.Copy(bundle, 0, before, 0, 8);
        Array.Copy(bundle, 16, before, 8, bundle.Length - 16);

        Assert.True(BundleReader.IsBundle(before));
        Assert.False(BundleReader.IsCurrent(before));
        var refused = Assert.Throws<InvalidDataException>(() => BundleReader.FromBytes(before));
        Assert.Contains("another format of compiled code", refused.Message);
    }

    [Fact]
    public void AFilesFirstBytes_SayWhetherItIsOfThisFormat()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_stamp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string current = Path.Combine(dir, "current.shum"), other = Path.Combine(dir, "other.shum");
            byte[] bundle = Library();
            File.WriteAllBytes(current, bundle);
            bundle[12] ^= 0x10;
            File.WriteAllBytes(other, bundle);
            File.WriteAllBytes(Path.Combine(dir, "empty.shum"), Array.Empty<byte>());

            Assert.True(BundleReader.IsCurrentFile(current));
            Assert.False(BundleReader.IsCurrentFile(other));
            Assert.False(BundleReader.IsCurrentFile(Path.Combine(dir, "empty.shum")));
            Assert.False(BundleReader.IsCurrentFile(Path.Combine(dir, "none.shum")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The stamp and the image of the bundle below, as they are with the
    // format as it is. The instruction set is in the stamp by computation.
    // The wire form of a file's sections is not, and this is what watches
    // it: a change to it changes the image, and the stamp has to be raised
    // by hand (BundleFormat.ComputeStamp, "wire form") before the two are
    // pinned again here.
    private const ulong PinnedStamp = 0xF51E4368E86784FB;
    private const ulong PinnedImage = 0xD13B8FADABB7C24C;

    // A bundle with one source-only entry and nothing compiled: every
    // section of the container, none of the compiler's output.
    private static ulong ImageOfAFixedBundle()
    {
        byte[] image = BundleWriter.ToBytes(new Bundle(new[] { new BundleEntry("fixed", "p(1).\n") }));
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < image.Length; i++)
        {
            // Not the stamp itself, nor the version of the engine that wrote it.
            if (i is >= 8 and < 16 or >= 17 and < 29) continue;
            h = (h ^ image[i]) * prime;
        }
        return h;
    }

    [Fact]
    public void TheWireFormOfABundle_ChangesOnlyWithTheStamp()
    {
        ulong image = ImageOfAFixedBundle();

        Assert.True(BundleFormat.Stamp == PinnedStamp && image == PinnedImage,
            BundleFormat.Stamp == PinnedStamp
                ? "The image of a bundle changed and its stamp did not: a bundle written before "
                  + "this change would be read as one written after. Raise the \"wire form\" mark "
                  + "in BundleFormat.ComputeStamp, then pin here: "
                  + $"PinnedStamp = 0x{BundleFormat.Stamp:X16} (after raising), PinnedImage = 0x{image:X16}."
                : "The format's stamp moved (the instruction set, a cell tag, or a mark raised by "
                  + "hand): every bundle and object file written before is refused from here on. "
                  + $"Pin it: PinnedStamp = 0x{BundleFormat.Stamp:X16}, PinnedImage = 0x{image:X16}.");
    }
}
