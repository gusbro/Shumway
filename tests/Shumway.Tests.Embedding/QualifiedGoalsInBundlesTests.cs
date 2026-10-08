using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A library compiled through the consult resolves a Module:Goal as
/// the consult that loaded it does, and calls it directly. Scryer's atts
/// writes them by the hundred: clpz:put_atts(V, A) inside clpz, and
/// atts:'$get_from_attr_list'(...) in every clause of a module's get_atts.
/// Compiled to ':'/2 each was a meta-call that builds the goal on the heap,
/// and on the wasm tier an exit to the host: clpz from its bundle ran several
/// times slower there than from its source. Measured in heap cells, a
/// deterministic count.</summary>
public sealed partial class QualifiedGoalsInBundlesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"shumway-qualbundle-{Guid.NewGuid():N}");

    public QualifiedGoalsInBundlesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    public sealed partial class Probe
    {
        [PrologPredicate("cells_allocated/1")]
        public static long CellsAllocated(Activation engine) => engine.CellsAllocated;
    }

    private const string Qa = """
        :- module(qa, [own/2, other/2, both/1, not_zero/1]).
        :- use_module(library(qb)).
        % Qualified with its own module.
        own(N, R) :- qa:step(N, 0, R).
        step(0, A, A) :- !.
        step(N, A, R) :- A1 is A + 1, N1 is N - 1, qa:step(N1, A1, R).
        % Qualified with another module: one of its exports, and a builtin
        % that module does not define.
        other(N, R) :- run(N, 0, R).
        run(0, A, A) :- !.
        run(N, A, R) :- qb:inc(A, A1), qb:succ(N1, N), run(N1, A1, R).
        % Control constructs and negation keep their qualification.
        both(X) :- qa:(X > 0, X < 10).
        not_zero(X) :- qa:(\+ X =:= 0).
        """;

    private const string Qb = """
        :- module(qb, [inc/2]).
        inc(X, Y) :- Y is X + 1.
        """;

    /// <summary>qa compiled as WebShumway compiles a library, loaded from its
    /// bundle alone.</summary>
    private PrologEngine FromBundle()
    {
        string root = Path.Combine(_dir, "qa.pl");
        File.WriteAllText(root, Qa);
        File.WriteAllText(Path.Combine(_dir, "qb.pl"), Qb);
        var errors = new List<ShmoCompileError>();
        var compiled = ShmoViaConsult.CompileMany(
            new[] { root }, Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        File.WriteAllBytes(Path.Combine(_dir, "qa.shum"), Librarian.CreateArchive(compiled
            .Select(c => new BundleArchiveMember(c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
            .ToList(), null));
        File.Delete(root);
        File.Delete(Path.Combine(_dir, "qb.pl"));
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.RegisterPredicates<Probe>();
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- use_module(library(qa)).");
        return e;
    }

    [Theory]
    [InlineData("own")]
    [InlineData("other")]
    public void AQualifiedCallBuildsNothing(string loop)
    {
        var e = FromBundle();
        var r = e.Query($"cells_allocated(C0), {loop}(10000, R), cells_allocated(C1), Cells is C1 - C0.");
        Assert.True(r.Success);
        Assert.Equal("10000", r["R"]!.ToString());
        long cells = long.Parse(r["Cells"]!.ToString()!);
        // A fresh variable per argument is all a direct call builds; through
        // ':'/2 every call also builds its qualified goal on the heap.
        Assert.True(cells < 4 * 10000, $"10000 iterations allocated {cells} cells: not called directly");
    }

    [Fact]
    public void QualifiedControlAnswersAsBefore()
    {
        var e = FromBundle();
        Assert.True(e.Query("both(5), \\+ both(20), not_zero(3), \\+ not_zero(0).").Success);
    }
}
