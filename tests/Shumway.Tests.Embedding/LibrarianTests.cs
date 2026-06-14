using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The shumway-lib librarian: assembling a runnable <c>.shum</c> out
/// of chosen <c>.shmo</c> objects without linking / pruning, plus the archive
/// operations (list / add / remove / extract) and the first-class archive
/// section in the bundle format.</summary>
public sealed class LibrarianTests
{
    private static byte[] Shmo(string source, string module,
        ShmoBuildMode mode = ShmoBuildMode.Debug)
        => ShmoWriter.ToBytes(ShmoCompiler.CompileSource(source, module, mode));

    private static BundleArchiveMember Member(string source, string module,
        ShmoBuildMode mode = ShmoBuildMode.Debug)
        => new($"{module}.shmo", Shmo(source, module, mode));

    private const string GreetSrc =
        ":- module(greet).\n:- public hello/1.\nhello(N) :- write(hello(N)), nl.\n";
    private const string AppSrc =
        ":- module(app).\n:- public run/1.\nrun(N) :- hello(N).\n";

    [Fact]
    public void Create_ThenRead_RoundTripsMembersByteIdentical()
    {
        byte[] greet = Shmo(GreetSrc, "greet");
        byte[] app = Shmo(AppSrc, "app");
        byte[] archive = Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("greet.shmo", greet),
            new BundleArchiveMember("app.shmo", app),
        });

        var members = Librarian.ReadArchive(archive);
        Assert.Equal(2, members.Count);
        // Verbatim: extract reproduces the exact input bytes.
        Assert.Equal(greet, members.Single(m => m.FileName == "greet.shmo").ShmoBytes);
        Assert.Equal(app, members.Single(m => m.FileName == "app.shmo").ShmoBytes);
    }

    [Fact]
    public void Create_ProducesNoLinkerEntries_OnlyArchiveMembers()
    {
        byte[] archive = Librarian.CreateArchive(new[] { Member(GreetSrc, "greet") });
        var bundle = BundleReader.FromBytes(archive);
        Assert.Empty(bundle.Entries);
        Assert.Single(bundle.ArchiveMembers);
    }

    [Fact]
    public void LoadBundle_RunsCrossModuleCall_WithoutLinking_Debug()
    {
        byte[] archive = Librarian.CreateArchive(new[]
        {
            Member(GreetSrc, "greet"),
            Member(AppSrc, "app"),
        });
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(archive));
        // run/1 lives in module 'app', hello/1 is public in module 'greet':
        // the cross-module call resolves at load with no link step.
        Assert.True(engine.Query("run(world).").Success);
    }

    [Fact]
    public void LoadBundle_RunsCrossModuleCall_WithoutLinking_ReleaseSourceStripped()
    {
        byte[] archive = Librarian.CreateArchive(new[]
        {
            Member(GreetSrc, "greet", ShmoBuildMode.Release),
            Member(AppSrc, "app", ShmoBuildMode.Release),
        });
        // Release strips source — load goes through the bytecode path, not a
        // re-consult, yet the cross-module public call still resolves.
        var member = Librarian.ReadArchive(archive)
            .Select(Librarian.Parse).First(o => o.ModuleName == "app");
        Assert.Equal("", member.Source);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(archive));
        Assert.True(engine.Query("run(world).").Success);
    }

    [Fact]
    public void AddMembers_ExtendsArchive()
    {
        byte[] archive = Librarian.CreateArchive(new[] { Member(GreetSrc, "greet") });
        byte[] grown = Librarian.AddMembers(archive, new[] { Member(AppSrc, "app") });
        var modules = Librarian.ReadArchive(grown)
            .Select(Librarian.ModuleNameOf).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "app", "greet" }, modules);
    }

    [Fact]
    public void RemoveModules_DropsNamed_ReportsMissing()
    {
        byte[] archive = Librarian.CreateArchive(new[]
        {
            Member(GreetSrc, "greet"),
            Member(AppSrc, "app"),
        });
        byte[] trimmed = Librarian.RemoveModules(
            archive, new[] { "greet", "nope" }, out var removed, out var notFound);
        Assert.Equal(new[] { "greet" }, removed);
        Assert.Equal(new[] { "nope" }, notFound);
        Assert.Equal(new[] { "app" },
            Librarian.ReadArchive(trimmed).Select(Librarian.ModuleNameOf).ToArray());
    }

    [Fact]
    public void Create_RejectsDuplicateModuleNames()
    {
        var ex = Assert.Throws<LibrarianException>(() =>
            Librarian.CreateArchive(new[]
            {
                new BundleArchiveMember("a.shmo", Shmo(GreetSrc, "greet")),
                new BundleArchiveMember("b.shmo", Shmo(GreetSrc, "greet")),
            }));
        Assert.Contains("duplicate module", ex.Message);
    }

    [Fact]
    public void AddMembers_RejectsCollisionWithExisting()
    {
        byte[] archive = Librarian.CreateArchive(new[] { Member(GreetSrc, "greet") });
        Assert.Throws<LibrarianException>(() =>
            Librarian.AddMembers(archive, new[] { Member(GreetSrc, "greet") }));
    }

    [Fact]
    public void ArchiveOps_OnLinkedBundle_ThrowCleanly()
    {
        // A linked (shumway-link) bundle has Entries but no archive members.
        var linked = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(GreetSrc, "greet"),
                ShmoCompiler.CompileSource(AppSrc, "app"),
            },
            EntryPoints = new[] { new PredicateRef("run", 1) },
        });
        Assert.True(linked.Success);
        Assert.Empty(BundleReader.FromBytes(linked.Bytes!).ArchiveMembers);

        var ex = Assert.Throws<LibrarianException>(() =>
            Librarian.AddMembers(linked.Bytes!, new[] { Member(GreetSrc, "lone") }));
        Assert.Contains("linked bundle", ex.Message);
    }

    [Fact]
    public void LinkedBundle_StillRoundTripsThroughReader_WithArchiveSection()
    {
        // The linker's SerialiseBundle now writes the (empty) archive section;
        // a linked bundle must still load and run.
        var linked = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(GreetSrc, "greet"),
                ShmoCompiler.CompileSource(AppSrc, "app"),
            },
            EntryPoints = new[] { new PredicateRef("run", 1) },
        });
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(linked.Bytes!));
        Assert.True(engine.Query("run(world).").Success);
    }

    [Fact]
    public void BothWriters_EmitTheArchiveSection_Identically()
    {
        // BundleWriter.ToBytes (default flags) and the round-trip through
        // BundleReader agree on the archive section.
        var members = new[] { Member(GreetSrc, "greet"), Member(AppSrc, "app") };
        var bundle = new Bundle(System.Array.Empty<BundleEntry>(),
            foreignAssemblies: null, snapshot: null, archiveMembers: members);
        byte[] viaWriter = BundleWriter.ToBytes(bundle);
        var rt = BundleReader.FromBytes(viaWriter);
        Assert.Equal(2, rt.ArchiveMembers.Count);
        Assert.Equal(members[0].ShmoBytes,
            rt.ArchiveMembers.Single(m => m.FileName == "greet.shmo").ShmoBytes);
    }
}
