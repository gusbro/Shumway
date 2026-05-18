using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §9 Arithmetic. Covers the Phase-1 subset: <c>is/2</c>,
/// the arithmetic comparison family (<c>=:=</c>, <c>=\=</c>, <c>&lt;</c>,
/// <c>&gt;</c>, <c>=&lt;</c>, <c>&gt;=</c>), and the standard evaluable
/// functions Shumway implements (chunk 44 added the bitwise / trig /
/// rounding set).
/// </summary>
public class ArithmeticConformance
{
    private static Term Int(long v) => new IntTerm(v);

    // ---------- is/2 ----------

    [Fact]
    public void Is_Addition() => AssertBinding("X is 1 + 2.", "X", Int(3));

    [Fact]
    public void Is_Subtraction() => AssertBinding("X is 10 - 4.", "X", Int(6));

    [Fact]
    public void Is_Multiplication() => AssertBinding("X is 6 * 7.", "X", Int(42));

    [Fact]
    public void Is_IntegerDivision() => AssertBinding("X is 17 // 5.", "X", Int(3));

    [Fact]
    public void Is_Modulo() => AssertBinding("X is 17 mod 5.", "X", Int(2));

    [Fact]
    public void Is_RemainderSignsDifferentFromMod()
    {
        // ISO: `mod` carries the sign of the divisor; `rem` carries the
        // sign of the dividend. (-7 mod 3) = 2 but (-7 rem 3) = -1.
        var engine = new PrologEngine();
        Assert.Equal(Int(2), engine.Query("X is -7 mod 3.")["X"]);
        Assert.Equal(Int(-1), engine.Query("X is -7 rem 3.")["X"]);
    }

    [Fact]
    public void Is_UnaryMinus() => AssertBinding("X is -(5).", "X", Int(-5));

    [Fact]
    public void Is_Abs() => AssertBinding("X is abs(-12).", "X", Int(12));

    [Fact]
    public void Is_MinMax()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3), engine.Query("X is min(3, 7).")["X"]);
        Assert.Equal(Int(7), engine.Query("X is max(3, 7).")["X"]);
    }

    // ---------- Arithmetic comparison ----------

    [Fact]
    public void ArithEq_ValueOnlyComparison()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("3 + 4 =:= 7.").Success);
        Assert.False(engine.Query("3 + 4 =:= 8.").Success);
    }

    [Fact]
    public void ArithNotEq() => AssertSucceeds("2 + 2 =\\= 5.");

    [Fact]
    public void Less_Greater()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("3 < 5.").Success);
        Assert.True(engine.Query("5 > 3.").Success);
        Assert.False(engine.Query("3 > 5.").Success);
    }

    [Fact]
    public void LessOrEq_GreaterOrEq()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("5 =< 5.").Success);
        Assert.True(engine.Query("5 >= 5.").Success);
    }

    // ---------- Bitwise ----------

    [Fact]
    public void Bitwise_AndOrXor()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(0b0100), engine.Query("X is 12 /\\ 5.")["X"]);
        Assert.Equal(Int(13), engine.Query("X is 12 \\/ 5.")["X"]);
        Assert.Equal(Int(9), engine.Query("X is 12 xor 5.")["X"]);
    }

    [Fact]
    public void Bitwise_Shifts()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(32), engine.Query("X is 8 << 2.")["X"]);
        Assert.Equal(Int(2), engine.Query("X is 32 >> 4.")["X"]);
    }

    // ---------- Errors ----------

    [Fact]
    public void Error_InstantiationCaughtAsIsoError()
    {
        // Outside catch/3, the engine surfaces the Core-level
        // PrologRuntimeException. Inside catch/3 it gets translated to
        // the canonical ISO error term (chunk 34's
        // PrologRuntimeException → error(...) bridge). The conformance
        // story is the user-visible one — they catch the ISO term.
        var engine = new PrologEngine();
        var sol = engine.Query("catch(X is Y + 1, error(Kind, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("instantiation_error"), sol["Kind"]);
    }

    [Fact]
    public void Error_DivisionByZero_CaughtAsIsoError()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("catch(X is 5 / 0, error(Kind, _), true).");
        Assert.True(sol.Success);
        // ISO: evaluation_error(zero_divisor).
        var k = sol["Kind"];
        string name = k is CompoundTerm c ? c.Functor : (k as AtomTerm)?.Name ?? "";
        Assert.Equal("evaluation_error", name);
    }

    // ---------- Helpers ----------

    private static void AssertBinding(string query, string varName, Term expected)
    {
        var engine = new PrologEngine();
        var sol = engine.Query(query);
        Assert.True(sol.Success, $"Query failed: {query}");
        Assert.Equal(expected, sol[varName]);
    }

    private static void AssertSucceeds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }
}
