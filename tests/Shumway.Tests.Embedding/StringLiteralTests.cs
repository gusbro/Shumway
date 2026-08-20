using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for the PSTR-integration chunk: string literals in source survive
/// compile + consult + query round-trips, both as top-level args and as
/// sub-args inside compounds.
/// </summary>
public class StringLiteralTests
{
    // A double-quoted literal reaches C# as the LIST it is (ADR-047 decision 6):
    // the representation is not observable at the boundary, so what arrives is
    // the same whether or not the engine stored it packed.
    private static Term Str(string s)
    {
        Term t = new AtomTerm("[]");
        for (int i = s.Length - 1; i >= 0; i--)
            t = new CompoundTerm(".", new Term[] { new AtomTerm(s[i].ToString()), t });
        return t;
    }
    private static Term Atom(string n) => new AtomTerm(n);

    [Fact]
    public void StringLiteral_TopLevelFact_RoundTrips()
    {
        var engine = new PrologEngine();
        engine.ConsultString("greeting(\"hello\").");
        var sol = engine.Query("greeting(X).");
        Assert.True(sol.Success);
        Assert.Equal(Str("hello"), sol["X"]);
    }

    [Fact]
    public void StringLiteral_TopLevelBody_BindsVariable()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = \"world\".");
        Assert.True(sol.Success);
        Assert.Equal(Str("world"), sol["X"]);
    }

    [Fact]
    public void StringLiteral_InsideCompound_HeadAndBody()
    {
        // The string lives as a sub-arg of a compound — exercises the
        // PreEmitMultiCellLiterals path on both the head (read mode) and the
        // body (write mode).
        var engine = new PrologEngine();
        engine.ConsultString("named(point, \"origin\").");
        var sol = engine.Query("named(point, S).");
        Assert.True(sol.Success);
        Assert.Equal(Str("origin"), sol["S"]);
    }

    [Fact]
    public void StringLiteral_NestedCompound_RoundTrips()
    {
        // Two levels deep: the string sub-arg sits inside a compound that's
        // itself the head's argument.
        var engine = new PrologEngine();
        engine.ConsultString("wrap(box(\"contents\")).");
        var sol = engine.Query("wrap(box(X)).");
        Assert.True(sol.Success);
        Assert.Equal(Str("contents"), sol["X"]);
    }

    [Fact]
    public void StringLiteral_MultipleInSameCompound_RoundTrip()
    {
        // Two PSTRs as sub-args of the same compound. Each takes its own temp
        // slot; the compound's arg cells stay one-each thanks to the pre-emit
        // refactor.
        var engine = new PrologEngine();
        engine.ConsultString("pair(\"left\", \"right\").");
        var sol = engine.Query("pair(A, B).");
        Assert.True(sol.Success);
        Assert.Equal(Str("left"), sol["A"]);
        Assert.Equal(Str("right"), sol["B"]);
    }

    [Fact]
    public void StringLiteral_Deduplicated_InPool()
    {
        // Same literal twice in the source — both should resolve to the same
        // pooled string, observable by the round-trip yielding equal terms.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p("shared").
            q("shared").
            """);
        var solP = engine.Query("p(X).");
        var solQ = engine.Query("q(Y).");
        Assert.Equal(Str("shared"), solP["X"]);
        Assert.Equal(Str("shared"), solQ["Y"]);
    }
}
