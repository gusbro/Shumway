using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- Non-det predicates declared at namespace level so the
// generator emits each bridge in the right partial type. ----

public partial class C244Range
{
    [PrologPredicate("c244_range/3", NonDeterministic = true)]
    public static IEnumerable<int> Range(int from, int to)
    {
        for (int i = from; i <= to; i++) yield return i;
    }
}

public partial class C244Words
{
    [PrologPredicate("c244_word/1", NonDeterministic = true)]
    public static IEnumerable<string> Words()
    {
        yield return "alpha";
        yield return "beta";
        yield return "gamma";
    }
}

public partial class C244Empty
{
    [PrologPredicate("c244_nothing/1", NonDeterministic = true)]
    public static IEnumerable<int> Nothing()
    {
        yield break;
    }
}

public partial class C244Tracked
{
    public static bool Disposed;
    public static int LastValue;

    [PrologPredicate("c244_tracked/1", NonDeterministic = true)]
    public static IEnumerable<int> Tracked()
    {
        Disposed = false;
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                LastValue = i;
                yield return i;
            }
        }
        finally
        {
            // Runs on iterator Dispose — exhaustion or GC.
            Disposed = true;
        }
    }
}

// ---- Instance non-det predicate ----
public partial class C244Stateful
{
    private readonly int[] _items;
    public C244Stateful(int[] items) { _items = items; }

    [PrologPredicate("c244_each/1", NonDeterministic = true)]
    public IEnumerable<int> Each()
    {
        foreach (var i in _items) yield return i;
    }
}

/// <summary>
/// Chunk 244: non-deterministic <c>[PrologPredicate]</c>. Method
/// returns <c>IEnumerable&lt;T&gt;</c>; the generator emits an
/// iterator + choice-point bridge so Prolog backtracks into the
/// predicate for additional solutions.
/// </summary>
public class Chunk244Tests
{
    [Fact]
    public void NonDet_EnumeratesAllSolutionsViaBacktrack()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Range));
        var sols = engine.Query<int>("c244_range(1, 5, X).", "X").ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, sols);
    }

    [Fact]
    public void NonDet_EmptyIterator_PredicateFails()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Empty));
        Assert.False(engine.QueryAll("c244_nothing(X).").Any());
    }

    [Fact]
    public void NonDet_FirstSolutionOnly_CutCommits()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Range));
        // ',!' commits after the first match.
        var sols = engine.Query<int>("c244_range(10, 20, X), !.", "X").ToList();
        Assert.Equal(new[] { 10 }, sols);
    }

    [Fact]
    public void NonDet_FindallCollectsAll()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Words));
        // findall round-trips through the non-det iterator.
        var sol = engine.Query("findall(W, c244_word(W), L).");
        var list = sol.Get<List<string>>("L");
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, list);
    }

    [Fact]
    public void NonDet_IteratorDisposedOnExhaustion()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Tracked));
        C244Tracked.Disposed = false;
        // Enumerate to exhaustion — the generator's finally must run.
        var all = engine.Query<int>("c244_tracked(X).", "X").ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, all);
        Assert.True(C244Tracked.Disposed,
            "iterator should be disposed after MoveNext returns false");
    }

    [Fact]
    public void NonDet_FailureMidStream_StillAdvances()
    {
        // Forces the engine to keep backtracking into the non-det
        // predicate until it finds a value matching '3'.
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244Range));
        var sols = engine.Query<int>("c244_range(1, 10, X), X =:= 3, !.", "X").ToList();
        Assert.Equal(new[] { 3 }, sols);
    }

    [Fact]
    public void NonDet_InstanceMethod_CapturesState()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(new C244Stateful(new[] { 100, 200, 300 }));
        var sols = engine.Query<int>("c244_each(X).", "X").ToList();
        Assert.Equal(new[] { 100, 200, 300 }, sols);
    }

    [Fact]
    public void NonDet_TypedReturnIsPrologTerm()
    {
        // The element type is a [PrologTerm] record — exercises the
        // converter pipeline inside the non-det bridge.
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C244PointSource));
        var pts = engine.Query<C244Pt>("c244_pts(P).", "P").ToList();
        Assert.Equal(3, pts.Count);
        Assert.Equal(new C244Pt(0, 0), pts[0]);
        Assert.Equal(new C244Pt(1, 1), pts[1]);
        Assert.Equal(new C244Pt(2, 2), pts[2]);
    }
}

[PrologTerm("c244_pt")]
public partial record C244Pt(int X, int Y);

public partial class C244PointSource
{
    [PrologPredicate("c244_pts/1", NonDeterministic = true)]
    public static IEnumerable<C244Pt> Pts()
    {
        for (int i = 0; i < 3; i++) yield return new C244Pt(i, i);
    }
}
