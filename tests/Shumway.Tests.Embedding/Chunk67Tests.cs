using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 67 — multi-argument indexing (Phase 2). When A1 is var in the
/// call, the dispatcher falls through to A2's switch, then A3's, etc.,
/// before landing in the full try/retry/trust chain. ADR-007 anticipates
/// this; chunk 67 implements it via the new <c>switch_on_arg</c> /
/// <c>switch_on_*_arg</c> opcodes that take an explicit arg index.
///
/// <para>These tests pin the end-to-end semantics: correct answers when
/// A1 is var (the indexed path); correct answers when A1 is bound and
/// A2 is also bound (the standard arg-0 path); correct answers when
/// every arg is var (the full chain fallback).</para>
/// </summary>
public class Chunk67Tests
{
    [Fact]
    public void Arg1OnlyIndexed_BoundQueryDispatches()
    {
        // All clauses have var arg 0. Arg 1 atom-discriminates.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public color/2.\n" +
            "color(_, red).\n" +
            "color(_, green).\n" +
            "color(_, blue).\n");
        Assert.True(engine.Query("color(anything, red).").Success);
        Assert.True(engine.Query("color(anything, blue).").Success);
        Assert.False(engine.Query("color(anything, purple).").Success);
    }

    [Fact]
    public void Arg1Indexed_UnboundQueryEnumeratesAll()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public color/2.\n" +
            "color(_, red).\n" +
            "color(_, green).\n" +
            "color(_, blue).\n");
        var sols = engine.QueryAll("color(_, _).").ToList();
        Assert.Equal(3, sols.Count);
    }

    [Fact]
    public void BothArgsIndexed_CrossDiscriminates()
    {
        // shape(circle, area). shape(square, area). shape(circle,
        // perimeter). shape(triangle, area). Calling shape(X, perimeter)
        // hits only one clause via arg-1 fallback (arg 0 is var, arg 1
        // bucket "perimeter" → {clause 2}).
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public shape/2.\n" +
            "shape(circle, area).\n" +
            "shape(square, area).\n" +
            "shape(circle, perimeter).\n" +
            "shape(triangle, area).\n");
        Assert.True(engine.Query("shape(circle, area).").Success);
        Assert.True(engine.Query("shape(triangle, area).").Success);
        Assert.False(engine.Query("shape(square, perimeter).").Success);
        // Unbound arg 0, bound arg 1: multi-arg fallback in action.
        var sols = engine.QueryAll("shape(_, perimeter).").ToList();
        Assert.Single(sols);
    }

    [Fact]
    public void Arg1IntegerIndexed_DispatchesByInt()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public size/2.\n" +
            "size(_, 10).\n" +
            "size(_, 20).\n" +
            "size(_, 30).\n");
        Assert.True(engine.Query("size(big_box, 20).").Success);
        Assert.False(engine.Query("size(big_box, 25).").Success);
    }

    [Fact]
    public void Arg1StructIndexed_DispatchesByFunctor()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public meta/2.\n" +
            "meta(_, p(X)) :- X = found_p.\n" +
            "meta(_, q(X)) :- X = found_q.\n");
        var sol1 = engine.Query("meta(thing, p(Y)).");
        Assert.True(sol1.Success);
        Assert.Equal(new Shumway.Compiler.Ast.AtomTerm("found_p"), sol1["Y"]);
        var sol2 = engine.Query("meta(thing, q(Y)).");
        Assert.True(sol2.Success);
        Assert.Equal(new Shumway.Compiler.Ast.AtomTerm("found_q"), sol2["Y"]);
    }

    [Fact]
    public void VarClauseMatchesEveryBucket()
    {
        // mix(_, x) is a var-arg-0, atom-arg-1 clause and matches any
        // call regardless of arg 0 — for arg 1 = x.
        // mix(a, _) is an atom-arg-0, var-arg-1 clause and matches any
        // call with arg 0 = a — for any arg 1.
        // mix(_, _) (var on both) matches everything.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public mix/2.\n" +
            "mix(_, x) :- one.\n" +
            "mix(a, _) :- two.\n" +
            "mix(_, _) :- three.\n" +
            ":- public one/0.\n one.\n" +
            ":- public two/0.\n two.\n" +
            ":- public three/0.\n three.\n");
        // mix(a, x) matches all three.
        Assert.Equal(3, engine.QueryAll("mix(a, x).").Count());
        // mix(b, x) matches clause 1 (var arg 0, atom x) and clause 3 (both var). NOT clause 2 (atom a, not b).
        Assert.Equal(2, engine.QueryAll("mix(b, x).").Count());
        // mix(b, y) matches only clause 3.
        Assert.Single(engine.QueryAll("mix(b, y)."));
    }

    [Fact]
    public void TripleArgs_MultiArgIndexing()
    {
        // All three args could discriminate; calling with one bound at
        // a time exercises each level's switch_on_arg in turn.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public triple/3.\n" +
            "triple(a, x, 1).\n" +
            "triple(a, y, 2).\n" +
            "triple(b, x, 3).\n" +
            "triple(b, y, 4).\n");
        Assert.True(engine.Query("triple(a, x, 1).").Success);
        Assert.True(engine.Query("triple(b, y, 4).").Success);
        Assert.False(engine.Query("triple(a, x, 4).").Success);
        // Unbound arg 0, all-bound rest: multi-arg fallback to args 1, 2.
        Assert.Single(engine.QueryAll("triple(_, y, 2)."));
        // Unbound arg 0 AND arg 1, bound arg 2.
        Assert.Single(engine.QueryAll("triple(_, _, 3)."));
    }

    [Fact]
    public void MixedSparseIndexing_NoArg1ButArg2()
    {
        // Arg 0 all var, arg 1 all var, arg 2 discriminates. Only arg 2
        // gets a switch layer.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public deep/3.\n" +
            "deep(_, _, alpha).\n" +
            "deep(_, _, beta).\n" +
            "deep(_, _, gamma).\n");
        Assert.True(engine.Query("deep(any, thing, beta).").Success);
        Assert.False(engine.Query("deep(any, thing, delta).").Success);
        Assert.Equal(3, engine.QueryAll("deep(_, _, _).").Count());
    }

    [Fact]
    public void IndexingAcrossManyClauses_LargeDispatch()
    {
        // Mid-size fact set to exercise the dictionary path of
        // SwitchTable (threshold is 16). 20 facts with distinct arg-1
        // atom values; all arg 0 var.
        var src = new System.Text.StringBuilder();
        src.AppendLine(":- public k/2.");
        var atoms = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j",
                            "k", "l", "m", "n", "o", "p", "q", "r", "s", "t" };
        foreach (var a in atoms)
            src.AppendLine($"k(_, {a}).");
        var engine = new PrologEngine();
        engine.ConsultString(src.ToString());
        foreach (var a in atoms)
            Assert.True(engine.Query($"k(any, {a}).").Success, $"k(any, {a}) failed");
        Assert.False(engine.Query("k(any, zzz).").Success);
    }
}
