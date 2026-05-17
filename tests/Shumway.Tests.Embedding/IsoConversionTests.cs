using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 27 coverage (Part A): C# exceptions raised by arithmetic
/// built-ins now surface as canonical ISO <c>error(Kind, _)</c> terms
/// when an enclosing <c>catch/3</c> intercepts them. Without an active
/// catch the underlying <see cref="Shumway.Core.PrologRuntimeException"/>
/// still propagates, so library callers see a structured exception
/// rather than a free-form message.
/// </summary>
public class IsoConversionTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);

    [Fact]
    public void Catch_ArithmeticDivisionByZero_GetsEvaluationError()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(X is 1 / 0, error(evaluation_error(K), _), Got = K).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("zero_divisor"), sol["Got"]);
    }

    [Fact]
    public void Catch_IntegerDivisionByZero_GetsEvaluationError()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(X is 7 // 0, error(evaluation_error(zero_divisor), _), Out = ok).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Out"]);
    }

    [Fact]
    public void Catch_UnboundInExpression_GetsInstantiationError()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(X is Y + 1, error(instantiation_error, _), Out = caught).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("caught"), sol["Out"]);
    }

    [Fact]
    public void Catch_TypeError_InModWithFloat_GetsTypeError()
    {
        var engine = new PrologEngine();
        // 5.0 mod 2 → mod expects integer operands; should be type_error.
        var sol = engine.Query(
            "catch(X is 5.0 mod 2, error(type_error(_, _), _), Out = caught).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("caught"), sol["Out"]);
    }

    [Fact]
    public void Uncaught_DivisionByZero_RaisesPrologRuntimeException()
    {
        // Without an enclosing catch, the lightweight Core-level exception
        // propagates to the .NET caller — embedding-API users can detect
        // the structured Kind / Detail directly.
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("X is 1 / 0."));
        Assert.Equal("evaluation_error", ex.Kind);
        Assert.Equal("zero_divisor", ex.Detail);
    }

    [Fact]
    public void Catch_MismatchedCatcher_RethrowsTranslatedIsoError()
    {
        // The catcher pattern doesn't match the actual error kind — the
        // ISO error term re-raises as ShumwayPrologException (not the
        // original PrologRuntimeException), so subsequent .NET catchers
        // see the ISO shape.
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("catch(X is 1 / 0, instantiation_error, true)."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("evaluation_error", inner.Functor);
    }
}
