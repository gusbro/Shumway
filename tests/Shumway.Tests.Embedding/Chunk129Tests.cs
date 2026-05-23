using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 129 (Phase 9 Stage A, step 1): the four ISO error kinds that
/// <see cref="IsoError"/> didn't have before — <c>representation_error</c>,
/// <c>syntax_error</c>, <c>resource_error</c>, <c>system_error</c> — plus
/// the corresponding <see cref="PrologRuntimeException"/> kind strings
/// being translated into matching <c>error/2</c> terms by
/// <c>MetaBuiltins.TranslateRuntimeError</c>.
///
/// <para>The pre-existing string-keyed <c>syntax_error</c> raise in
/// <c>AtomCharBuiltins</c> (the <c>number_codes/2</c> path) used to fall
/// through to the generic case in the translator and surface as
/// <c>error(syntax_error, illegal_number)</c> — wrong shape. Chunk 129
/// adds the explicit case so it now becomes
/// <c>error(syntax_error(illegal_number), _)</c>, matching ISO §8.16.7.</para>
/// </summary>
public class Chunk129Tests
{
    private static AtomTerm Atom(string n) => new(n);

    // ---------- IsoError factory shape ----------

    [Fact]
    public void RepresentationError_BuildsCorrectTerm()
    {
        var err = IsoError.RepresentationError("character_code");
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.Equal("error", ct.Functor);
        Assert.Equal(2, ct.Args.Length);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("representation_error", inner.Functor);
        Assert.Single(inner.Args);
        Assert.Equal(Atom("character_code"), inner.Args[0]);
    }

    [Fact]
    public void SyntaxError_BuildsCorrectTerm()
    {
        var err = IsoError.SyntaxError("illegal_number");
        var inner = Assert.IsType<CompoundTerm>(
            Assert.IsType<CompoundTerm>(err).Args[0]);
        Assert.Equal("syntax_error", inner.Functor);
        Assert.Equal(Atom("illegal_number"), inner.Args[0]);
    }

    [Fact]
    public void ResourceError_BuildsCorrectTerm()
    {
        var err = IsoError.ResourceError("heap");
        var inner = Assert.IsType<CompoundTerm>(
            Assert.IsType<CompoundTerm>(err).Args[0]);
        Assert.Equal("resource_error", inner.Functor);
        Assert.Equal(Atom("heap"), inner.Args[0]);
    }

    [Fact]
    public void SystemError_PayloadFree_IsBareAtom()
    {
        // §7.12.2.j — the standard system_error term carries no payload.
        var err = IsoError.SystemError();
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.Equal("error", ct.Functor);
        var inner = Assert.IsType<AtomTerm>(ct.Args[0]);
        Assert.Equal("system_error", inner.Name);
    }

    [Fact]
    public void SystemError_WithDetail_IsCompound()
    {
        // The detail variant — useful for surfacing a .NET exception
        // message through catch/3 without losing the diagnostic.
        var err = IsoError.SystemError("io_failed");
        var inner = Assert.IsType<CompoundTerm>(
            Assert.IsType<CompoundTerm>(err).Args[0]);
        Assert.Equal("system_error", inner.Functor);
        Assert.Equal(Atom("io_failed"), inner.Args[0]);
    }

    // ---------- catch/3 against the new kinds via TranslateRuntimeError ----------

    [Fact]
    public void NumberCodes_BadInput_RaisesSyntaxErrorIllegalNumber()
    {
        // Chunk 129: the existing PrologRuntimeException("syntax_error",
        // "illegal_number") raise in number_codes/2 now translates into
        // the ISO-shaped error(syntax_error(illegal_number), _).
        var engine = new PrologEngine();
        // [97,98,99] is the code list for "abc" — not a number, so
        // number_codes/2 raises ISO syntax_error(illegal_number).
        var sol = engine.Query(
            "catch(number_codes(_, [97,98,99]), "
            + "error(syntax_error(D), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("illegal_number"), sol["D"]);
    }

    [Fact]
    public void Catch_RepresentationError_PatternMatches()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(error(representation_error(max_arity), _)), "
            + "error(representation_error(Flag), _), Caught = Flag).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("max_arity"), sol["Caught"]);
    }

    [Fact]
    public void Catch_ResourceError_PatternMatches()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(error(resource_error(heap), _)), "
            + "error(resource_error(R), _), Caught = R).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("heap"), sol["Caught"]);
    }

    [Fact]
    public void Catch_SystemError_BareAtomPatternMatches()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(throw(error(system_error, _)), "
            + "error(system_error, _), Caught = ok).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }
}
