using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 44: Tier-1 IL bodies (allocate / call_builtin / proceed / cut)
/// plus a fuller arithmetic operator set (bitwise, trig, power, sign,
/// gcd, rounding). Rules whose body is a chain of builtin calls (no
/// user-defined predicate calls yet) now promote to IL through the
/// existing single-clause path — the new opcode coverage handles the
/// put_/get_variable_x/y, call_builtin, allocate/deallocate, and
/// neck_cut bytes that real rule bodies are made of.
/// </summary>
public class Chunk44Tests
{
    private static Term Int(long v) => new IntTerm(v);
    private static Term Atom(string n) => new AtomTerm(n);

    // ============================================================================
    // IL bodies: rules that call builtins promote to IL
    // ============================================================================

    [Fact]
    public void IlBody_GreaterThan_Promotes()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public pos/1.\npos(X) :- X > 0.");
        Assert.True(engine.Query("pos(5).").Success);
        Assert.False(engine.Query("pos(-3).").Success);
        // The threshold-1 promotion should have fired on the first call.
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("pos", permanent: true).Id, 1);
        Assert.True(engine.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void IlBody_Unification_Promotes()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public eq/2.\neq(X, Y) :- X = Y.");
        Assert.True(engine.Query("eq(a, a).").Success);
        Assert.False(engine.Query("eq(a, b).").Success);
        Assert.True(engine.Query("eq(X, foo), X == foo.").Success);
    }

    [Fact]
    public void IlBody_NeckCut_PromotesAndWorks()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public maybe/1.\n" +
            "maybe(X) :- atom(X), !.\n" +
            "maybe(_).\n");
        // With cut: the first matching clause commits.
        Assert.True(engine.Query("maybe(foo).").Success);
        // Second clause runs for non-atoms.
        Assert.True(engine.Query("maybe(42).").Success);
    }

    [Fact]
    public void IlBody_ArithmeticIs_Promotes()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public double/2.\ndouble(X, Y) :- Y is X * 2.");
        Assert.Equal(Int(10), engine.Query("double(5, R).")["R"]);
    }

    [Fact]
    public void IlBody_BodyWithAllocate_Promotes()
    {
        // A 2-goal body needs an env frame (allocate) so the bound variable
        // survives the first call.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public check/1.\ncheck(X) :- atom(X), atom_length(X, N), N > 0.");
        Assert.True(engine.Query("check(hello).").Success);
        Assert.False(engine.Query("check(42).").Success);
    }

    [Fact]
    public void IlBody_ProducesSameResultsAsTier0()
    {
        var src = ":- public mul/3.\nmul(X, Y, Z) :- Z is X * Y.";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sol0 = tier0.Query("mul(6, 7, R).");

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("mul(2, 3, _).");   // warm + promote
        var sol1 = tier1.Query("mul(6, 7, R).");

        Assert.Equal(sol0["R"], sol1["R"]);
    }

    // ============================================================================
    // Arithmetic operators
    // ============================================================================

    [Fact]
    public void Arith_BitwiseAnd()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(10), engine.Query("X is 14 /\\ 11.")["X"]);   // 1110 & 1011 = 1010
        Assert.Equal(Int(10), engine.Query("X is 14 /\\ 10.")["X"]);   // 1110 & 1010 = 1010
    }

    [Fact]
    public void Arith_BitwiseOr()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(15), engine.Query("X is 12 \\/ 3.")["X"]);    // 1100 | 0011 = 1111
    }

    [Fact]
    public void Arith_BitwiseXor()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(5), engine.Query("X is 12 xor 9.")["X"]);     // 1100 ^ 1001 = 0101
    }

    [Fact]
    public void Arith_BitwiseNot()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(-1), engine.Query("X is \\ 0.")["X"]);
        Assert.Equal(Int(~42L), engine.Query("X is \\ 42.")["X"]);
    }

    [Fact]
    public void Arith_ShiftLeftRight()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(8), engine.Query("X is 1 << 3.")["X"]);
        Assert.Equal(Int(2), engine.Query("X is 16 >> 3.")["X"]);
    }

    [Fact]
    public void Arith_Sign()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(1), engine.Query("X is sign(42).")["X"]);
        Assert.Equal(Int(-1), engine.Query("X is sign(-7).")["X"]);
        Assert.Equal(Int(0), engine.Query("X is sign(0).")["X"]);
    }

    [Fact]
    public void Arith_Gcd()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(6), engine.Query("X is gcd(12, 18).")["X"]);
        Assert.Equal(Int(1), engine.Query("X is gcd(17, 5).")["X"]);
    }

    [Fact]
    public void Arith_PowerIntegerExponent()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(8), engine.Query("X is 2 ** 3.")["X"]);
        Assert.Equal(Int(1024), engine.Query("X is 2 ^ 10.")["X"]);
    }

    [Fact]
    public void Arith_FloatRounding()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(4), engine.Query("X is ceiling(3.2).")["X"]);
        Assert.Equal(Int(3), engine.Query("X is floor(3.8).")["X"]);
        Assert.Equal(Int(4), engine.Query("X is round(3.5).")["X"]);
        Assert.Equal(Int(3), engine.Query("X is truncate(3.9).")["X"]);
    }

    [Fact]
    public void Arith_TrigBasics()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is sin(0.0).");
        var f = Assert.IsType<FloatTerm>(sol["X"]);
        Assert.Equal(0.0, f.Value, 10);

        sol = engine.Query("X is cos(0.0).");
        f = Assert.IsType<FloatTerm>(sol["X"]);
        Assert.Equal(1.0, f.Value, 10);
    }

    [Fact]
    public void Arith_SqrtAndExpLog()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is sqrt(16.0).");
        var f = Assert.IsType<FloatTerm>(sol["X"]);
        Assert.Equal(4.0, f.Value, 10);

        // exp(log(N)) ≈ N.
        sol = engine.Query("X is exp(log(7.0)).");
        f = Assert.IsType<FloatTerm>(sol["X"]);
        Assert.Equal(7.0, f.Value, 8);
    }
}
