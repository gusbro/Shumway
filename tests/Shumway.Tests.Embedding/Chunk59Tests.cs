using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 59: small Phase-1 closures.
///   - <c>between/3</c> now enumerates every integer in the range
///     via the runtime CP machinery (chunk 56's pattern).
///   - <c>read_term/2</c> is registered as a stream-reading alias
///     of the existing <c>read_term_from_stream/2</c>.
///   - <c>string_concat/3</c> gains the (?, ?, +) split mode so a
///     ground combined string decomposes into every prefix/suffix.
/// </summary>
public class Chunk59Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ============================================================================
    // between/3 multi-solution
    // ============================================================================

    [Fact]
    public void Between_EnumeratesAllIntegersInRange()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("between(1, 5, X).").ToList();
        Assert.Equal(5, sols.Count);
        Assert.Equal(Int(1), sols[0]["X"]);
        Assert.Equal(Int(5), sols[4]["X"]);
    }

    [Fact]
    public void Between_GroundX_ChecksMembership()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("between(1, 10, 5).").Success);
        Assert.False(engine.Query("between(1, 10, 11).").Success);
    }

    [Fact]
    public void Between_EmptyRange_NoSolutions()
    {
        var engine = new PrologEngine();
        Assert.Empty(engine.QueryAll("between(10, 5, _).").ToList());
    }

    [Fact]
    public void Between_BacktrackingFindsFirstSatisfying()
    {
        var engine = new PrologEngine();
        // First X in [1..10] where X * X > 30 → X = 6.
        var sol = engine.Query(
            "between(1, 10, X), Y is X * X, Y > 30.");
        Assert.True(sol.Success);
        Assert.Equal(Int(6), sol["X"]);
    }

    // ============================================================================
    // read_term/2 (ISO alias for stream-reading)
    // ============================================================================

    [Fact]
    public void ReadTerm_AliasesReadTermFromStream()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "foo(bar, 42).\n");
            var engine = new PrologEngine();
            var sol = engine.Query(
                "open('" + path.Replace("\\", "/") + "', read, S), " +
                "read_term(S, T), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(
                new CompoundTerm("foo", new Term[] { Atom("bar"), Int(42) }),
                sol["T"]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ============================================================================
    // string_concat/3 (?, ?, +) split mode
    // ============================================================================

    [Fact]
    public void StringConcat_NonDet_SplitsString()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("string_concat(A, B, \"hi\").").ToList();
        // "hi" has 3 splits: ("", "hi"), ("h", "i"), ("hi", "").
        Assert.Equal(3, sols.Count);
    }

    [Fact]
    public void StringConcat_NonDet_FindsKnownSuffix()
    {
        var engine = new PrologEngine();
        // Find the prefix of "abcde" that, when paired with "de", makes
        // the whole thing. The split mode enumerates until it finds "abc".
        var sol = engine.Query("string_concat(A, \"de\", \"abcde\").");
        Assert.True(sol.Success);
        // A should be "abc" as a PSTR. We can verify via string_length.
        Assert.Equal(Int(3), engine.Query(
            "string_concat(A, \"de\", \"abcde\"), string_length(A, L).")["L"]);
    }
}
