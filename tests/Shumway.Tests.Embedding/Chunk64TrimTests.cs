using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 64 (revisited): env-trimming runtime gate is now ON. The
/// <see cref="Shumway.Core.Engine.TrimEnv"/> implementation grew a
/// CP-frame-protection check that raises the target shrink address
/// to the top of the most recent CP frame, which was the missing
/// piece in the prior investigation rounds — without it, a Call /
/// CallBuiltin trim could pull <c>_stackTop</c> below an active
/// try_me_else (or IL) CP frame, and the next push would overwrite
/// the CP's saved slots, corrupting state and (depending on the
/// timing) hanging or crashing on the next backtrack.
///
/// <para>The check uniformly protects bytecode try_me_else CPs,
/// indexed try/retry/trust CPs, and IL choice points — they all
/// share the same <c>_b</c> chain, so reading <c>_b + CpSize(arity)</c>
/// gives the right safety floor for any kind.</para>
/// </summary>
public class Chunk64TrimTests
{
    [Fact]
    public void Trim_OnMultiClauseCallee_PreservesCpFrame()
    {
        // The pattern that exposed the bug in the prior investigation
        // rounds: a query body whose first goal pushes a try_me_else
        // CP (because the callee is multi-clause), then a builtin
        // call inside the callee's first clause asks for the trim.
        // Without the CP-frame floor the trim would drop _stackTop
        // below the just-pushed CP and corrupt subsequent state.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public pick/1.\n" +
            "pick(X) :- atom(X).\n" +
            "pick(X) :- number(X).\n");
        engine.Query("pick(foo).");   // warm Tier 1
        var sols = engine.QueryAll("(X = foo ; X = 7), pick(X).").ToList();
        Assert.Equal(2, sols.Count);
    }

    [Fact]
    public void Trim_DoesNotBreakIlPromotedCallees()
    {
        // Repeatedly invoke a procedure with Tier-1 IL active and
        // multi-clause callees — the loop exercises trim points
        // across many backtracks and would deadlock or yield wrong
        // counts if any single trim invalidated an outstanding CP.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public color/1.\n" +
            ":- public size/1.\n" +
            "color(red). color(green). color(blue).\n" +
            "size(small). size(large).\n");
        // 3 colors * 2 sizes = 6 cross-products, exercised end-to-
        // end via the standard Tier-0 backtracker but with Tier-1
        // promotion active for color/1, size/1.
        engine.Query("color(red).");  engine.Query("size(small).");
        Assert.Equal(6, engine.QueryAll("color(_), size(_).").Count());
    }

    [Fact]
    public void Trim_DegradesToNoop_WhenCpFrameWouldClash()
    {
        // The CP-protection check makes trim degrade to a no-op
        // when shrinking would intrude on an active CP frame —
        // this is observable by the fact that a deeply nested
        // multi-clause query backtracks correctly even when every
        // intermediate level has a CP-pushing try_me_else.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public a/1.\n" +
            ":- public b/1.\n" +
            ":- public c/1.\n" +
            "a(1). a(2).\n" +
            "b(1). b(2).\n" +
            "c(1). c(2).\n");
        // 2 * 2 * 2 = 8 solutions, each one tested via a chain of
        // multi-clause backtrack returns.
        Assert.Equal(8, engine.QueryAll("a(X), b(Y), c(Z).").Count());
    }
}
