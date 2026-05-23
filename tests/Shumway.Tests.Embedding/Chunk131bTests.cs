using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 131b (Phase 9 Stage A, step 3, file 2): the arithmetic
/// evaluator's "I don't know this functor / atom / arity" paths and
/// <c>succ/2</c>'s contract failures now raise catchable
/// <see cref="Shumway.Core.PrologRuntimeException"/> errors with ISO
/// kinds, replacing the uncatchable
/// <see cref="System.InvalidOperationException"/> earlier phases used.
///
/// <para>Note on the type_error value slot: Shumway's
/// <c>PrologRuntimeException</c> carries a flat string Detail, so the
/// translated form is <c>error(type_error(evaluable, _), _)</c> rather
/// than the full ISO <c>type_error(evaluable, Name/Arity)</c>. A
/// catcher matching on the kind atom (the common pattern) still
/// succeeds; widening the exception to carry a Term value slot is
/// queued for a later refinement.</para>
/// </summary>
public class Chunk131bTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- ArithmeticEvaluator ----------

    [Fact]
    public void UnknownAtomInArithmetic_RaisesTypeErrorEvaluable()
    {
        // The chunk-130 surfaced case: `X is foo` used to throw
        // InvalidOperationException; now it's a catchable type_error.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is foo, error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("evaluable"), sol["T"]);
    }

    [Fact]
    public void UnknownUnaryFunction_RaisesTypeErrorEvaluable()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is frobnicate(1), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("evaluable"), sol["T"]);
    }

    [Fact]
    public void UnknownBinaryFunction_RaisesTypeErrorEvaluable()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is frobnicate(1, 2), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("evaluable"), sol["T"]);
    }

    [Fact]
    public void UnknownArityFunction_RaisesTypeErrorEvaluable()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is foo(1,2,3), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("evaluable"), sol["T"]);
    }

    [Fact]
    public void ContextSlot_CarriesIs2Indicator()
    {
        // Chunk-130 stamping: the indicator names the evaluating builtin,
        // not the inner arithmetic functor (which lives in the value slot).
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is foo, error(_, Name/Arity), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("is"), sol["Name"]);
        Assert.Equal(Int(2), sol["Arity"]);
    }

    // ---------- succ/2 ----------

    [Fact]
    public void Succ_BothUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(succ(_X, _Y), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Succ_NegativeFirstArg_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(succ(-1, _Y), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("not_less_than_zero"), sol["D"]);
    }

    [Fact]
    public void Succ_NegativeSecondArg_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(succ(_X, -1), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("not_less_than_zero"), sol["D"]);
    }

    [Fact]
    public void Succ_NonIntegerArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(succ(foo, _Y), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    [Fact]
    public void Succ_ZeroSecondArg_StillFails()
    {
        // succ(_, 0) has no solution — fail, not error.
        var e = new PrologEngine();
        Assert.False(e.Query("succ(_X, 0).").Success);
    }

    // ---------- Happy paths still work ----------

    [Fact]
    public void Succ_ForwardDirection_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("succ(5, Y).");
        Assert.True(sol.Success);
        Assert.Equal(Int(6), sol["Y"]);
    }

    [Fact]
    public void Succ_BackwardDirection_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("succ(X, 5).");
        Assert.True(sol.Success);
        Assert.Equal(Int(4), sol["X"]);
    }

    [Fact]
    public void StandardArithmetic_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("X is 2 + 3 * 4.");
        Assert.True(sol.Success);
        Assert.Equal(Int(14), sol["X"]);
    }
}
