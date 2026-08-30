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

    // ---------- §9 error shapes and Cor.2 evaluables ----------

    [Fact]
    public void Error_UnknownEvaluable_ReportsIndicator()
    {
        // The culprit is the procedure INDICATOR Name/Arity, not the term.
        AssertSucceeds("catch(_ is foo, error(type_error(evaluable, V), _), true), V == foo/0.");
        AssertSucceeds("catch(_ is foo(1,2,3), error(type_error(evaluable, V), _), true), V == foo/3.");
    }

    [Fact]
    public void Error_IntegerOpOnFloat_ReportsCulpritValue()
    {
        AssertSucceeds(@"catch(_ is 1.0 /\ 2, error(type_error(integer, V), _), true), V == 1.0.");
        AssertSucceeds(@"catch(_ is 5 mod 2.0, error(type_error(integer, V), _), true), V == 2.0.");
        AssertSucceeds(@"catch(_ is \ 1.5, error(type_error(integer, V), _), true), V == 1.5.");
    }

    [Fact]
    public void Error_DomainConditions_AreUndefined()
    {
        // §9.3 domain conditions: evaluation_error(undefined), not IEEE inf/nan.
        AssertSucceeds("catch(_ is log(0), error(evaluation_error(undefined), _), true).");
        AssertSucceeds("catch(_ is sqrt(-1.0), error(evaluation_error(undefined), _), true).");
        AssertSucceeds("catch(_ is acosh(0.5), error(evaluation_error(undefined), _), true).");
    }

    [Fact]
    public void Error_FloatOverflow_FromFiniteOperands()
    {
        // §9.1.4.1: a finite computation exceeding the float range is
        // evaluation_error(float_overflow), never a silent infinity.
        AssertSucceeds("catch(_ is exp(1000), error(evaluation_error(float_overflow), _), true).");
        AssertSucceeds("catch(_ is 1.0e308 * 1.0e308, error(evaluation_error(float_overflow), _), true).");
        AssertSucceeds("catch(_ is 1.0e308 / 1.0e-308, error(evaluation_error(float_overflow), _), true).");
    }

    [Fact]
    public void Is_RoundHalvesGoTowardPositiveInfinity()
    {
        // ISO 9.1.6.1: round(x) = floor(x + 1/2).
        var engine = new PrologEngine();
        Assert.Equal(Int(-3), engine.Query("X is round(-3.5).")["X"]);
        Assert.Equal(Int(-4), engine.Query("X is round(-4.5).")["X"]);
        Assert.Equal(Int(5), engine.Query("X is round(4.5).")["X"]);
    }

    [Fact]
    public void Is_TruncateOfHugeFloatIsExact()
    {
        // truncate(1.0e30) must produce the double's exact integer value
        // (a 30-digit bignum), not a silently wrapped long. The double is
        // not exactly 10^30, so pin magnitude + integrality, not equality.
        AssertSucceeds("X is truncate(1.0e30), integer(X), abs(X - 10^30) < 10^15.");
    }

    [Fact]
    public void Is_IntegerPowerWithNegativeExponent()
    {
        // ISO 9.3.10 (Cor.2): ±1 keep an integer value; 0 has none
        // (undefined); any other integer base has no integer result —
        // type_error(float, Base).
        var engine = new PrologEngine();
        Assert.Equal(Int(1), engine.Query("X is 1 ^ (-1).")["X"]);
        Assert.Equal(Int(-1), engine.Query("X is (-1) ^ (-3).")["X"]);
        AssertSucceeds("catch(_ is 0 ^ (-42), error(evaluation_error(undefined), _), true).");
        AssertSucceeds("catch(_ is 2 ^ (-1), error(type_error(float, V), _), true), V == 2.");
    }

    [Fact]
    public void Is_HyperbolicFamily()
    {
        // Cor.2 additions round-trip: atanh(tanh(x)) ≈ x, asinh(sinh(x)) ≈ x.
        AssertSucceeds("X is atanh(tanh(0.5)), abs(X - 0.5) < 1.0e-9.");
        AssertSucceeds("X is asinh(sinh(0.5)), abs(X - 0.5) < 1.0e-9.");
        AssertSucceeds("X is acosh(cosh(0.5)), abs(X - 0.5) < 1.0e-9.");
    }

    [Fact]
    public void Is_LogWithBaseAndLog10()
    {
        AssertSucceeds("X is log(2, 8), abs(X - 3.0) < 1.0e-9.");
        AssertSucceeds("X is log10(1000), abs(X - 3.0) < 1.0e-9.");
        AssertSucceeds("catch(_ is log(1, 10), error(evaluation_error(zero_divisor), _), true).");
    }

    [Fact]
    public void Is_BitFunctions_MsbLsbPopcount()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(7), engine.Query("X is msb(128).")["X"]);
        Assert.Equal(Int(103), engine.Query("X is msb(2^103 + 1).")["X"]);
        Assert.Equal(Int(103), engine.Query("X is lsb(2^103).")["X"]);
        Assert.Equal(Int(4), engine.Query("X is popcount(170).")["X"]);
        AssertSucceeds("catch(_ is popcount(3.14), error(type_error(integer, V), _), true), V == 3.14.");
        AssertSucceeds("catch(_ is popcount(-1), error(domain_error(not_less_than_zero, V), _), true), V == -1.");
    }

    [Fact]
    public void Is_RepresentationCap_IsACatchableResourceError()
    {
        // Unbounded integers still live in a finite representation (.NET's
        // BigInteger caps a magnitude at int.MaxValue bits). The cap used to
        // leak as a raw OverflowException / OutOfMemoryException at one
        // exponent and a non-ISO error tag at the next; all three neighbours
        // now answer with the same catchable ISO resource_error(memory).
        AssertSucceeds("catch(_ is 2^2147483646, error(resource_error(memory), _), true).");
        AssertSucceeds("catch(_ is 2^2147483647, error(resource_error(memory), _), true).");
        AssertSucceeds("catch(_ is 2^2147483648, error(resource_error(memory), _), true).");
        // A trivial base stays exact at ANY exponent.
        AssertBinding("X is 1^2147483648.", "X", Int(1));
        AssertBinding("X is (-1)^2147483649.", "X", Int(-1));
        AssertBinding("X is 0^2147483648.", "X", Int(0));
    }

    [Fact]
    public void Is_ShiftCounts_BeyondIntRange()
    {
        // The shift count was truncated by a bare (int) cast, so
        // `1 << 4294967296` shifted by zero and answered 1 — silently wrong.
        AssertSucceeds("catch(_ is 1 << 4294967296, error(resource_error(memory), _), true).");
        AssertBinding("X is 0 << 4294967296.", "X", Int(0));
        AssertBinding("X is 5 >> 4294967296.", "X", Int(0));
        AssertBinding("X is -5 >> 4294967296.", "X", Int(-1));
        // C# masks a long's shift count to 0..63: `1L >> 64` was 1.
        AssertBinding("X is 1 >> 64.", "X", Int(0));
        AssertBinding("X is -1 >> 200.", "X", Int(-1));
        // A negative count shifts the other way (BigInteger semantics).
        AssertBinding("X is 5 >> -2.", "X", Int(20));
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
