using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Engine features/fixes from the SWI library triage
/// (docs/library-triage-swi.md): variant equivalence (<c>=@=</c>), the
/// <c>\e</c>/<c>\u</c>/<c>\U</c> lexer escapes, and no-op SWI declaration
/// directives.</summary>
public sealed class SwiCompatFixTests
{
    [Fact]
    public void VariantEquivalence()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("f(X, Y) =@= f(A, B).").Success);        // variants
        Assert.True(e.Query("f(a, X) =@= f(a, Y).").Success);
        Assert.False(e.Query("f(X, X) =@= f(A, B).").Success);       // sharing differs
        Assert.True(e.Query("f(X, X) \\=@= f(A, B).").Success);
        Assert.False(e.Query("g(1) =@= g(2).").Success);
    }

    [Fact]
    public void LexerEscapes_e_and_u()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("0'\\e =:= 27.").Success);           // ESC
        Assert.True(e.Query("0'\\u0041 =:= 65.").Success);       // 'A'
        Assert.True(e.Query("0'\\U00000041 =:= 65.").Success);   // 'A' (8-digit)
    }

    [Fact]
    public void SwiNoOpDirectives_LoadCleanly()
    {
        var e = new PrologEngine();
        // A module declaring SWI-specific directives that Shumway no-ops still
        // loads and its predicate works.
        e.ConsultString(
            ":- module(m, [p/1]).\n"
            + ":- module_transparent p/1.\n"
            + ":- volatile p/1.\n"
            + ":- predicate_options(p/1, 1, [verbose(boolean)]).\n"
            + ":- redefine_system_predicate(p/1).\n"
            + "p(ok).\n");
        Assert.True(e.Query("m:p(ok).").Success);
    }
}
