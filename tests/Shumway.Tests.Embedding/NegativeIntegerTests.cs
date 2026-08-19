using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 37: negative integer literals now parse to <c>IntTerm(-N)</c>
/// directly instead of the compound <c>-/1(N)</c>. Built-ins that
/// expect integers see negative literals without the user having to
/// run them through <c>is</c>.
/// </summary>
public class NegativeIntegerTests
{
    private static Term Int(long v) => new IntTerm(v);

    // ---------- type tests ----------

    [Fact]
    public void Integer_OfNegativeLiteral_Succeeds()
    {
        // Before chunk 37 this failed because -3 was the compound -(3).
        var engine = new PrologEngine();
        Assert.True(engine.Query("integer(-3).").Success);
        Assert.True(engine.Query("integer(-1).").Success);
    }

    [Fact]
    public void Number_OfNegativeLiteral_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("number(-7).").Success);
    }

    // ---------- unification ----------

    [Fact]
    public void Unify_VariableWithNegativeLiteral()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = -5.");
        Assert.True(sol.Success);
        Assert.Equal(Int(-5), sol["X"]);
    }

    // ---------- arithmetic ----------

    [Fact]
    public void Arith_NegativeOperandInExpression()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(-2), engine.Query("X is -5 + 3.")["X"]);
        Assert.Equal(Int(-15), engine.Query("X is -5 * 3.")["X"]);
    }

    [Fact]
    public void Arith_NegativeResultFromExpression()
    {
        var engine = new PrologEngine();
        // 1 - 4 = -3 — the binary minus stays binary; only the literal
        // collapse case is touched by chunk 37.
        Assert.Equal(Int(-3), engine.Query("X is 1 - 4.")["X"]);
    }

    // ---------- between/3 + arg/3 ----------

    [Fact]
    public void Between_NegativeLowBound()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("between(-3, 3, -2).").Success);
        Assert.False(engine.Query("between(-3, 3, -10).").Success);
    }

    [Fact]
    public void Arg_NegativeIndex_TypeError()
    {
        // arg/3 expects a positive integer; -1 IS now an Integer cell
        // (was a compound before), so the failure mode is "out of range",
        // not type_error.
        var engine = new PrologEngine();
        // §8.5.2.3 (GNU-verified): a negative index is a domain error,
        // not a silent failure.
        Assert.True(engine.Query(
            "catch(arg(-1, foo(a, b), _), "
            + "error(domain_error(not_less_than_zero, -1), _), true).").Success);
    }

    // ---------- Float ----------

    [Fact]
    public void Float_NegativeLiteral_ParsesAsFloatCell()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = -3.14.");
        Assert.True(sol.Success);
        Assert.Equal(new FloatTerm(-3.14), sol["X"]);
    }

    // ---------- Explicit compound stays compound ----------

    [Fact]
    public void ExplicitMinusParen_StaysAsCompound()
    {
        // `-(3)` (parens after the minus) is the unary-minus compound;
        // integer/1 sees a compound and fails.
        var engine = new PrologEngine();
        Assert.False(engine.Query("integer(-(3)).").Success);
        Assert.True(engine.Query("compound(-(3)).").Success);
    }
}
