namespace Shumway.Tests.Wasm;

/// <summary>ADR-020 reserved builds and the general unifier in the wasm
/// backend. A reserved build (put_structure_r / put_list_r for non-last
/// nested compound args) is the engine's write-frame cascade replayed at
/// compile time: one heap guard, straight stores at fixed offsets. The
/// general unifier is module function 1 -- a worklist walk above the stack
/// top used by get_value / unify_value when both sides are bound
/// compounds.</summary>
public class WasmReservedUnifyTests
{
    private const string Corpus = """
        mkp(T, T).
        same(X, X).

        wrap(X, R) :- mkp(f(g(X), h(X)), R).
        unwrap(f(g(A), h(B)), A, B).
        rt(X, A, B) :- wrap(X, T), unwrap(T, A, B).

        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        glue  :- app([1,2], [3], R), same(R, [1,2,3]).
        glue2 :- app([1,2], [3], R), same(R, [1,2,4]).
        first3(A) :- app([1,2], [3], R), same([A|_], R).

        deepeq :- mkp(f(1, g(2, [7,8]), h(X)), T1),
                  mkp(f(1, g(2, [7,8]), h(9)), T2),
                  same(T1, T2), nine(X).
        nine(9).

        vartrail :- mkp(p(X, Y), T), same(T, p(1, 2)), one(X), two(Y).
        one(1).
        two(2).

        pick(1).
        pick(2).
        bt(W) :- mkp(q(X), T), pick(V), same(T, q(V)), keep(V, W).
        keep(2, 2).
        """;

    private static WasmProgramHarness Harness() => new(Corpus);

    [Fact]
    public void AReservedBuildRoundTrips()
    {
        // wrap/2 builds f(g(X), h(X)) with put_structure_r (non-last nested
        // args); unwrap/3 reads it back through head matching. The layout
        // must be cell-for-cell what the engine would have built.
        using var h = Harness();
        Assert.True(h.Solve("rt", 7, null, null));
        Assert.Equal(7, h.Answer(1).AsInt);
        Assert.Equal(7, h.Answer(2).AsInt);
    }

    [Fact]
    public void TwoIndependentGroundListsUnify()
    {
        // app builds a THIRD list; same/2's get_value then walks two
        // independently-built cons chains through the general unifier.
        using var h = Harness();
        Assert.True(h.Solve("glue"));
        Assert.False(h.Solve("glue2"));
    }

    [Fact]
    public void TheUnifierBindsThroughAPartialPattern()
    {
        using var h = Harness();
        Assert.True(h.Solve("first3", (long?)null));
        Assert.Equal(1, h.Answer(0).AsInt);
    }

    [Fact]
    public void DeepStructuresCompareAndBind()
    {
        // Nested struct + list on both sides, one hole: the worklist
        // descends, the hole binds, nine/1 checks the binding.
        using var h = Harness();
        Assert.True(h.Solve("deepeq"));
    }

    [Fact]
    public void UnifierBindingsAreVisibleAfterwards()
    {
        using var h = Harness();
        Assert.True(h.Solve("vartrail"));
    }

    [Fact]
    public void UnifierBindingsUndoOnBacktracking()
    {
        // pick/1 leaves a choice point BELOW the q(X) cell; same/2 binds X
        // to 1 through the unifier, keep(1, W) fails, and the retry must
        // find X unbound again -- the unifier's trail entry undone.
        using var h = Harness();
        Assert.True(h.Solve("bt", (long?)null));
        Assert.Equal(2, h.Answer(0).AsInt);
    }

    [Fact]
    public void TheEngineAgrees()
    {
        var engine = new Shumway.Embedding.PrologEngine();
        engine.ConsultString(Corpus);
        Assert.True(engine.Query("rt(7, 7, 7), glue, first3(1), deepeq, vartrail, bt(2).").Success);
        Assert.False(engine.Query("glue2.").Success);
    }
}
