using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 43: full multi-mode <c>length/2</c> and multi-solution
/// <c>sub_atom/5</c>. Both moved into the prelude on top of pure-
/// enumeration <c>$</c>-helper builtins so backtracking comes from
/// the standard WAM choice-point machinery rather than from any
/// stateful builtin trickery.
/// </summary>
public class Chunk43Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ============================================================================
    // length/2 — every existing mode plus the both-free enumeration
    // ============================================================================

    [Fact]
    public void Length_ListBound_CountsElements()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3), engine.Query("length([a, b, c], N).")["N"]);
        Assert.Equal(Int(0), engine.Query("length([], N).")["N"]);
    }

    [Fact]
    public void Length_NBound_GeneratesFreshVars()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("length(L, 3), is_list(L).").Success);
        Assert.True(engine.Query("length(L, 0), L = [].").Success);
    }

    [Fact]
    public void Length_BothBoundGroundCheck()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("length([a, b, c], 3).").Success);
        Assert.False(engine.Query("length([a, b, c], 4).").Success);
    }

    [Fact]
    public void Length_BothFree_EnumeratesInOrder()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("length(L, N).").Take(5).ToList();
        Assert.Equal(5, sols.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal(Int(i), sols[i]["N"]);
        // L grows in step: sols[0]["L"] is [], sols[1]["L"] is [_], etc.
        Assert.Equal(Atom("[]"), sols[0]["L"]);
    }

    // ============================================================================
    // sub_atom/5 — every decomposition becomes a backtrackable solution
    // ============================================================================

    [Fact]
    public void SubAtom_GroundDecomposition_StillSucceeds()
    {
        // The chunk-40 first-solution path is preserved: ground Before+Length
        // gives the unique decomposition that satisfies them.
        var engine = new PrologEngine();
        var sol = engine.Query("sub_atom(hello, 1, 3, After, Sub).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["After"]);
        Assert.Equal(Atom("ell"), sol["Sub"]);
    }

    [Fact]
    public void SubAtom_FindOccurrencesOfSubstring_EnumeratesAll()
    {
        // sub_atom(banana, B, L, A, ana) enumerates both positions where
        // 'ana' occurs (0-indexed: 1 and 3).
        var engine = new PrologEngine();
        var sols = engine.QueryAll("sub_atom(banana, B, _, _, ana).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal(Int(1), sols[0]["B"]);
        Assert.Equal(Int(3), sols[1]["B"]);
    }

    [Fact]
    public void SubAtom_NoMatch_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("sub_atom(banana, _, _, _, zzz).").Success);
    }

    [Fact]
    public void SubAtom_AllDecompositions_EnumeratesEveryTuple()
    {
        // For "ab" we expect (2+1)(2+2)/2 = 6 tuples:
        // (0, 0, 2, ""), (0, 1, 1, "a"), (0, 2, 0, "ab"),
        // (1, 0, 1, ""), (1, 1, 0, "b"),
        // (2, 0, 0, "").
        var engine = new PrologEngine();
        var sols = engine.QueryAll("sub_atom(ab, B, L, A, S).").ToList();
        Assert.Equal(6, sols.Count);
    }

    [Fact]
    public void SubAtom_FindAtCharacterBoundary()
    {
        // Empty substring at position 0.
        var engine = new PrologEngine();
        var sol = engine.Query("sub_atom(abc, 0, 0, 3, '').");
        Assert.True(sol.Success);
    }
}
