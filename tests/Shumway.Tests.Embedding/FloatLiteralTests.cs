using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for float literals in source: chunk-15 added a float literal pool
/// alongside the existing string pool and the <c>get_float</c> / <c>put_float</c>
/// opcodes. Floats inside compounds go through the same pre-emit pattern as
/// strings so the compound's arg slots stay single-cell.
/// </summary>
public class FloatLiteralTests
{
    private static Term Flt(double v) => new FloatTerm(v);
    private static Term Atom(string n) => new AtomTerm(n);

    [Fact]
    public void FloatLiteral_TopLevelFact_RoundTrips()
    {
        var engine = new PrologEngine();
        engine.ConsultString("pi(3.14).");
        var sol = engine.Query("pi(X).");
        Assert.True(sol.Success);
        Assert.Equal(Flt(3.14), sol["X"]);
    }

    [Fact]
    public void FloatLiteral_HeadMatches_OnEqualValue()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(3.14).");
        // Match in the head — exercises get_float.
        var matched = engine.Query("p(3.14).");
        Assert.True(matched.Success);
        var notMatched = engine.Query("p(2.71).");
        Assert.False(notMatched.Success);
    }

    [Fact]
    public void FloatLiteral_TopLevelBody_BindsVariable()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = 2.5.");
        Assert.True(sol.Success);
        Assert.Equal(Flt(2.5), sol["X"]);
    }

    [Fact]
    public void FloatLiteral_InsideCompound_HeadAndBody()
    {
        // Float lives as a sub-arg of a compound — exercises
        // PreEmitMultiCellLiterals on both sides.
        var engine = new PrologEngine();
        engine.ConsultString("named(pi, 3.14).");
        var sol = engine.Query("named(pi, V).");
        Assert.True(sol.Success);
        Assert.Equal(Flt(3.14), sol["V"]);
    }

    [Fact]
    public void FloatLiteral_NestedCompound_RoundTrips()
    {
        var engine = new PrologEngine();
        engine.ConsultString("constant(physics(1.0e8)).");
        var sol = engine.Query("constant(physics(X)).");
        Assert.True(sol.Success);
        Assert.Equal(Flt(1.0e8), sol["X"]);
    }

    [Fact]
    public void FloatLiteral_MultipleInSameCompound_RoundTrip()
    {
        var engine = new PrologEngine();
        engine.ConsultString("range(0.5, 9.5).");
        var sol = engine.Query("range(Lo, Hi).");
        Assert.True(sol.Success);
        Assert.Equal(Flt(0.5), sol["Lo"]);
        Assert.Equal(Flt(9.5), sol["Hi"]);
    }

    [Fact]
    public void FloatLiteral_MixedWithIntInCompound_OrderingPreserved()
    {
        // The int between the floats stays at its arg slot; the floats are
        // pre-emitted to temps without disturbing the int's position.
        var engine = new PrologEngine();
        engine.ConsultString("triple(1.5, 2, 3.5).");
        var sol = engine.Query("triple(A, B, C).");
        Assert.True(sol.Success);
        Assert.Equal(Flt(1.5), sol["A"]);
        Assert.Equal(new IntTerm(2), sol["B"]);
        Assert.Equal(Flt(3.5), sol["C"]);
    }
}
