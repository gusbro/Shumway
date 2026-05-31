using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- A non-det predicate whose iterator body sets a flag in
// finally so we can observe whether Dispose ran. ----
public partial class C245TrackedCut
{
    public static bool Disposed;
    public static int Yielded;

    [PrologPredicate("c245_tracked/1", NonDeterministic = true)]
    public static IEnumerable<int> Tracked()
    {
        Disposed = false;
        Yielded = 0;
        try
        {
            for (int i = 1; i <= 1000; i++)
            {
                Yielded = i;
                yield return i;
            }
        }
        finally
        {
            Disposed = true;
        }
    }
}

/// <summary>
/// Chunk 245: cut-pruned non-det choice points fire the chunk-244
/// iterator's <c>Dispose</c> deterministically. Without this hook
/// a generator that holds non-managed resources (DB cursors, file
/// handles) would leak until the .NET finalizer runs.
/// </summary>
public class Chunk245Tests
{
    [Fact]
    public void Cut_AfterFirstSolution_DisposesIterator()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C245TrackedCut));
        C245TrackedCut.Disposed = false;
        C245TrackedCut.Yielded = 0;

        var sols = engine.Query<int>("c245_tracked(X), !.", "X").ToList();
        Assert.Equal(new[] { 1 }, sols);
        Assert.Equal(1, C245TrackedCut.Yielded);          // only yielded once
        Assert.True(C245TrackedCut.Disposed,
            "iterator's finally must run when ! prunes its CP");
    }

    [Fact]
    public void Cut_AfterArbitraryMatch_DisposesIterator()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C245TrackedCut));
        C245TrackedCut.Disposed = false;
        C245TrackedCut.Yielded = 0;

        // Find the first X >= 50, cut.
        var sols = engine.Query<int>("c245_tracked(X), X >= 50, !.", "X").ToList();
        Assert.Equal(new[] { 50 }, sols);
        Assert.Equal(50, C245TrackedCut.Yielded);
        Assert.True(C245TrackedCut.Disposed,
            "iterator's finally must run when ! prunes after partial enumeration");
    }

    [Fact]
    public void ArrowOperator_PrunesChoicePoint_DisposesIterator()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C245TrackedCut));
        C245TrackedCut.Disposed = false;
        C245TrackedCut.Yielded = 0;

        // (G -> T ; E) implicitly cuts G's choice points if G
        // succeeds. The first c245_tracked solution is X=1, so the
        // if-then-else commits to T (X = picked); no backtrack
        // through the non-det predicate.
        var sols = engine.Query<int>(
            "(c245_tracked(X) -> Y = X ; Y = 0).", "Y").ToList();
        Assert.Equal(new[] { 1 }, sols);
        Assert.True(C245TrackedCut.Disposed,
            "iterator's finally must run when '->' prunes the CP");
    }

    [Fact]
    public void Exhaustion_StillDisposes_NoDoubleDispose()
    {
        // Sanity check that chunk 244's exhaustion path still works
        // and that the chunk 245 cleanup doesn't somehow double-fire.
        // (Repeated Dispose on a generator iterator is documented
        // as a no-op in the BCL, so even if it did fire twice the
        // test wouldn't catch it — but the test does verify that
        // Disposed is true after a full enumeration.)
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C245TrackedCut));
        // Enumerate everything (no cut).
        var sols = engine.Query<int>("c245_tracked(X), X > 999.", "X").ToList();
        Assert.Equal(new[] { 1000 }, sols);
        Assert.True(C245TrackedCut.Disposed);
    }
}
