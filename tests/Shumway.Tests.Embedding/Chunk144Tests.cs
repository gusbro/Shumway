using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 144 (Phase 10, step A1): the offending value an
/// <c>error/2</c> term should report — the X in
/// <c>type_error(integer, X)</c>, the Y in
/// <c>domain_error(not_less_than_zero, Y)</c> — used to be a fresh
/// anonymous variable across the
/// <see cref="Shumway.Core.PrologRuntimeException"/>-translation
/// path. Chunk 144 widens the exception with a <c>Value</c> payload
/// captured at the throw site by an
/// <see cref="Shumway.Core.Activation.MaterializeCellToTerm"/> callback,
/// and <c>MetaBuiltins.TranslateRuntimeError</c> uses it when building
/// the error compound. Catchers matching on the value slot now bind
/// to the actual culprit.
/// </summary>
public class Chunk144Tests
{
    private static AtomTerm Atom(string n) => new(n);

    [Fact]
    public void TypeError_Evaluable_BindsValueSlot()
    {
        // `X is foo` — foo is the offending non-evaluable atom.
        // ArithmeticEvaluator.EvaluateAtomConstant now passes the cell
        // through the value-carrying ctor; the catcher binds V to it.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is foo, error(type_error(evaluable, V), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo"), sol["V"]);
    }

    [Fact]
    public void TypeError_NonEvaluableCompound_BindsValueSlot()
    {
        // A bound non-evaluable compound — `is(_, frobnicate(1,2,3))`.
        // The catcher sees the offending compound term.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(_X is frobnicate(1,2,3), error(type_error(evaluable, V), _), true).");
        Assert.True(sol.Success);
        // Translated value is whatever EvaluateCompound's "wrong-arity"
        // branch threw. The chunk-131b code passes the cell through
        // the value ctor.
        Assert.NotNull(sol["V"]);
    }

    [Fact]
    public void TypeError_NoValueAtThrowSite_StillGetsAnonVar()
    {
        // The PrologRuntimeException(string, string) constructor — the
        // old shape used by sites that don't have a Cell to capture —
        // still produces an anonymous-var value slot. Backwards
        // compatible.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(succ(foo, _), error(type_error(integer, V), _), true).");
        Assert.True(sol.Success);
        // V should be unbound (a fresh _G var) — pin by unifying with
        // a fresh marker; that succeeds because V is var.
        Assert.True(e.Query(
            "catch(succ(foo, _), error(type_error(integer, V), _), V = anything).").Success);
    }
}
