using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.4 Term comparison.
///
/// The standard order of terms (§7.2): Var &lt; Number &lt; Atom &lt;
/// String &lt; Compound. Numbers compare by value; atoms by code-list
/// of their names; compounds by arity, then functor name, then arg-by-arg.
///
/// Covers the syntactic comparison family — <c>(@&lt;)/2</c>,
/// <c>(@&gt;)/2</c>, <c>(@=&lt;)/2</c>, <c>(@&gt;=)/2</c>,
/// <c>(==)/2</c>, <c>(\\==)/2</c> — and <c>compare/3</c> (§8.4.2).
/// These are pure tests; ISO records no errors for them.
/// </summary>
public class TermComparisonConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- (==)/2 and (\==)/2 ----------

    [Fact]
    public void IdenticalAtoms_AreEqualEqual() =>
        Assert.True(new PrologEngine().Query("foo == foo.").Success);

    [Fact]
    public void DifferentAtoms_NotEqualEqual() =>
        Assert.True(new PrologEngine().Query("foo \\== bar.").Success);

    [Fact]
    public void TwoFreshVars_NotEqualEqual()
    {
        // Two distinct unbound variables: ISO §7.2 — Var is the
        // smallest category, but distinct vars are not identical.
        var e = new PrologEngine();
        Assert.True(e.Query("X \\== Y.").Success);
    }

    [Fact]
    public void SameVarTwice_IsEqualEqual() =>
        Assert.True(new PrologEngine().Query("X == X.").Success);

    [Fact]
    public void BoundVarToValue_EqualEqualToValue()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("X = 1, X == 1.").Success);
    }

    [Fact]
    public void IntegerVsFloat_NotEqualEqual()
    {
        // == is *structural*: a 1 (Int) and a 1.0 (Float) are
        // distinct, even though =:= treats them as numerically equal.
        var e = new PrologEngine();
        Assert.True(e.Query("1 \\== 1.0.").Success);
    }

    [Fact]
    public void Floats_StructuralEqual()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("1.5 == 1.5.").Success);
    }

    [Fact]
    public void CompoundsStructurallyEqual_AreEqualEqual()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("foo(1, 2) == foo(1, 2).").Success);
    }

    [Fact]
    public void CompoundsDifferentArgs_NotEqualEqual()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("foo(1) \\== foo(2).").Success);
    }

    // ---------- @</2, @>/2, @=</2, @>=/2 ----------

    [Fact]
    public void StandardOrder_NumberLessThanAtom() =>
        // ISO §7.2: Number < Atom.
        Assert.True(new PrologEngine().Query("1 @< foo.").Success);

    [Fact]
    public void StandardOrder_AtomLessThanCompound()
    {
        // Atom < Compound (a compound with the same functor name as an
        // atom is still bigger because of the arity comparison).
        var e = new PrologEngine();
        Assert.True(e.Query("foo @< foo(1).").Success);
    }

    [Fact]
    public void StandardOrder_VarLessThanNumber()
    {
        // Var < Number.
        var e = new PrologEngine();
        Assert.True(e.Query("X @< 1.").Success);
    }

    [Fact]
    public void StandardOrder_NumbersByValue()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("1 @< 2.").Success);
        Assert.True(e.Query("2 @> 1.").Success);
        Assert.False(e.Query("1 @< 1.").Success);
        Assert.True(e.Query("1 @=< 1.").Success);
        Assert.True(e.Query("1 @>= 1.").Success);
    }

    [Fact]
    public void StandardOrder_AtomsByName()
    {
        // Lexicographic on the name's character codes.
        var e = new PrologEngine();
        Assert.True(e.Query("apple @< banana.").Success);
        Assert.True(e.Query("banana @> apple.").Success);
    }

    [Fact]
    public void StandardOrder_CompoundsByArity()
    {
        // Lower arity sorts first regardless of functor name.
        var e = new PrologEngine();
        Assert.True(e.Query("foo(1) @< foo(1, 2).").Success);
        Assert.True(e.Query("zebra(1) @< apple(1, 2).").Success);
    }

    [Fact]
    public void StandardOrder_CompoundsByFunctorThenArg()
    {
        var e = new PrologEngine();
        // Same arity: name first, then arg-by-arg.
        Assert.True(e.Query("apple(1) @< banana(1).").Success);
        Assert.True(e.Query("foo(1) @< foo(2).").Success);
    }

    // ---------- compare/3 ----------

    [Fact]
    public void Compare_LessThan()
    {
        var e = new PrologEngine();
        var sol = e.Query("compare(R, 1, 2).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("<"), sol["R"]);
    }

    [Fact]
    public void Compare_Equal()
    {
        var e = new PrologEngine();
        var sol = e.Query("compare(R, foo, foo).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("="), sol["R"]);
    }

    [Fact]
    public void Compare_GreaterThan()
    {
        var e = new PrologEngine();
        var sol = e.Query("compare(R, foo, 1).");
        Assert.True(sol.Success);
        Assert.Equal(Atom(">"), sol["R"]);
    }

    [Fact]
    public void Compare_OrderGroundFirstArg()
    {
        // When the first arg is already ground, compare/3 just
        // succeeds iff that order is correct.
        var e = new PrologEngine();
        Assert.True(e.Query("compare(<, 1, 2).").Success);
        Assert.False(e.Query("compare(>, 1, 2).").Success);
        Assert.True(e.Query("compare(=, foo, foo).").Success);
    }
}
