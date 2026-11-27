using System.Security.Cryptography;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-076, decisions 1 and 3: a <c>.shum</c> and a <c>.shmo</c> carry, after
/// the stamp, a rules version and the recipe of their build, readable with
/// the stamp of another format. The recipe holds the inputs in order, the
/// sources with their digest and time, and the options that change the
/// product; a field it does not know is skipped.
/// </summary>
public sealed class BundleRecipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_recipe_" + Guid.NewGuid().ToString("N"));

    public BundleRecipeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BundleRecipe EveryField() => new()
    {
        Tool = "shumway-link",
        Module = "m",
        Inputs = new[]
        {
            new RecipeInput { Kind = RecipeInputKind.Source, Name = "a.pl" },
            new RecipeInput
            {
                Kind = RecipeInputKind.Object, Name = "b.shmo",
                Recipe = new BundleRecipe { Tool = "shumway-compile", Module = "b", Debug = true },
            },
            new RecipeInput { Kind = RecipeInputKind.Archive, Name = "c.shum" },
        },
        Sources = new[] { RecipeSource.Of("a.pl", new byte[] { 1, 2, 3 }, new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc)) },
        Consult = true, Library = true,
        Entries = new[] { new PredicateRef("main", 0), new PredicateRef("a/b", 2) },
        Goal = "main", AllowUndefined = true, Strip = true, CompiledIl = true, StripWam = true,
        Wasm = true, Stdlib = true, PrunePrelude = true, RegionPrune = false,
        DynamicLibraries = new[] { "lib" }, ForeignAssemblies = new[] { "f.dll" },
        NativeLibraries = new[] { "n.dll" }, Debug = true, WithSource = true, ArityCompat = true,
        Dialect = "scryer",
    };

    [Fact]
    public void ARecipe_ReadsBackAsItWasWritten()
    {
        byte[] bytes = EveryField().ToBytes();
        BundleRecipe back = BundleRecipe.FromBytes(bytes);
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal("a/b", back.Entries[1].Name);
        Assert.Equal(RecipeInputKind.Object, back.Inputs[1].Kind);
        Assert.Equal("b", back.Inputs[1].Recipe!.Module);
        Assert.False(back.RegionPrune);
        Assert.Equal(new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc), back.Sources[0].WrittenUtc);
    }

    [Fact]
    public void AFieldARecipeDoesNotKnow_IsSkipped()
    {
        byte[] bytes = EveryField().ToBytes();
        var later = new byte[bytes.Length + 8];
        Array.Copy(bytes, later, bytes.Length);
        later[bytes.Length] = 200;                 // a tag of a later engine
        BitConverter.GetBytes(3).CopyTo(later, bytes.Length + 1);
        Assert.Equal(bytes, BundleRecipe.FromBytes(later).ToBytes());
    }

    private static byte[] Object(string module)
        => ShmoWriter.ToBytes(ShmoCompiler.CompileSource($":- module({module}, [p/0]).\np.\n", module)
            .WithRecipe(new BundleRecipe { Tool = "shumway-compile", Module = module }));

    [Fact]
    public void AnArchive_ListsItsMembers_WithTheRecipeEachCarries()
    {
        byte[] archive = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("m1.shmo", Object("m1")),
            new BundleArchiveMember("m2.shmo", Object("m2")),
        });
        BundleRecipe recipe = BundleReader.FromBytes(archive).Recipe!;
        Assert.Equal("shumway-lib", recipe.Tool);
        Assert.Equal(new[] { "m1.shmo", "m2.shmo" }, recipe.Inputs.Select(i => i.Name));
        Assert.Equal(new[] { "m1", "m2" }, recipe.Inputs.Select(i => i.Recipe!.Module));
    }

    // The header is read alone: the stamp of another format does not stop it,
    // and a file of the version before the recipe has none.
    [Fact]
    public void TheRecipe_IsReadWithTheStampOfAnotherFormat()
    {
        byte[] archive = Librarian.CreateArchive(new[] { new BundleArchiveMember("m1.shmo", Object("m1")) });
        archive[9] ^= 0x5A;
        Assert.False(BundleReader.IsCurrent(archive));
        var header = BundleFormat.HeaderRecipe(archive);
        Assert.NotNull(header);
        Assert.Equal(BundleRecipe.CurrentRules, header!.Value.Rules);
        Assert.Equal("m1", header.Value.Recipe!.Inputs[0].Recipe!.Module);

        byte[] obj = Object("m1");
        obj[12] ^= 0x01;
        Assert.Equal("m1", BundleFormat.HeaderRecipe(obj)!.Value.Recipe!.Module);

        BitConverter.GetBytes(6u).CopyTo(archive, 4);
        Assert.Null(BundleFormat.HeaderRecipe(archive));
    }

    // A rules version this engine does not read: the number, no recipe.
    [Fact]
    public void ARulesVersionThisEngineDoesNotRead_GivesNoRecipe()
    {
        byte[] archive = Librarian.CreateArchive(new[] { new BundleArchiveMember("m1.shmo", Object("m1")) });
        BitConverter.GetBytes(99u).CopyTo(archive, 16);
        var header = BundleFormat.HeaderRecipe(archive);
        Assert.Equal(99, header!.Value.Rules);
        Assert.Null(header.Value.Recipe);
        Bundle read = BundleReader.FromBytes(archive);
        Assert.Equal(99, read.RecipeRules);
        Assert.Null(read.Recipe);
        Assert.Single(read.ArchiveMembers);
    }

    [Fact]
    public void ALink_RecordsItsInputsInOrder_ItsSources_AndItsOptions()
    {
        string a = Path.Combine(_dir, "a.pl");
        File.WriteAllText(a, @"
greet :- write(hello), nl.
");
        var written = new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, written);
        string b = Path.Combine(_dir, "b.shmo");
        File.WriteAllBytes(b, Object("b"));

        var plan = new LinkPlan
        {
            Library = true,
            Goal = "greet",
            AllowUndefined = true,
            StripSource = true,
            PrunePrelude = true,
            RegionPrune = false,
            Report = _ => { },
        };
        LinkConfig config = plan.Prepare(new[] { b, a }, out bool usage)!;
        Assert.False(usage);
        var link = ShmoLinker.Link(config);
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));

        BundleRecipe recipe = BundleReader.FromBytes(link.Bytes!).Recipe!;
        Assert.Equal("shumway-link", recipe.Tool);
        Assert.Equal(new[] { (RecipeInputKind.Object, "b.shmo"), (RecipeInputKind.Source, "a.pl") },
            recipe.Inputs.Select(i => (i.Kind, i.Name)));
        Assert.Equal("b", recipe.Inputs[0].Recipe!.Module);
        RecipeSource source = Assert.Single(recipe.Sources);
        Assert.Equal("a.pl", source.Name);
        using (var sha = SHA256.Create()) Assert.Equal(sha.ComputeHash(File.ReadAllBytes(a)), source.Digest);
        Assert.Equal(written, source.WrittenUtc);
        Assert.True(recipe.Library && recipe.AllowUndefined && recipe.Strip && recipe.PrunePrelude);
        Assert.False(recipe.RegionPrune);
        Assert.False(recipe.Consult);
        Assert.Equal("greet", recipe.Goal);
    }
}
