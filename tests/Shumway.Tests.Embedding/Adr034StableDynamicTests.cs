using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-034 — sound stable-dynamic inlining. A rule-bearing dynamic predicate
/// (the Arity <c>:- visible</c> idiom: rules declared dynamic only for
/// findall/setof meta-call visibility, never mutated in practice) may be
/// inlined into a caller's CP-free guard as its ADR-023 snapshot — but ONLY
/// with a clause-entry staleness test: the first assert/retract on the
/// predicate flips the caller to an un-inlined fallback path whose call
/// dispatches against the LIVE dynamic. These tests pin:
///
/// <para>1. THE BUG THAT MOTIVATED THE ADR (probe4, 2026-07-10): a persisted
/// IL bundle inlined a dynamic snapshot into a static caller with no eviction
/// path — <c>assertz(r(-1)), g(-1, R)</c> answered the stale <c>no</c> where
/// Tier-0/WAM answers <c>yes</c> (ISO logical update view).</para>
///
/// <para>2. Fact-only dynamics (the real assert targets) are never
/// caller-inlined at all.</para>
///
/// <para>3. A guard that could MUTATE the database is never combined with an
/// inlined snapshot (the staleness window).</para>
/// </summary>
public class Adr034StableDynamicTests
{
    public static TheoryData<Adr031CpFreeGuardTests.Mode> Modes => new()
    {
        Adr031CpFreeGuardTests.Mode.Tier0,
        Adr031CpFreeGuardTests.Mode.Tier1Runtime,
        Adr031CpFreeGuardTests.Mode.Tier1Bundle,
    };

    private static PrologEngine Engine(Adr031CpFreeGuardTests.Mode m, string program)
    {
        switch (m)
        {
            case Adr031CpFreeGuardTests.Mode.Tier0:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 0;
                e.ConsultString(program);
                return e;
            }
            case Adr031CpFreeGuardTests.Mode.Tier1Runtime:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 1;
                e.ConsultString(program);
                return e;
            }
            default:
            {
                var bundle = new Bundle(new[] { new BundleEntry("adr034", program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    private const string RuleDynProgram =
        ":- public g/2.\n"
        + ":- dynamic r/1.\n"
        + "r(X) :- X > 0.\n"
        + "g(X, R) :- r(X), !, R = yes.\n"
        + "g(_, R) :- R = no.\n";

    [Theory]
    [MemberData(nameof(Modes))]
    public void StaleSnapshot_MutationFlipsCallerToLivePath(
        Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m, RuleDynProgram);
        // Fast path (snapshot, where inlined): the shipped rule decides.
        Assert.True(e.Query("g(5, R), R == yes.").Success);
        Assert.True(e.Query("g(-1, R), R == no.").Success);
        Assert.Single(e.QueryAll("g(5, R)."));
        // THE BUG: assert a clause the snapshot doesn't have, same query —
        // the caller must see it (ISO logical update view: the call to g —
        // and inside it, to r — begins after the assert).
        Assert.True(e.Query("assertz(r(-1)), g(-1, R), R == yes.").Success);
        // Cross-query: the predicate stays on the live path for the rest of
        // the engine's lifetime.
        Assert.True(e.Query("g(-1, R), R == yes.").Success);
        Assert.Single(e.QueryAll("g(-1, R)."));
        // The unmutated behaviour is otherwise unchanged.
        Assert.True(e.Query("g(5, R), R == yes.").Success);
        Assert.True(e.Query("g(-2, R), R == no.").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StaleSnapshot_RetractFlipsToo(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m, RuleDynProgram);
        Assert.True(e.Query("g(5, R), R == yes.").Success);
        // Retract the only rule → r/1 has no clauses → g falls to clause 2.
        Assert.True(e.Query("retract((r(X) :- X > 0)), g(5, R), R == no.").Success);
        Assert.True(e.Query("g(5, R), R == no.").Success);
        Assert.Single(e.QueryAll("g(5, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FactOnlyDynamic_NeverInlined_AssertVisible(
        Adr031CpFreeGuardTests.Mode m)
    {
        // f/1 is a FACT-ONLY dynamic — a real assert target. It must never be
        // caller-inlined (neither by the guard tiers nor by the leaf/fact
        // inliners), so a later assert is visible with no staleness machinery.
        var e = Engine(m,
            ":- public h/2.\n"
            + ":- dynamic f/1.\n"
            + "f(1).\n"
            + "f(2).\n"
            + "h(X, R) :- f(X), !, R = seen.\n"
            + "h(_, R) :- R = none.\n");
        Assert.True(e.Query("h(1, R), R == seen.").Success);
        Assert.True(e.Query("h(7, R), R == none.").Success);
        Assert.True(e.Query("assertz(f(7)), h(7, R), R == seen.").Success);
        Assert.True(e.Query("h(7, R), R == seen.").Success);
        Assert.Single(e.QueryAll("h(7, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void MutationInGuard_PlusSnapshot_NotCombined(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The staleness-window shape: the SAME guard asserts to r/1 and then
        // calls it. The clause-entry test runs before the assert, so an
        // inlined snapshot would be stale by the call — the recogniser must
        // reject the combination and keep the clause on the plain path, where
        // the call dispatches live. k(-5): assertz(r(-5)) then r(-5) succeeds
        // via the NEW fact (the shipped rule -5 > 0 fails).
        var e = Engine(m,
            ":- public k/2.\n"
            + ":- dynamic r/1.\n"
            + "r(X) :- X > 0.\n"
            + "k(X, R) :- assertz(r(X)), r(X), !, R = y.\n"
            + "k(_, R) :- R = n.\n");
        Assert.True(e.Query("k(-5, R), R == y.").Success);
        Assert.Single(e.QueryAll("k(-5, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EmptyDynamic_StaysOnLiveDispatch(
        Adr031CpFreeGuardTests.Mode m)
    {
        // Empty-dynamic-as-fail was MEASURED AND REJECTED (see ADR-034): in
        // any reasonable program the assert happens, so the steady state
        // would be the plain path plus a per-entry probe — a net cost. This
        // pins the plain behaviour: a guard call to an empty dynamic keeps
        // the live dispatch, and asserts are visible with no extra machinery.
        var e = Engine(m,
            ":- public g/2.\n"
            + ":- dynamic e/1.\n"
            + "g(X, R) :- e(X), !, R = found.\n"
            + "g(_, R) :- R = none.\n");
        Assert.True(e.Query("g(1, R), R == none.").Success);
        Assert.True(e.Query("assertz(e(1)), g(1, R), R == found.").Success);
        Assert.True(e.Query("g(1, R), R == found.").Success);
        Assert.True(e.Query("g(2, R), R == none.").Success);
        Assert.Single(e.QueryAll("g(1, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void SnapshotInsideG3Inner_CollectedTransitively(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The dynamic is reached one level DOWN: the guard calls valid/1,
        // whose body calls the dynamic r/1 (a G3 inner). The staleness fid
        // must be collected transitively so the CALLER clause carries the
        // test.
        var e = Engine(m,
            ":- public v/2.\n"
            + ":- dynamic r/1.\n"
            + "r(X) :- X > 0.\n"
            + "valid(X) :- integer(X), r(X).\n"
            + "v(X, R) :- valid(X), !, R = ok.\n"
            + "v(_, R) :- R = bad.\n");
        Assert.True(e.Query("v(5, R), R == ok.").Success);
        Assert.True(e.Query("v(-1, R), R == bad.").Success);
        Assert.True(e.Query("assertz(r(-1)), v(-1, R), R == ok.").Success);
        Assert.True(e.Query("v(-1, R), R == ok.").Success);
        Assert.True(e.Query("v(-2, R), R == bad.").Success);
        Assert.Single(e.QueryAll("v(-1, R)."));
    }
}
