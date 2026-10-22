using System.Linq;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What the host reads from a code address of a running query (the
/// predicate an error escaped from, the dialect of the calling module) comes
/// from the program that query runs. A query nested in it (a directive of a
/// file it consults) links a program of its own and replaced the host's
/// current address map; read with that one, an address named a predicate of
/// the other program.</summary>
public sealed class NestedQueryAddressMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-addrmap-{Guid.NewGuid():N}");

    public NestedQueryAddressMapTests() => Directory.CreateDirectory(_dir);

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
    public void AnErrorAfterANestedQueryNamesItsOwnPredicate()
    {
        string file = File("f.pl", ":- atom(a).\n");
        var source = new StringBuilder(":- dynamic(q/1).\nq(0).\n");
        for (int i = 0; i < 20; i++) source.Append($":- dynamic(d{i}/1).\nd{i}(X) :- atom_length(X, _).\n");
        // The asserts change q's size, so the nested query's layout moves the
        // predicates after it.
        for (int m = 0; m <= 40; m++)
        {
            var e = new PrologEngine();
            e.ConsultString(source.ToString());
            Assert.ThrowsAny<Exception>(() => e.Query(
                $"retractall(q(_)), forall(between(1, {m}, K), assertz(q(K))), consult('{file}'), d7(_)."));
            var frame = e.LastErrorStackTrace.FirstOrDefault();
            Assert.Equal(("d7", 1), (frame.Name, frame.Arity));
        }
    }

    /// <summary>A module loaded under the swi dialect concatenates numbers as
    /// text (ADR-040); the dialect is read from the caller's address.</summary>
    [Fact]
    public void AfterANestedQueryTheCallerKeepsItsDialect()
    {
        string extra = Path.Combine(_dir, "extra.pl").Replace(Path.DirectorySeparatorChar, '/');
        string directive = File("directive.pl", ":- atom(a).\n");
        // The predicates the first file adds are in the nested query's static
        // region and not in the running query's, which moves the module's code.
        for (int m = 0; m <= 20; m++)
        {
            var lines = new StringBuilder();
            for (int k = 0; k < m; k++) lines.Append($"s{k}(X) :- atom(X), s{k}x(X).\ns{k}x(_).\n");
            System.IO.File.WriteAllText(extra, lines.ToString());
            var e = new PrologEngine();
            e.WithLibraryDialect("swi", () =>
            {
                e.ConsultString(":- module(m, [srun/2]).\nsh(A, X) :- atom_concat(A, 2, X).\nsrun(A, X) :- sh(A, X).\n");
                return true;
            });
            var sol = e.Query($"consult('{extra}'), consult('{directive}'), srun(1, X), X == '12'.");
            Assert.True(sol.Success, $"m = {m}");
        }
    }
}
