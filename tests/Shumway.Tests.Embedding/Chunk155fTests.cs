using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155f: in-place asserta for extensible-indexed dynamic
/// predicates. The new clause becomes the head of every affected
/// chain. Each chain's old head is demoted (<c>try_me_else</c> →
/// <c>retry_me_else</c> + 4 nops, preserving the <c>&lt;next&gt;</c>
/// operand). The new head chunk is appended at the end of the
/// buffer with <c>try_me_else &lt;old-head&gt;</c>; every pointer
/// slot that referenced the old head — switch_on_term operands,
/// sub-switch table values and default addresses — is redirected
/// to the new head.
/// </summary>
public class Chunk155fTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void Asserta_SameKey_AfterPromotion_PrependedToBucketAndVarChains()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        // Heat.
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("asserta(d(a)).");
        // Bucket: new entry first → returns 2 'a' solutions.
        Assert.Equal(2, e.QueryAll("d(a).").Count());
        // Var chain order: new first, then originals.
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("a"), Atom("b") }, xs);
    }

    [Fact]
    public void Asserta_NewKey_AfterPromotion_NewBucketCreated()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("asserta(d(c)).");
        Assert.True(e.Query("d(c).").Success);
        // Asserta order: new entry first in var chain.
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("c"), Atom("a"), Atom("b") }, xs);
    }

    [Fact]
    public void Asserta_VarArg_PrependedToEveryBucket()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/2.");
        e.Query("assertz(d(a, 1)).");
        e.Query("assertz(d(b, 2)).");
        e.Query("d(a, _).");
        e.Query("d(b, _).");
        e.Query("asserta(d(_, generic)).");
        // For any concrete key, the var-arg result comes first.
        var aSols = e.QueryAll("d(a, X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("generic"), Int(1) }, aSols);
        var bSols = e.QueryAll("d(b, X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("generic"), Int(2) }, bSols);
    }

    [Fact]
    public void Asserta_ThenAssertz_SameKey_Ordering()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("d(1).");
        e.Query("d(1).");
        // After promotion, asserta-then-assertz.
        e.Query("asserta(d(0)).");
        e.Query("assertz(d(2)).");
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        // Var chain order: 0 (asserta'd first), 1 (original), 2 (assertz'd last).
        Assert.Equal(new long[] { 0, 1, 2 }, xs);
    }

    [Fact]
    public void Asserta_MultipleSameKey_Stacks()
    {
        // Several asserta calls to the same key. Each becomes the
        // new head of the bucket chain. Need at least 2 initial
        // clauses to promote to the chunk-155a indexed layout
        // (single-clause dynamic predicates use the chain shortcut).
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(99)).");
        e.Query("d(1).");
        e.Query("d(99).");
        for (int i = 2; i <= 4; i++)
            e.Query($"asserta(d({i})).");
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        // asserta order: last asserta'd is first. Then 1 (original)
        // then 99 (original). So [4, 3, 2, 1, 99].
        Assert.Equal(new long[] { 4, 3, 2, 1, 99 }, xs);
    }

    [Fact]
    public void AssertaThenRetract_OnIndexed_Works()
    {
        // Need 2+ initial clauses for the predicate to use chunk-
        // 155a indexed (vs the single-clause chain shortcut).
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(z)).");
        e.Query("d(a).");
        e.Query("d(z).");
        e.Query("asserta(d(b)).");
        e.Query("retract(d(a)).");
        Assert.False(e.Query("d(a).").Success);
        Assert.True(e.Query("d(b).").Success);
        Assert.True(e.Query("d(z).").Success);
    }
}
