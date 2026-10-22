using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a running query consults is linked into its code as it runs. What
/// the host reads from an address in that code (the predicate an error escaped
/// from, the dialect of the calling module) names the predicate written there,
/// not the last one the query was set up with. A clause the query asserts is not
/// linked as a predicate: an address in it reads as a dynamic clause, and the
/// dialect is its caller's.</summary>
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

    private static (string Name, int Arity) FirstFrameOf(PrologEngine e, string goal)
    {
        Assert.ThrowsAny<Exception>(() => e.Query(goal));
        return e.LastErrorStackTrace.FirstOrDefault();
    }

    [Fact]
    public void AnErrorInAClauseTheQueryAsserted_IsNotBlamedOnAnotherPredicate()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(dp/1).\ndp(1).\n:- dynamic(ip/2).\nip(a, 1).\nip(b, 2).\nip(c, 3).\n"
            + "wrap :- ap(_), true.\n");
        var dynamicClause = ("<dynamic clause>", -1);
        // A new dynamic predicate, called from a predicate setup linked.
        Assert.Equal(dynamicClause,
            FirstFrameOf(e, "assertz((ap(X) :- atom_length(X, _))), wrap."));
        // A declared one, behind its first clause.
        Assert.Equal(dynamicClause,
            FirstFrameOf(e, @"assertz((dp(X) :- atom_length(X, _))), dp(Y), Y \== 1."));
        // One whose clauses are indexed on the first argument.
        Assert.Equal(dynamicClause,
            FirstFrameOf(e, "assertz((ip(k, X) :- atom_length(X, _))), ip(k, _)."));
        Assert.DoesNotContain(e.LastErrorStackTrace, f => f.Name.StartsWith("$"));
    }

    /// <summary>The next query links the clauses asserted before it: they are named.</summary>
    [Fact]
    public void AClauseAssertedByAnEarlierQuery_IsNamed()
    {
        var e = new PrologEngine();
        e.ConsultString("q :- true.\n");
        Assert.True(e.Query("assertz((bp(X) :- atom_length(X, _))).").Success);
        Assert.Equal(("bp", 1), FirstFrameOf(e, "bp(_)."));
    }

    /// <summary>The asserted clause comes right after a module the query loaded under the swi
    /// dialect; the clause is the user's, called from the query, and concatenating numbers
    /// is a type error for it.</summary>
    [Fact]
    public void AClauseTheQueryAsserted_TakesItsCallersDialect()
    {
        string file = File("mswi.pl", ":- module(mswi, [mrun/1]).\nmrun(X) :- atom_concat(X, '', _).\n");
        var e = new PrologEngine();
        bool ok = e.WithLibraryDialect("swi", () => e.Query(
            $"consult('{file}'), assertz((u(X) :- atom_concat(1, 2, X))), "
            + "catch(u(R), error(type_error(_, _), _), R = error), R == error.").Success);
        Assert.True(ok);
    }
}
