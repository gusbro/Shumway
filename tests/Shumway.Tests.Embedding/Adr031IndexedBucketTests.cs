using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-031 indexed buckets — CP-free guard commit inside INDEXED dispatch.
/// A chain node whose clause is an accepted guard skips its bucket
/// choice-point push: it records the next node's cursor in the per-member
/// <c>idxnext</c> local and branches to the clause's shared guard block;
/// guard failure dispatches on the local (tail sentinel −1 → method fail).
/// These tests pin the semantic differential across Tier-0 (oracle), runtime
/// promotion and the persisted-IL bundle.
/// </summary>
public class Adr031IndexedBucketTests
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
                var bundle = new Bundle(new[] { new BundleEntry("adr031idx", program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void KeyedBucket_GuardChain_CommitAndFallthrough(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The dispatch-then-validate idiom: bucket 'a' is a 3-clause chain
        // whose first two clauses are tierA guards. Guard failure must walk
        // the bucket (node → node → catch-all) without engine round trips;
        // key 'b' must never see bucket 'a'.
        var e = Engine(m,
            ":- public p/3.\n"
            + "p(a, X, R) :- X > 10, !, R = abig.\n"
            + "p(a, X, R) :- X > 0, !, R = asmall.\n"
            + "p(a, _, R) :- R = aneg.\n"
            + "p(b, _, R) :- R = isb.\n");
        Assert.True(e.Query("p(a, 20, R), R == abig.").Success);
        Assert.True(e.Query("p(a, 5, R), R == asmall.").Success);
        Assert.True(e.Query("p(a, -5, R), R == aneg.").Success);
        Assert.True(e.Query("p(b, 99, R), R == isb.").Success);
        Assert.Single(e.QueryAll("p(a, 20, R)."));
        Assert.Single(e.QueryAll("p(a, 5, R)."));
        Assert.Single(e.QueryAll("p(a, -5, R)."));
        Assert.Single(e.QueryAll("p(b, 0, R)."));
        // Unbound key: the var chain enumerates per ISO (clause order,
        // commits prune) — exactly one solution (clause 1 binds K=a, commits).
        Assert.True(e.Query("p(K, 20, R), K == a, R == abig.").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void VarHeadGuard_InMultipleChains_ContinuesPerChain(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The multi-chain case the idxnext local exists for: the var-head
        // guard clauses live in EVERY chain (key buckets + the var/default
        // chain), each node with a DIFFERENT next — a guard failure must
        // continue in the chain it was entered through.
        var e = Engine(m,
            ":- public q/2.\n"
            + "q(a, R) :- !, R = qa.\n"
            + "q(X, R) :- atom(X), !, R = qatom.\n"
            + "q(N, R) :- number(N), N > 0, !, R = qpos.\n"
            + "q(_, R) :- R = qother.\n");
        Assert.True(e.Query("q(a, R), R == qa.").Success);          // key chain, clause 1
        Assert.True(e.Query("q(b, R), R == qatom.").Success);       // atom, not the key
        Assert.True(e.Query("q(5, R), R == qpos.").Success);        // integer chain
        Assert.True(e.Query("q(-3, R), R == qother.").Success);     // both guards fail
        Assert.True(e.Query("q(f(1), R), R == qother.").Success);   // struct chain
        Assert.Single(e.QueryAll("q(a, R)."));
        Assert.Single(e.QueryAll("q(b, R)."));
        Assert.Single(e.QueryAll("q(5, R)."));
        Assert.Single(e.QueryAll("q(-3, R)."));
        Assert.Single(e.QueryAll("q(f(1), R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TailNodeGuard_SentinelFailsToCaller(
        Adr031CpFreeGuardTests.Mode m)
    {
        // Bucket 'a' = two guard clauses, NO catch-all: the second node is a
        // chain TAIL (idxnext = −1). Both guards failing must fail the
        // predicate (the sentinel falls through the dispatch switch), and the
        // CALLER's alternative must run.
        var e = Engine(m,
            ":- public t/2.\n"
            + "r(a, X) :- X > 10, !.\n"
            + "r(a, X) :- X > 100, !.\n"
            + "r(b, _).\n"
            + "t(X, R) :- r(a, X), !, R = got.\n"
            + "t(_, R) :- R = none.\n");
        Assert.True(e.Query("t(20, R), R == got.").Success);
        Assert.True(e.Query("t(5, R), R == none.").Success);   // both bucket guards fail
        Assert.Single(e.QueryAll("t(20, R)."));
        Assert.Single(e.QueryAll("t(5, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void DynSnapshotInIndexedGuard_StalenessFallback(
        Adr031CpFreeGuardTests.Mode m)
    {
        // ADR-034 inside an indexed bucket: the guard inlines the rule-bearing
        // dynamic rd/1's snapshot; after the first assert the clause-entry
        // test routes to the fallback, which materializes the bucket CP FROM
        // the idxnext local and calls the live predicate.
        var e = Engine(m,
            ":- public s/3.\n"
            + ":- dynamic rd/1.\n"
            + "rd(X) :- X > 0.\n"
            + "s(a, X, R) :- rd(X), !, R = pos.\n"
            + "s(a, _, R) :- R = neg.\n"
            + "s(b, _, R) :- R = isb.\n");
        Assert.True(e.Query("s(a, 5, R), R == pos.").Success);
        Assert.True(e.Query("s(a, -1, R), R == neg.").Success);
        Assert.True(e.Query("s(b, 0, R), R == isb.").Success);
        // Mutation, same query — the fallback must see the live clause AND
        // keep the bucket semantics (commit prunes; key 'b' untouched).
        Assert.True(e.Query("assertz(rd(-1)), s(a, -1, R), R == pos.").Success);
        Assert.True(e.Query("s(a, -1, R), R == pos.").Success);
        Assert.True(e.Query("s(a, -2, R), R == neg.").Success);
        Assert.True(e.Query("s(b, 0, R), R == isb.").Success);
        Assert.Single(e.QueryAll("s(a, -1, R)."));
        Assert.Single(e.QueryAll("s(a, -2, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void RegionIndexedMember_GuardBuckets(
        Adr031CpFreeGuardTests.Mode m)
    {
        // The LOCAL indexed predicate is absorbed as a region member — the
        // region driver's twin of the standalone path (region cursors in
        // idxnext, region-wide dispatch switch in the fail stub).
        var e = Engine(m,
            ":- public main/3, many/3.\n"
            + "pl(a, X, R) :- X > 10, !, R = abig.\n"
            + "pl(a, X, R) :- X > 0, !, R = asmall.\n"
            + "pl(a, _, R) :- R = aneg.\n"
            + "pl(b, _, R) :- R = isb.\n"
            + "main(K, X, R) :- pl(K, X, R).\n"
            // A second indexed member in the same region — per-member
            // idxnext locals must not interfere across a->b calls.
            + "pm(x, N, R) :- pl(a, N, S), S == abig, !, R = viax.\n"
            + "pm(x, _, R) :- R = notbig.\n"
            + "pm(y, _, R) :- R = isy.\n"
            + "many(K, X, R) :- pm(K, X, R).\n");
        Assert.True(e.Query("main(a, 20, R), R == abig.").Success);
        Assert.True(e.Query("main(a, 5, R), R == asmall.").Success);
        Assert.True(e.Query("main(a, -5, R), R == aneg.").Success);
        Assert.True(e.Query("main(b, 0, R), R == isb.").Success);
        Assert.Single(e.QueryAll("main(a, 5, R)."));

        Assert.True(e.Query("many(x, 20, R), R == viax.").Success);
        Assert.True(e.Query("many(x, 5, R), R == notbig.").Success);
        Assert.True(e.Query("many(y, 0, R), R == isy.").Success);
        Assert.Single(e.QueryAll("many(x, 20, R)."));
        Assert.Single(e.QueryAll("many(x, 5, R)."));
    }

    [Fact]
    public void KillSwitch_DisablesIndexedBucketGuards()
    {
        // With the flag off, the plain bucket-CP emission runs — same answers.
        bool old = IlPredicateCompiler.CpFreeIndexedBuckets;
        IlPredicateCompiler.CpFreeIndexedBuckets = false;
        try
        {
            var e = Engine(Adr031CpFreeGuardTests.Mode.Tier1Bundle,
                ":- public p/3.\n"
                + "p(a, X, R) :- X > 10, !, R = abig.\n"
                + "p(a, X, R) :- X > 0, !, R = asmall.\n"
                + "p(a, _, R) :- R = aneg.\n"
                + "p(b, _, R) :- R = isb.\n");
            Assert.True(e.Query("p(a, 20, R), R == abig.").Success);
            Assert.True(e.Query("p(a, -5, R), R == aneg.").Success);
            Assert.Single(e.QueryAll("p(a, 5, R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeIndexedBuckets = old;
        }
    }
}
