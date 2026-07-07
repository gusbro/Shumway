using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 131d (Phase 9 Stage A, step 3, file 6): format/1 and format/2's
/// contract-violation paths now raise catchable ISO-shaped errors.
/// AttvarBuiltins and StreamBuiltins were already using
/// <see cref="Shumway.Core.PrologRuntimeException"/> correctly.
/// </summary>
public class Chunk131dTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- malformed format strings ----------

    [Fact]
    public void Format_TruncatedTilde_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format('hi~', []), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("format_spec"), sol["D"]);
    }

    [Fact]
    public void Format_UnknownSpec_RaisesDomainError()
    {
        var e = new PrologEngine();
        // ~z is not a recognised directive (Phase 33 added ~q/~p/~i/~e/~f/
        // ~g/~r/~R/~D, so the old ~q example is now valid).
        var sol = e.Query("catch(format('~z', [foo]), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("format_spec"), sol["D"]);
    }

    [Fact]
    public void Format_TooFewArgs_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format('~a ~a', [only_one]), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("format_argument_count"), sol["D"]);
    }

    // ---------- type mismatches inside specs ----------

    [Fact]
    public void Format_TildeA_NonAtom_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format('~a', [123]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    [Fact]
    public void Format_TildeA_Unbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format('~a', [_X]), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Format_TildeD_NonInteger_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format('~d', [foo]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    [Fact]
    public void Format_TildeS_NonIntListElement_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(format('~s', [[foo, bar]]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["T"]);
    }

    // ---------- format-arg list shape ----------

    [Fact]
    public void Format_FormatStringUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format(_F, []), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Format_FormatStringNonAtom_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(format(123, []), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    [Fact]
    public void Format_ArgListNotProperList_RaisesTypeErrorList()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(format('~a', [foo | non_nil]), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("list"), sol["T"]);
    }

    // ---------- Happy path still works ----------

    [Fact]
    public void Format_NoTilde_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("format('hello', []).").Success);
    }

    [Fact]
    public void Format_TildeAOnAtom_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("format('~a', [hi]).").Success);
    }

    // ---------- Context indicator ----------

    [Fact]
    public void ContextSlot_CarriesFormatIndicator()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(format('~a', [123]), error(_, Name/Arity), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("format"), sol["Name"]);
        Assert.Equal(Int(2), sol["Arity"]);
    }
}
