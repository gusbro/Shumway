using Shumway.Embedding;
using Shumway.WebHost;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>
/// ADR-076, decision 4: the source a page writes beside a library's bundle.
/// The copy the recipe carries comes first, with the time the recipe
/// records, from a bundle of any format; else the text its module carries,
/// a second before the bundle. Either way not newer than the bundle, which
/// stays the library.
/// </summary>
public sealed class SourceToWriteTests
{
    private const string Text = ":- module(lib, [p/0]).\np.\n";
    private static readonly DateTime Recorded = new(2026, 10, 3, 9, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime BundleTime = new(2026, 11, 27, 18, 0, 0, DateTimeKind.Utc);

    private static byte[] Archive(bool carry, ShmoBuildMode mode)
    {
        var obj = ShmoCompiler.CompileSource(Text, "lib", mode).WithRecipe(new BundleRecipe
        {
            Tool = "shumway-compile",
            Module = "lib",
            Sources = new[] { RecipeSource.Of("lib.pl", System.Text.Encoding.UTF8.GetBytes(Text), Recorded, carry) },
        });
        return Librarian.CreateArchive(new[] { new BundleArchiveMember("lib.shmo", ShmoWriter.ToBytes(obj)) });
    }

    [Fact]
    public void TheCopyTheRecipeCarries_WithTheTimeItRecords_InAnyFormat()
    {
        byte[] bundle = Archive(carry: true, ShmoBuildMode.Release);
        bundle[9] ^= 0x44;
        var (text, written) = LibraryArchive.SourceToWrite(bundle, "lib", BundleTime)!.Value;
        Assert.Equal(Text, text);
        Assert.Equal(Recorded, written);
    }

    [Fact]
    public void WithoutACopy_TheModulesText_ASecondBeforeTheBundle()
    {
        // A debug build keeps the module's text.
        byte[] bundle = Archive(carry: false, ShmoBuildMode.Debuggable);
        var (text, written) = LibraryArchive.SourceToWrite(bundle, "lib", BundleTime)!.Value;
        Assert.StartsWith(":- module(lib", text);
        Assert.Equal(BundleTime.AddSeconds(-1), written);
    }

    [Fact]
    public void ABundleThatCarriesNoText_GivesNone()
        => Assert.Null(LibraryArchive.SourceToWrite(Archive(carry: false, ShmoBuildMode.Release), "lib", BundleTime));
}
