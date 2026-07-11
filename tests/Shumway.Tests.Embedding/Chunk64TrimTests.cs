using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 64 (revisited): env-trimming runtime gate is now ON. The
/// <see cref="Shumway.Core.Activation.TrimEnv"/> implementation grew a
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
///
/// <para>A later fix in the same area stops env trimming from running
/// on a clause's <em>last</em> goal: there the environment is the
/// caller's (a last goal allocates no frame) or is about to be
/// deallocated, so trimming it discarded the caller's still-live Y
/// slots. The last two tests guard that fix — a transform-free repro
/// and the findall/3 pattern that first surfaced it.</para>
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

    [Fact]
    public void Trim_OnLastGoalBuiltin_DoesNotCorruptTheCallerFrame()
    {
        // Regression: a clause whose last goal is a builtin (=/2 here)
        // allocates no frame, so its "current" environment is the
        // caller's. ClauseCompiler used to emit that CallBuiltin with a
        // live-permanents trim operand anyway, and the trim shrank the
        // caller's environment, discarding its still-live Y slots — so
        // the third goal here read a corrupted L. The fix emits the
        // no-trim sentinel for a last goal.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public ff/2.\n" +
            "ff(N, _) :- member(N, [1,2]), fail.\n" +
            "ff(_, L) :- L = [1,2].\n" +
            ":- public gg/2.\n" +
            "gg(M, _) :- member(M, [3,4]), fail.\n" +
            "gg(_, K) :- K = [3,4].\n");
        // L is bound by ff and must survive the gg call to reach the
        // L == [1,2] test; likewise K must survive nothing but still
        // not be corrupted by ff's earlier trim.
        Assert.True(engine.Query("ff(N, L), gg(M, K), L == [1,2].").Success);
        Assert.True(engine.Query("ff(N, L), gg(M, K), K == [3,4].").Success);
    }

    [Fact]
    public void Trim_TwoFindallsThenAGoal_KeepTheirResults()
    {
        // The same bug reached through findall/3: its synthesised
        // collect-loop helper clauses end in builtin goals, so two
        // findalls followed by a goal reading the first result used to
        // fail. This is the pattern that first surfaced the bug.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(N, member(N, [1,2]), L), " +
            "findall(M, member(M, [3,4]), K), L == [1,2].").Success);
    }
}
