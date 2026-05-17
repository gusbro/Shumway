using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class ArithmeticTests
{
    private static Term Int(long v) => new IntTerm(v);
    private static Term Flt(double v) => new FloatTerm(v);

    // ---------- is/2 — leaf literals ----------

    [Fact]
    public void Is_PositiveInteger()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 42.");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["X"]);
    }

    [Fact]
    public void Is_NegativeInteger()
    {
        // -3 parses as the compound -(3); is/2 evaluates → -3.
        var engine = new PrologEngine();
        var sol = engine.Query("X is -3.");
        Assert.True(sol.Success);
        Assert.Equal(Int(-3), sol["X"]);
    }

    [Fact]
    public void Is_FloatLiteral()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 3.14.");
        Assert.True(sol.Success);
        Assert.Equal(Flt(3.14), sol["X"]);
    }

    // ---------- is/2 — basic operations ----------

    [Fact]
    public void Is_Addition()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 2 + 3.");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["X"]);
    }

    [Fact]
    public void Is_Subtraction()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 10 - 7.");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }

    [Fact]
    public void Is_Multiplication()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 6 * 7.");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["X"]);
    }

    [Fact]
    public void Is_RealDivision_AlwaysProducesFloat()
    {
        // / always uses float division in our implementation.
        var engine = new PrologEngine();
        var sol = engine.Query("X is 10 / 4.");
        Assert.True(sol.Success);
        Assert.Equal(Flt(2.5), sol["X"]);
    }

    [Fact]
    public void Is_IntegerDivision_TruncatesTowardZero()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 7 // 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }

    [Fact]
    public void Is_Modulo_SignedByDivisor()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is -7 mod 3.");
        Assert.True(sol.Success);
        Assert.Equal(Int(2), sol["X"]);                  // ISO: sign of divisor
    }

    [Fact]
    public void Is_Remainder_SignedByDividend()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is -7 rem 3.");
        Assert.True(sol.Success);
        Assert.Equal(Int(-1), sol["X"]);                 // C-style remainder
    }

    [Fact]
    public void Is_OperatorPrecedence()
    {
        // 1 + 2 * 3 = 1 + 6 = 7 (since * binds tighter than +).
        var engine = new PrologEngine();
        var sol = engine.Query("X is 1 + 2 * 3.");
        Assert.True(sol.Success);
        Assert.Equal(Int(7), sol["X"]);
    }

    [Fact]
    public void Is_UnaryMinus()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is -(5 + 3).");
        Assert.True(sol.Success);
        Assert.Equal(Int(-8), sol["X"]);
    }

    [Fact]
    public void Is_Abs()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is abs(-7).");
        Assert.True(sol.Success);
        Assert.Equal(Int(7), sol["X"]);
    }

    [Fact]
    public void Is_MinMax()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3), engine.Query("X is min(3, 7).")["X"]);
        Assert.Equal(Int(7), engine.Query("X is max(3, 7).")["X"]);
    }

    [Fact]
    public void Is_FloatPromotion()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 1 + 2.5.");
        Assert.True(sol.Success);
        Assert.Equal(Flt(3.5), sol["X"]);
    }

    // ---------- is/2 — variables and nested ----------

    [Fact]
    public void Is_NestedExpression()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is (1 + 2) * (3 + 4).");
        Assert.True(sol.Success);
        Assert.Equal(Int(21), sol["X"]);
    }

    [Fact]
    public void Is_PreviouslyBoundVariable()
    {
        // X is bound earlier, then used as a value in the next is.
        var engine = new PrologEngine();
        var sol = engine.Query("X = 5, Y is X * 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(10), sol["Y"]);
    }

    [Fact]
    public void Is_UnboundRightSide_RaisesInstantiationError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("X is Y."));
        Assert.Equal("instantiation_error", ex.Kind);
    }

    [Fact]
    public void Is_AtomRightSide_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<InvalidOperationException>(() => engine.Query("X is foo."));
    }

    [Fact]
    public void Is_DivisionByZero_RaisesEvaluationError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("X is 1 / 0."));
        Assert.Equal("evaluation_error", ex.Kind);
        Assert.Equal("zero_divisor", ex.Detail);
    }

    [Fact]
    public void Is_IntegerDivisionByZero_RaisesEvaluationError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("X is 1 // 0."));
        Assert.Equal("evaluation_error", ex.Kind);
        Assert.Equal("zero_divisor", ex.Detail);
    }

    // ---------- Comparison predicates ----------

    [Fact]
    public void ArithEqual_SuccessAndFailure()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("1 + 2 =:= 3.").Success);
        Assert.False(engine.Query("1 + 2 =:= 4.").Success);
        // Cross-type equality: 3 =:= 3.0 should be true (numeric equality).
        Assert.True(engine.Query("3 =:= 3.0.").Success);
    }

    [Fact]
    public void ArithNotEqual()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("1 + 2 =\\= 4.").Success);
        Assert.False(engine.Query("1 + 2 =\\= 3.").Success);
    }

    [Fact]
    public void ArithLess()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("1 < 2.").Success);
        Assert.False(engine.Query("2 < 2.").Success);
        Assert.False(engine.Query("3 < 2.").Success);
    }

    [Fact]
    public void ArithGreater()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("3 > 2.").Success);
        Assert.False(engine.Query("2 > 2.").Success);
        Assert.False(engine.Query("1 > 2.").Success);
    }

    [Fact]
    public void ArithLessOrEqual()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("2 =< 2.").Success);
        Assert.True(engine.Query("1 =< 2.").Success);
        Assert.False(engine.Query("3 =< 2.").Success);
    }

    [Fact]
    public void ArithGreaterOrEqual()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("2 >= 2.").Success);
        Assert.True(engine.Query("3 >= 2.").Success);
        Assert.False(engine.Query("1 >= 2.").Success);
    }

    // ---------- Realistic programs ----------

    [Fact]
    public void Recursive_FactorialFact()
    {
        // factorial(0, 1).
        // factorial(N, F) :- N > 0, M is N - 1, factorial(M, MF), F is N * MF.
        var engine = new PrologEngine();
        engine.ConsultString(
            "factorial(0, 1).\n" +
            "factorial(N, F) :- N > 0, M is N - 1, factorial(M, MF), F is N * MF.\n");

        Assert.Equal(Int(120), engine.Query("factorial(5, F).")["F"]);
        Assert.Equal(Int(3628800), engine.Query("factorial(10, F).")["F"]);
    }

    [Fact]
    public void Recursive_FibonacciFact()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "fib(0, 0).\n" +
            "fib(1, 1).\n" +
            "fib(N, F) :- N > 1, M1 is N - 1, M2 is N - 2, " +
                "fib(M1, F1), fib(M2, F2), F is F1 + F2.\n");

        Assert.Equal(Int(55), engine.Query("fib(10, F).")["F"]);
    }
}
