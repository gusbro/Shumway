using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// call/N with a module-qualified goal `M:G`. `call(M:Goal, Extra...)` must
/// extend Goal with the extra arguments INSIDE the module qualification —
/// `call(m:foo, X)` is `m:foo(X)`, not `:(m, foo, X)` (a spurious (:)/3). Real
/// libraries depend on this: library(error)'s must_be/2 runs `call(error:ilist,
/// Term)` through a meta-argument, which is how Scryer's DCG expander reaches it.
/// </summary>
public class ModuleQualifiedCallTests
{
    [Fact]
    public void CallModuleQualified_ExtendsInsideTheQualifier()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(hello).");
        // call(user:foo, X) == user:foo(X) == foo(X)
        Assert.Equal("hello",
            Assert.IsType<AtomTerm>(e.Query("call(user:foo, X).")["X"]).Name);
    }

    [Fact]
    public void CallModuleQualified_TwoExtraArgs()
    {
        var e = new PrologEngine();
        e.ConsultString("add(A, B, C) :- C is A + B.");
        Assert.Equal(5L,
            Assert.IsType<IntTerm>(e.Query("call(user:add(2), 3, C).")["C"]).Value);
    }

    [Fact]
    public void CallModuleQualified_NoExtraArgs()
    {
        var e = new PrologEngine();
        e.ConsultString("p(ok).");
        Assert.True(e.Query("call(user:p(ok)).").Success);
    }

    [Fact]
    public void PlainCall_StillWorks()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(hello).");
        Assert.Equal("hello",
            Assert.IsType<AtomTerm>(e.Query("call(foo, X).")["X"]).Name);
    }
}
