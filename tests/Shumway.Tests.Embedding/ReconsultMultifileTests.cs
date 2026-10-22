using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Reloading a file replaces what it gave a multifile predicate and
/// nothing else: the clauses other files gave it and the asserted ones stay,
/// and the file's new clauses land after them. reconsult/1 abolished the whole
/// predicate, so reloading one file took every other file's clauses with it.</summary>
public sealed class ReconsultMultifileTests : IDisposable
{
    private const string Decl = ":- multifile t/1.\n:- dynamic t/1.\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-mf-{Guid.NewGuid():N}");

    public ReconsultMultifileTests()
    {
        Directory.CreateDirectory(_dir);
        Write("a.pl", Decl + "t(a1).\nt(a2).\n");
        Write("b.pl", Decl + "t(b1).\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    private string PathOf(string name) => Path.Combine(_dir, name).Replace(Path.DirectorySeparatorChar, '/');

    private static void AssertList(PrologEngine e, string goal, string expected)
        => Assert.True(e.Query($"findall(X, {goal}, L), L == {expected}.").Success,
            $"{goal} is not {expected}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReloadTakesBackOnlyItsOwnClauses(bool throughTheApi)
    {
        var e = new PrologEngine();
        e.ConsultFile(PathOf("a.pl"));
        e.ConsultFile(PathOf("b.pl"));
        Assert.True(e.Query("assertz(t(x)).").Success);
        Write("a.pl", Decl + "t(a3).\n");
        if (throughTheApi) e.ReconsultFile(PathOf("a.pl"));
        else Assert.True(e.Query($"reconsult('{PathOf("a.pl")}').").Success);
        AssertList(e, "t(X)", "[b1,x,a3]");
    }

    [Fact]
    public void AFileThatNoLongerGivesClausesTakesItsOldOnesBack()
    {
        var e = new PrologEngine();
        e.ConsultFile(PathOf("a.pl"));
        e.ConsultFile(PathOf("b.pl"));
        Write("a.pl", "q(1).\n");
        e.ReconsultFile(PathOf("a.pl"));
        AssertList(e, "t(X)", "[b1]");
    }

    /// <summary>The way Logtalk's testers load an entity: twice in one query,
    /// into the same intermediate file.</summary>
    [Fact]
    public void TwoReloadsInOneQueryLeaveOneCopy()
    {
        var e = new PrologEngine();
        e.ConsultFile(PathOf("b.pl"));
        string a = PathOf("a.pl");
        Assert.True(e.Query(
            $"reconsult('{a}'), reconsult('{a}'), findall(X, t(X), L), L == [b1,a1,a2].").Success,
            "inside the query");
        AssertList(e, "t(X)", "[b1,a1,a2]");
    }

    [Fact]
    public void AQueryEnumeratingThePredicateStopsSeeingTheWithdrawnClauses()
    {
        var e = new PrologEngine();
        e.ConsultFile(PathOf("a.pl"));
        e.ConsultFile(PathOf("b.pl"));
        Write("a.pl", Decl + "t(a3).\n");
        Assert.True(e.Query(
            $"t(_), reconsult('{PathOf("a.pl")}'), findall(X, t(X), L), L == [b1,a3], !.").Success);
    }

    /// <summary>A module file loaded again replaces its module, and what it
    /// gave multifile predicates with it (ADR-055).</summary>
    [Fact]
    public void AModuleFileLoadedAgainTakesBackWhatItGave()
    {
        Write("m.pl", ":- module(m, []).\n" + Decl + "t(m1).\n:- multifile user:h/1.\nuser:h(m1).\n");
        var e = new PrologEngine();
        e.ConsultFile(PathOf("b.pl"));
        e.ConsultFile(PathOf("m.pl"));
        e.ConsultFile(PathOf("m.pl"));
        AssertList(e, "t(X)", "[b1,m1]");
        AssertList(e, "h(X)", "[m1]");
    }

    /// <summary>ensure_loaded/1 reloads a file that changed; inside a query the
    /// rest of the query sees the reload, as it sees reconsult/1's.</summary>
    [Fact]
    public void AChangedFileEnsureLoadedInAQueryIsSeenByTheRestOfIt()
    {
        string a = PathOf("a.pl");
        Write("a.pl", Decl + "t(a1).\ns(1).\n");
        var e = new PrologEngine();
        e.ConsultFile(PathOf("b.pl"));
        Assert.True(e.Query($"ensure_loaded('{a}'), s(1).").Success);
        Write("a.pl", Decl + "t(a3).\ns(2).\n");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "a.pl"), DateTime.UtcNow.AddMinutes(1));
        Assert.True(e.Query(
            $"t(_), ensure_loaded('{a}'), s(X), X == 2, findall(Y, t(Y), L), L == [b1,a3], !.").Success);
    }

    [Fact]
    public void APredicateTheFileDoesNotDeclareMultifileIsStillReplacedWhole()
    {
        Write("c.pl", ":- dynamic d/1.\nd(1).\n");
        var e = new PrologEngine();
        e.ConsultFile(PathOf("c.pl"));
        Assert.True(e.Query("assertz(d(9)).").Success);
        e.ReconsultFile(PathOf("c.pl"));
        AssertList(e, "d(X)", "[1]");
    }

    [Fact]
    public void ReloadedTextReplacesWhatTextGaveAndKeepsTheFiles()
    {
        var e = new PrologEngine();
        e.ConsultFile(PathOf("b.pl"));
        e.ConsultString(Decl + "t(s1).\n");
        e.ReconsultString(Decl + "t(s2).\n");
        AssertList(e, "t(X)", "[b1,s2]");
    }
}
