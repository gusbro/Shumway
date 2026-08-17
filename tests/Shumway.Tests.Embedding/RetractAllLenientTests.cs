using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>retractall/1 leniency (SWI / SICStus): a no-op on an UNDEFINED
/// predicate (was raising permission_error via retract), an error on a STATIC
/// one, and a normal bulk-retract on a dynamic one.</summary>
public sealed class RetractAllLenientTests
{
    [Fact]
    public void RetractAll_OnUndefined_IsNoOp()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("retractall(never_defined(_)).").Success);
        // The predicate is left UNDEFINED (no fabricated dynamic stub), so
        // calling it still raises existence_error — unchanged by the retractall.
        Assert.True(e.Query(
            "catch(never_defined(_), error(existence_error(procedure, _), _), true).").Success);
    }

    [Fact]
    public void RetractAll_OnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            p(1).
            p(2).
            """);   // static
        var sol = e.Query("catch(retractall(p(_)), error(E, _), true), E = permission_error(modify, static_procedure, _).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void RetractAll_OnDynamic_RemovesMatching()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic d/1.
            d(1).
            d(2).
            d(3).
            """);
        Assert.True(e.Query("retractall(d(2)).").Success);
        Assert.Equal(new[] { "1", "3" },
            e.QueryAll("d(X).").Select(s => s.Bindings["X"].ToString()).ToArray());
        Assert.True(e.Query("(retractall(d(_)), \\+ d(_)).").Success);
    }
}
