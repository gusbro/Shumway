using System.Linq;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-041: a dynamic call selects its clause from the predicate the
/// running query's own program has at that address. A query nested in it (a
/// directive of a file it consults) links a program of its own; the selector
/// read that program's address map, so a call of the outer query whose
/// trampoline sat where the nested layout had another dynamic predicate ran
/// that predicate's clause.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class NestedQueryClauseSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-nestsel-{Guid.NewGuid():N}");

    public NestedQueryClauseSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ACallAfterANestedQueryRunsItsOwnPredicate()
    {
        string file = Path.Combine(_dir, "f.pl").Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(file, ":- atom(a).\n");
        const int n = 20;
        string want = "[" + string.Join(",", Enumerable.Range(0, n)) + "]";
        string calls = string.Join(", ", Enumerable.Range(0, n).Select(i => $"(d{i}(X{i}) -> true ; X{i} = none)"));
        string list = "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"X{i}")) + "]";
        // The asserts change q's size, so the nested query's layout moves
        // every predicate after it; some of the sizes put another predicate's
        // trampoline where the outer query has one of its own.
        for (int m = 0; m <= 40; m++)
        {
            var source = new StringBuilder(":- dynamic(q/1).\nq(0).\n");
            for (int i = 0; i < n; i++) source.Append($":- dynamic(d{i}/1).\nd{i}({i}).\n");
            var e = new PrologEngine();
            e.ConsultString(source.ToString());
            var sol = e.Query($"retractall(q(_)), forall(between(1, {m}, K), assertz(q(K))), consult('{file}'), "
                + $"{calls}, L = {list}, term_to_atom(L, A), (L == {want} -> R = ok ; R = A).");
            Assert.True(sol.Success, $"m = {m}: the query failed");
            Assert.Equal("ok", sol["R"]?.ToString());
        }
    }

    /// <summary>What a nested query adds that this query can call is linked
    /// into this query's own program and recorded in its own chain table: a
    /// predicate the consulted file defines, or one its directive asserts, is
    /// still selected with no choice point.</summary>
    [Theory]
    [InlineData("nd", "b", true)]
    [InlineData("nd", "b", false)]
    [InlineData("na", "y", true)]
    [InlineData("na", "y", false)]
    public void APredicateTheNestedQueryAddsIsSelected(string name, string arg, bool rebuild)
    {
        string file = Path.Combine(_dir, "g.pl").Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(file, ":- dynamic(nd/1).\nnd(a).\nnd(b).\n:- assertz(na(x)), assertz(na(y)).\n:- atom(a).\n");
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(q/1).\nq(0).\n");
        int fid = Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern(name, permanent: true).Id, 1);
        bool prev = DynamicCodePatcher.DynSelDiag;
        DynamicCodePatcher.DynSelDiag = true;
        try
        {
            DynamicCodePatcher.ResetSelCounters(fid);
            // With the retract and assert the nested query links a program of
            // its own; without them it runs on this one's persistent region.
            string pre = rebuild ? "retractall(q(_)), assertz(q(1)), " : "";
            Assert.True(e.Query($"{pre}consult('{file}'), {name}({arg}).").Success);
            Assert.Equal(1, DynamicCodePatcher.SelSole);
        }
        finally
        {
            DynamicCodePatcher.DynSelDiag = prev;
            DynamicCodePatcher.ResetSelCounters();
        }
    }
}
