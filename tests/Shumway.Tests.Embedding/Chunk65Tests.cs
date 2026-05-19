using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 65: per-call Warren argument scheduler. Replaces the upfront
/// conservative head-var preservation with a dependency-graph driver
/// that saves only the head-vars needed to break cycles or
/// self-clobbers, then topologically orders arg puts so each
/// <c>unify_value_x</c> / <c>put_value_x</c> reads its source before
/// the source register is overwritten.
///
/// <para>Three save sources, each one minimal:</para>
/// <list type="bullet">
/// <item><description>Forced saves — head-vars referenced at depth ≥ 2
/// inside a top-level compound. Their reads happen in
/// <c>DrainPendingCompounds</c>, after every main put_* has clobbered
/// the arg slots, so the home must be preserved upfront.</description></item>
/// <item><description>Self-loop saves — top-level compound at dst
/// <c>i</c> with a direct flat-var sub-arg whose home is <c>i</c>.
/// <c>put_structure</c> / <c>put_list</c> clobbers X[i] before the
/// inner <c>unify_value_x</c> reads it.</description></item>
/// <item><description>Cycle-breaking saves — when the cross-arg
/// dependency graph has a cycle (e.g. swap <c>bar(Y, X)</c> with
/// X.home=0, Y.home=1), one save breaks the cycle and the rest
/// topo-sorts.</description></item>
/// </list>
///
/// <para>These tests pin the correctness contract; the scheduler is
/// strictly better than the conservative pass it replaces, but
/// observably so only via bytecode size / cycle count, not query
/// results. The bytecode-size tests live in
/// <see cref="Shumway.Tests.Compiler"/>.</para>
/// </summary>
public class Chunk65Tests
{
    [Fact]
    public void Shuffle_SimpleSwap_StillCorrect()
    {
        // foo(X, Y) :- bar(Y, X). The classical swap — Warren needs one
        // cycle-breaking save (of X, the lower home in the 2-cycle).
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public bar/2.\n" +
            "foo(X, Y) :- bar(Y, X).\n" +
            "bar(b, a).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
        Assert.False(engine.Query("foo(b, a).").Success);
    }

    [Fact]
    public void Shuffle_ThreeArgRotate_StillCorrect()
    {
        // foo(X, Y, Z) :- bar(Z, X, Y). A 3-cycle in the dependency
        // graph. Warren breaks it with one save (of X), then topo-sorts
        // to [0, 2, 1] — strictly fewer puts than the conservative
        // pass's 2 saves + 3 puts.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/3.\n" +
            ":- public bar/3.\n" +
            "foo(X, Y, Z) :- bar(Z, X, Y).\n" +
            "bar(c, a, b).\n");
        Assert.True(engine.Query("foo(a, b, c).").Success);
        Assert.False(engine.Query("foo(c, b, a).").Success);
    }

    [Fact]
    public void Shuffle_FourArgRotate_StillCorrect()
    {
        // foo(W, X, Y, Z) :- bar(Z, W, X, Y). A 4-cycle. Warren still
        // breaks with one save.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/4.\n" +
            ":- public bar/4.\n" +
            "foo(W, X, Y, Z) :- bar(Z, W, X, Y).\n" +
            "bar(d, a, b, c).\n");
        Assert.True(engine.Query("foo(a, b, c, d).").Success);
        Assert.False(engine.Query("foo(a, b, c, e).").Success);
    }

    [Fact]
    public void Shuffle_ReadBeforeWrite_NoSaveNeeded()
    {
        // foo(X) :- bar(X, X). X.home=0. Arg 0 is a no-op (flat var at
        // home); arg 1 reads X[0] which is still original (no-op didn't
        // write). Warren emits zero saves.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar(X, X).\n" +
            "bar(7, 7).\n");
        Assert.True(engine.Query("foo(7).").Success);
        Assert.False(engine.Query("foo(8).").Success);
    }

    [Fact]
    public void Shuffle_CompoundSelfLoopAtHome_StillCorrect()
    {
        // foo(X) :- bar([X], X). X.home=0. Arg 0 = [X] at dst 0 — the
        // put_list clobbers X[0] before the inner unify_value_x reads
        // it. Self-loop save of X handles this. Arg 1 reads the saved
        // slot after the rebind.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar([X], X).\n" +
            "bar([7], 7).\n");
        Assert.True(engine.Query("foo(7).").Success);
        Assert.False(engine.Query("foo(8).").Success);
    }

    [Fact]
    public void Shuffle_CompoundCrossDep_OneSaveEnough()
    {
        // foo(X, Y) :- bar([Y], X). X.home=0, Y.home=1. arg 0 = [Y] at
        // dst 0 reads X[1] (no self-loop), writes X[0]. arg 1 = X at
        // dst 1 reads X[0], writes X[1]. Cross-2-cycle: 0 → 1 (from
        // arg 0's read of X[1] = arg 1's dst) and 1 → 0. The
        // conservative pass saves both X (flat-read at position > home)
        // and Y (compound contains Y, conservative rule). Warren breaks
        // the cycle with one save (of X) and topo-sorts the remainder.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public bar/2.\n" +
            "foo(X, Y) :- bar([Y], X).\n" +
            "bar([b], a).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
        Assert.False(engine.Query("foo(b, a).").Success);
    }

    [Fact]
    public void Shuffle_NestedCompoundForcedSave_StillCorrect()
    {
        // foo(X) :- bar([[X]], 7). X.home=0. arg 0 = [[X]] — X lives at
        // depth 2 inside the outer compound. Its unify_value_x fires in
        // DrainPendingCompounds, after every main put_* has clobbered
        // X[0..N-1]. The Warren scheduler force-saves X upfront so the
        // drained read pulls from the safe slot.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar([[X]], 7).\n" +
            "bar([[5]], 7).\n");
        Assert.True(engine.Query("foo(5).").Success);
        Assert.False(engine.Query("foo(6).").Success);
    }

    [Fact]
    public void Shuffle_MixedFlatAndCompoundReads_StillCorrect()
    {
        // foo(X, Y, Z) :- bar([Y, Z], X, Z). X.home=0, Y.home=1,
        // Z.home=2. arg 0 = [Y, Z] reads X[1], X[2] — both safe (no
        // self-loop). arg 1 = X reads X[0]. arg 2 = Z reads X[2].
        // Edges: 0 → 1 (arg 0 writes X[0], arg 1 reads X[0]),
        // 1 → 2 (arg 1 writes X[1], — but Y.home=1, no edge from arg 2
        // reading X[1] since arg 2 reads X[2]).  Actually no cycle —
        // 1 → 0 (from arg 1 reading X[0]) and 0 → 1, 0 → 2,
        // 2 → 2 (self at arg 2)? No, arg 2 reads X[2] and is itself a
        // no-op (Z.home=2 == dst 2). Topo emits cleanly.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/3.\n" +
            ":- public bar/3.\n" +
            "foo(X, Y, Z) :- bar([Y, Z], X, Z).\n" +
            "bar([b, c], a, c).\n");
        Assert.True(engine.Query("foo(a, b, c).").Success);
        Assert.False(engine.Query("foo(a, c, b).").Success);
    }

    [Fact]
    public void Shuffle_VarRepeatedInCompoundAndFlat_StillCorrect()
    {
        // foo(X) :- bar([X], [X]). X.home=0. arg 0 = [X] at dst 0 self-
        // loops (put_list 0 clobbers X[0] before unify_value_x reads).
        // arg 1 = [X] at dst 1 reads X[0] — needs to read original X,
        // but arg 0 already clobbered it. After self-loop save of X,
        // both reads reference the safe slot.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public bar/2.\n" +
            "foo(X) :- bar([X], [X]).\n" +
            "bar([7], [7]).\n");
        Assert.True(engine.Query("foo(7).").Success);
        Assert.False(engine.Query("foo(8).").Success);
    }

    [Fact]
    public void Shuffle_PermanentsBypassScheduler()
    {
        // foo(X, Y) :- bar(Y, X), q(X, Y). X and Y are referenced past
        // the first goal so they're both permanents (Y registers). The
        // scheduler's "in X" filter skips them entirely — no saves, no
        // reordering. The bar/q chain still produces the right answer.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public foo/2.\n" +
            ":- public bar/2.\n" +
            ":- public q/2.\n" +
            "bar(b, a).\n" +
            "q(a, b).\n" +
            "foo(X, Y) :- bar(Y, X), q(X, Y).\n");
        Assert.True(engine.Query("foo(a, b).").Success);
        Assert.False(engine.Query("foo(b, a).").Success);
    }
}
