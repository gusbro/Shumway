namespace Shumway.Tests.Wasm;

/// <summary>Arithmetic RPN chains (a_eval_*) and float literals in the wasm
/// backend. The a_eval sequence is the engine's eval-stack machine simulated
/// at compile time over i64 locals: a deopt anywhere in a sequence rewinds to
/// its first push, which is sound because pushes are read-only. Floats are
/// the two-cell encoding with the double's bits baked from the literal
/// pool.</summary>
public class WasmArithFloatTests
{
    private const string Corpus = """
        poly(X, R) :- R is X*X + 3*X.
        sq(X, R) :- R is (X + 1) * (X - 1).
        halfd(X, R) :- R is X // 2.
        modd(X, Y, R) :- R is X mod Y.
        negv(X, R) :- R is -X.
        sig(X, S) :- S is sign(X).
        bigger(X, Y) :- X * X > Y.

        pi(3.14159).
        pi2(3.14159).
        pi3(2.5).
        pitwice :- pi(X), pi2(X).
        notpi :- pi(X), pi3(X).
        zero(0.0).
        z2(-0.0).
        zneg :- zero(Z), z2(Z).
        """;

    private static WasmProgramHarness Harness() => new(Corpus);

    [Theory]
    [InlineData(5, 40)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    public void AnRpnChainEvaluates(long x, long expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("poly", x, null));
        Assert.Equal(expected, h.Answer(1).AsInt);
    }

    [Fact]
    public void ParenthesisedSubexpressions()
    {
        using var h = Harness();
        Assert.True(h.Solve("sq", 7, null));
        Assert.Equal(48, h.Answer(1).AsInt);
    }

    [Theory]
    [InlineData(9, 4)]
    [InlineData(-9, -4)]     // // truncates toward zero
    public void IntegerDivisionTruncates(long x, long expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("halfd", x, null));
        Assert.Equal(expected, h.Answer(1).AsInt);
    }

    [Theory]
    [InlineData(7, 3, 1)]
    [InlineData(-7, 3, 2)]   // ISO mod: sign of the divisor
    public void ModFollowsTheDivisor(long x, long y, long expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("modd", x, y, null));
        Assert.Equal(expected, h.Answer(2).AsInt);
    }

    [Fact]
    public void UnaryOperators()
    {
        using var h = Harness();
        Assert.True(h.Solve("negv", 5, null));
        Assert.Equal(-5, h.Answer(1).AsInt);
        Assert.True(h.Solve("sig", -7, null));
        Assert.Equal(-1, h.Answer(1).AsInt);
        Assert.True(h.Solve("sig", 0, null));
        Assert.Equal(0, h.Answer(1).AsInt);
        Assert.True(h.Solve("sig", 42, null));
        Assert.Equal(1, h.Answer(1).AsInt);
    }

    [Theory]
    [InlineData(4, 15, true)]
    [InlineData(4, 16, false)]
    [InlineData(-4, 15, true)]
    public void AnArithmeticComparisonOverAChain(long x, long y, bool holds)
    {
        using var h = Harness();
        Assert.Equal(holds, h.Solve("bigger", x, y));
    }

    [Fact]
    public void AFloatFactBindsItsArgument()
    {
        using var h = Harness();
        Assert.True(h.Solve("pi", (long?)null));
        Assert.Equal(3.14159, h.AnswerFloat(0));
    }

    [Fact]
    public void AFloatMatchesItselfAndNotAnother()
    {
        // pitwice: write mode in pi/1 (allocate + bind), read mode in pi2/1
        // (reconstruct the bits, compare against the baked constant).
        using var h = Harness();
        Assert.True(h.Solve("pitwice"));
        Assert.False(h.Solve("notpi"));
    }

    [Fact]
    public void NegativeZeroIsZero()
    {
        // ISO has ONE zero: -0.0 is born as 0.0 (the MakeFloat funnel), so
        // the two literals unify.
        using var h = Harness();
        Assert.True(h.Solve("zneg"));
    }

    [Fact]
    public void TheEngineAgrees()
    {
        var engine = new Shumway.Embedding.PrologEngine();
        engine.ConsultString(Corpus);
        Assert.True(engine.Query(
            "poly(5, 40), sq(7, 48), halfd(-9, -4), modd(-7, 3, 2), sig(-7, -1).").Success);
        Assert.True(engine.Query("pitwice, zneg, bigger(4, 15).").Success);
        Assert.False(engine.Query("notpi.").Success);
        Assert.False(engine.Query("bigger(4, 16).").Success);
    }
}
