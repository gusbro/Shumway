using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 70 — lazy PSTR concatenation (Phase 2). When both arguments
/// of <c>string_concat/3</c> are PSTRs, the result is built by copying
/// A's logical content into a fresh buffer and pointing the tail cell
/// at B's existing header — no allocation for B's content. The
/// existing chunk-6 PSTR machinery already supported a
/// <see cref="Shumway.Core.Tag.Pstr"/>-tagged tail (the design noted
/// this as the lazy-concat plug-in point); chunk 70 makes the chain
/// actually get built and makes the read paths
/// (<see cref="Shumway.Core.Activation.AsPstrString"/>,
/// <see cref="Shumway.Core.Activation.GetPstrChainLength"/>) follow it.
///
/// <para>The chunk is observably a performance optimisation; these
/// tests pin the correctness contract by checking that lazy-concat
/// results behave identically to eager-concat results under every
/// operation the engine exposes for PSTRs: unification, decomposition,
/// length, conversion to atom, repeated concat, and DCG-style char
/// walking.</para>
/// </summary>
public class Chunk70Tests
{
    [Fact]
    public void TwoPstrs_ConcatProducesCorrectString()
    {
        // The basic shape: build two PSTRs via string literals, concat
        // them, expect the round-trip result.
        var engine = new PrologEngine();
        var sol = engine.Query("string_concat(\"hello \", \"world\", R).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("hello world"), sol["R"]);
    }

    [Fact]
    public void ConcatResult_UnifiesWithEqualGroundPstr()
    {
        // The lazy form (header → buffer of A → tail cell pointing at
        // B's header) and the eager form (single buffer of A++B) must
        // unify as equal strings.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"hello \", \"world\", R), R = \"hello world\".");
        Assert.True(sol.Success);
    }

    [Fact]
    public void ConcatResult_DecomposesIntoCharCodes()
    {
        // Walking the result via [H|T] one char at a time exercises the
        // chunk-6 AdvancePstrHead path across the chain boundary: after
        // the buffer's last code unit, the tail cell hands off to B's
        // header.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"ab\", \"cd\", R), R = [H1, H2, H3, H4].");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm('a'), sol["H1"]);
        Assert.Equal(new IntTerm('b'), sol["H2"]);
        Assert.Equal(new IntTerm('c'), sol["H3"]);
        Assert.Equal(new IntTerm('d'), sol["H4"]);
    }

    [Fact]
    public void ConcatResult_StringLengthFollowsChain()
    {
        // string_length/2 needs the *logical* code-unit count, which on
        // a lazy chain means walking the tail cells.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"hello \", \"world\", R), string_length(R, L).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(11), sol["L"]);
    }

    [Fact]
    public void EmptyLeft_ReturnsRightUnchanged()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_concat(\"\", \"only\", R).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("only"), sol["R"]);
    }

    [Fact]
    public void EmptyRight_ReturnsLeftUnchanged()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_concat(\"only\", \"\", R).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("only"), sol["R"]);
    }

    [Fact]
    public void ChainedConcats_ProduceCorrectCombinedString()
    {
        // string_concat(A, B, AB), string_concat(AB, C, ABC). The first
        // concat builds a lazy chain; the second flattens it (A's
        // content gets re-copied) and starts a new chain with C as the
        // right side. Either way the result must read back correctly.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"foo\", \"bar\", X), " +
            "string_concat(X, \"baz\", Y).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("foobarbaz"), sol["Y"]);
    }

    [Fact]
    public void ConcatResult_RoundTripsThroughAtomString()
    {
        // atom_string/2 reads the PSTR content; a lazy chain must be
        // walked fully so the resulting atom has the merged content.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"green \", \"tea\", S), atom_string(A, S).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("green tea"), sol["A"]);
    }

    [Fact]
    public void MixedAtomAndPstrConcat_FallsBackToEager()
    {
        // When one side is an atom (not a PSTR), the optimisation
        // shouldn't fire — the atom would need to be materialised into
        // a buffer regardless, so chaining a tail at B doesn't save
        // anything. Verify correctness either way.
        var engine = new PrologEngine();
        // hello is an atom (single-quoted), world is a PSTR string.
        var sol = engine.Query("string_concat(hello, \" world\", R).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("hello world"), sol["R"]);
    }

    [Fact]
    public void LargeChain_StillCorrectAcrossManyConcats()
    {
        // Concatenate 10 fragments via repeated string_concat. Each
        // step flattens the prior chain into a new buffer; the final
        // result must still equal the eagerly-joined string.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public build/2.
            build([], "").
            build([H|T], R) :- build(T, RT), string_concat(H, RT, R).
            """);
        var sol = engine.Query(
            "build([\"alpha\", \"beta\", \"gamma\", \"delta\", \"epsilon\"], R).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("alphabetagammadeltaepsilon"), sol["R"]);
    }

    [Fact]
    public void TwoConcats_SecondFlattensFirst()
    {
        // The chunk's documented limitation is that subsequent concats
        // on a lazy result re-copy the left side. This test pins the
        // semantic behaviour (correctness) of that path — the lazy /
        // eager distinction is invisible to the user.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"AAA\", \"BBB\", X), " +
            "string_concat(X, \"CCC\", Y), " +
            "Y = \"AAABBBCCC\".");
        Assert.True(sol.Success);
    }

    [Fact]
    public void ConcatChain_DecomposeIntoListGivesAllChars()
    {
        // string_chars/2 fully materialises the PSTR as a list of
        // 1-char atoms — must walk the chain to produce all chars.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "string_concat(\"ab\", \"cd\", R), R = [H1, H2, H3, H4], " +
            "string_chars(R, [C1, C2, C3, C4]).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm('a'), sol["H1"]);
        Assert.Equal(new IntTerm('d'), sol["H4"]);
        Assert.Equal(new AtomTerm("a"), sol["C1"]);
        Assert.Equal(new AtomTerm("d"), sol["C4"]);
    }
}
