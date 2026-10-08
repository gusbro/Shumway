using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A library the engine replaces with its own (ADR-040) is replaced
/// whatever form it is found in. The decision reads a marker in the source; a
/// compiled bundle has no source text, so a bundle of a replaced library (one
/// WebShumway's batch compiled before it skipped them) used to load in place of
/// the engine's version.</summary>
public sealed class LibraryNativeOverrideTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-override-{Guid.NewGuid():N}");

    public LibraryNativeOverrideTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "first"));
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "fake"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // SWI's when.pl, recognised by its marker: the engine's when/2 replaces it.
    private const string SwiWhen = """
        :- module(when, [when/2]).
        when(C, G) :- '$eval_when_condition'(C, G).
        """;

    private void BundleOf(string module, string source, string intoDir)
    {
        string src = Path.Combine(_dir, "fake", module + ".pl");
        File.WriteAllText(src, source);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { src }, Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        File.WriteAllBytes(Path.Combine(intoDir, module + ".shum"), Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null));
    }

    [Fact]
    public void ABundleOfAReplacedLibraryDoesNotLoadInItsPlace()
    {
        // The collection's root holds a bundle whose when/2 always fails; its
        // source, under src/, is SWI's.
        BundleOf("when", ":- module(when, [when/2]).\nwhen(_, _) :- fail.\n",
            Path.Combine(_dir, "first"));
        File.WriteAllText(Path.Combine(_dir, "src", "when.pl"), SwiWhen);
        var e = new PrologEngine();
        e.AddLibraryDirectory(Path.Combine(_dir, "first"));
        e.AddLibraryDirectory(Path.Combine(_dir, "src"));
        e.ConsultString(":- use_module(library(when)).");
        Assert.True(e.Query("when(nonvar(X), Y = done), X = 1, Y == done.").Success);
    }

    [Fact]
    public void ScryersBuiltinsIsTheEnginesOwn()
    {
        string src = Path.Combine(_dir, "src", "builtins.pl");
        File.WriteAllText(src, """
            :- module(builtins, [(->)/2]).
            :- non_counted_backtracking (->)/2.
            G1 -> G2 :- control_entry_point((G1 -> G2)).
            """);
        Assert.True(PrologEngine.ProvidesLibraryNatively("builtins", src));

        var e = new PrologEngine();
        var warnings = new StringWriter();
        e.Warnings = warnings;
        e.AddLibraryDirectory(Path.Combine(_dir, "src"), "scryer");
        e.ConsultString(":- use_module(library(builtins)).");
        Assert.Equal("", warnings.ToString());
        Assert.True(e.Query("( true -> X = yes ; X = no ), X == yes.").Success);
    }

    [Fact]
    public void ALibraryOfItsOwnIsNotProvided()
    {
        string src = Path.Combine(_dir, "src", "when.pl");
        File.WriteAllText(src, ":- module(when, [when/2]).\nwhen(_, G) :- call(G).\n");
        Assert.False(PrologEngine.ProvidesLibraryNatively("when", src));
        File.WriteAllText(src, SwiWhen);
        Assert.True(PrologEngine.ProvidesLibraryNatively("when", src));
        Assert.False(PrologEngine.ProvidesLibraryNatively("clpz", src));
    }
}
