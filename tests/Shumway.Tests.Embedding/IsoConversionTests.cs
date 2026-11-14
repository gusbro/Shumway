using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// C# exceptions raised by arithmetic built-ins surface as canonical ISO
/// <c>error(Kind, _)</c> terms: to an enclosing <c>catch/3</c>, and to the
/// host when nothing catches them, as a <see cref="ShumwayPrologException"/>
/// whose inner exception is the builtin's
/// <see cref="Shumway.Core.PrologRuntimeException"/>.
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
    public void Uncaught_DivisionByZero_RaisesTheBall()
    {
        // With no catch/3 on the way, the host gets what a catch/3 would:
        // the ISO ball, with the builtin's Kind / Detail inside.
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(() => engine.Query("X is 1 / 0."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        Assert.Equal("evaluation_error", Assert.IsType<CompoundTerm>(ct.Args[0]).Functor);
        var raised = Assert.IsType<Shumway.Core.PrologRuntimeException>(ex.InnerException);
        Assert.Equal("evaluation_error", raised.Kind);
        Assert.Equal("zero_divisor", raised.Detail);
    }

    [Fact]
    public void Catch_MismatchedCatcher_RethrowsTranslatedIsoError()
    {
        // The catcher pattern doesn't match the actual error kind: the host
        // gets the same as with no catch/3 at all.
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("catch(X is 1 / 0, instantiation_error, true)."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("evaluation_error", inner.Functor);
        Assert.IsType<Shumway.Core.PrologRuntimeException>(ex.InnerException);
    }
}
