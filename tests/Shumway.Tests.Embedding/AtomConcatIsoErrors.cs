using System;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ISO §8.16.2: <c>atom_concat(A, B, C)</c> needs either {A, B} both
/// atoms (forward direction) or C an atom (reverse direction). When
/// C is var and either A or B is var, no direction can drive, so
/// the right error is <c>instantiation_error</c> — not
/// <c>type_error(atom, _)</c>, which the old chunk-131c code raised
/// whenever any of the three was a non-atom non-ref.
///
/// Surfaced linting Blint.pl: a <c>concat([Prefix, X], Msg)</c> in
/// Blint with X and Msg still unbound used to crash with
/// <c>type_error(atom, _G..)</c>, masking the real ISO complaint.
/// </summary>
public class AtomConcatIsoErrors
{
    [Fact]
    public void Bound_Var_Var_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(foo, _, _), E, true).");
        Assert.True(sol.Success);
        Assert.Contains("instantiation_error", sol["E"]!.ToString());
    }

    [Fact]
    public void Var_Bound_Var_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(_, bar, _), E, true).");
        Assert.True(sol.Success);
        Assert.Contains("instantiation_error", sol["E"]!.ToString());
    }

    [Fact]
    public void All_Var_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(_, _, _), E, true).");
        Assert.True(sol.Success);
        Assert.Contains("instantiation_error", sol["E"]!.ToString());
    }

    [Fact]
    public void NonAtom_AsA_VarC_RaisesTypeErrorAtom()
    {
        // A=42 (int), B=var, C=var: C can't drive (var), forward
        // can't drive (A non-atom). ISO is type_error(atom, A).
        var e = new PrologEngine();
        var sol = e.Query("catch(atom_concat(42, foo, _), E, true).");
        Assert.True(sol.Success);
        Assert.Contains("type_error", sol["E"]!.ToString());
    }
}
