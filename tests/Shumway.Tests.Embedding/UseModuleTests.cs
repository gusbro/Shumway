using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// SWI-style <c>use_module/1</c> builtin — Prolog-level entry point that
/// the REPL uses to opt into the CLP(FD) / CLP(R) libraries that the
/// embedding API exposes as <see cref="PrologEngine.UseClpfd"/> and
/// <see cref="PrologEngine.UseClpr"/>.
/// </summary>
public class UseModuleTests
{
    [Fact]
    public void UseModule_LibraryClpfd_EnablesFdConstraints()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("use_module(library(clpfd)).").Success);

        var sol = engine.Query("[X, Y] ins 1..3, X + Y #= 4, label([X, Y]).");
        Assert.True(sol.Success);
        Assert.Equal(1L, ((IntTerm)sol["X"]!).Value);
        Assert.Equal(3L, ((IntTerm)sol["Y"]!).Value);
    }

    [Fact]
    public void UseModule_LibraryClpr_EnablesRealConstraints()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("use_module(library(clpr)).").Success);
        Assert.True(engine.Query("{X + Y = 3.0, X - Y = 1.0}.").Success);
    }

    [Fact]
    public void UseModule_UnknownLibrary_RaisesExistenceError()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(use_module(library(nope)), "
            + "error(existence_error(library, nope), _), true).").Success);
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("use_module(library(nope))."));
    }

    [Fact]
    public void UseModule_UnboundArg_RaisesInstantiationError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.Query("use_module(_).") );
        Assert.Contains("instantiation_error", ex.Message);
    }
}
