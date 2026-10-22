using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a running query consults is linked into its code as it runs. What
/// the host reads from an address in that code (the predicate an error escaped
/// from, the dialect of the calling module) names the predicate written there,
/// not the last one the query was set up with.</summary>
public sealed class LiveLinkAddressMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-livemap-{Guid.NewGuid():N}");

    public LiveLinkAddressMapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string File(string name, string text)
    {
        string path = Path.Combine(_dir, name).Replace(Path.DirectorySeparatorChar, '/');
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AnErrorInCodeConsultedByTheQueryNamesItsPredicate()
    {
        string file = File("live.pl", "lp(X) :- atom_length(X, _).\n");
        var e = new PrologEngine();
        e.ConsultString("q :- true.\n");
        Assert.ThrowsAny<Exception>(() => e.Query($"consult('{file}'), lp(_)."));
        var live = e.LastErrorStackTrace.FirstOrDefault();
        // The next query links lp/1 at setup: the name it gives is the one to match.
        Assert.ThrowsAny<Exception>(() => e.Query("lp(_)."));
        var linked = e.LastErrorStackTrace.FirstOrDefault();
        Assert.EndsWith("lp", linked.Name);
        Assert.Equal(linked, live);
    }

    /// <summary>A module loaded under the swi dialect concatenates numbers as text
    /// (ADR-040); the dialect is read from the caller's address.</summary>
    [Fact]
    public void AModuleConsultedByTheQueryKeepsItsDialect()
    {
        string file = File("m.pl",
            ":- module(m, [srun/2]).\nsh(A, X) :- atom_concat(A, 2, X).\nsrun(A, X) :- sh(A, X).\n");
        var e = new PrologEngine();
        bool ok = e.WithLibraryDialect("swi",
            () => e.Query($"consult('{file}'), srun(1, X), X == '12'.").Success);
        Assert.True(ok);
    }
}
