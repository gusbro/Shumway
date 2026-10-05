using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic predicate that starts a query hot has the indexed
/// layout: a chain per first-argument key and one of all its clauses. Its
/// clauses come and go in place. The answers stay those of the plain chain a
/// cold predicate runs on, and the dead entries do not pile up in the chains
/// every call walks.</summary>
public sealed partial class IndexedDynamicChurnTests
{
    private const string Program = """
        :- dynamic p/2, item/2.
        fill(0) :- !.
        fill(N) :- assertz(item(k(N), N)), M is N - 1, fill(M).
        drain :- retract(item(_, _)), fail.
        drain.
        cycles(0, _) :- !.
        cycles(C, N) :- drain, fill(N), D is C - 1, cycles(D, N).
        drop_evens :- between(1, 20, I), V is I * 2, retract(item(_, V)), fail.
        drop_evens.
        visit(L) :- findall(V, (item(_, V), touch(V)), L).
        touch(V) :- ( V =:= 40 -> drop_evens ; true ).
        visit_nested(L) :- findall(V, (item(_, V), touch_nested(V)), L).
        touch_nested(V) :- ( V =:= 40 -> nested_query(drop_evens) ; true ).
        """;

    public sealed partial class Host
    {
        // A query of its own while the caller's is suspended.
        [PrologPredicate("nested_query/1")]
        public static bool NestedQuery(Activation engine, string goal) =>
            ((PrologEngine)engine.Host!).Query(goal + ".").Success;
    }

    // A predicate is compiled indexed once it is hot, at the next query's setup.
    private static PrologEngine Engine(bool hot)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;   // the chains are the interpreter's
        e.JitIndexing.Threshold = hot ? 1 : int.MaxValue;
        e.RegisterPredicates(typeof(Host));
        e.ConsultString(Program);
        e.ChainAudit = true;
        return e;
    }

    private static readonly string[] Probes =
    {
        "findall(K-V, p(K, V), R)", "findall(V, p(a, V), R)", "findall(V, p(b, V), R)",
        "findall(V, p(c, V), R)", "findall(V, p(d, V), R)", "findall(V, p(1, V), R)",
        "findall(V, p(f(_), V), R)", "findall(V, p([_|_], V), R)", "findall(V, p(zzz, V), R)",
    };

    // Each step is a query of its own; after every one the probes answer on
    // the hot engine as on the cold one.
    private static PrologEngine SameAnswersAsAPlainChain(params string[] steps)
    {
        var hot = Engine(hot: true);
        var cold = Engine(hot: false);
        foreach (string step in steps)
        {
            Assert.Equal(cold.Query(step + ".").Success, hot.Query(step + ".").Success);
            foreach (string probe in Probes)
                Assert.True($"{cold.Query(probe + ".")["R"]}" == $"{hot.Query(probe + ".")["R"]}",
                    $"after {step}: {probe} is {hot.Query(probe + ".")["R"]} on the indexed layout and "
                    + $"{cold.Query(probe + ".")["R"]} on the chain");
        }
        return hot;
    }

    private const string Warm = "p(a, _), p(b, _)";

    // A clause whose first argument is a variable is in every key's chain:
    // that of a key first seen after a retract left a dead entry among the
    // live ones, too.
    [Theory]
    [InlineData("assertz(p(a, 1)), assertz(p(_, any)), assertz(p(b, 2))", "retract(p(a, 1))")]
    [InlineData("assertz(p(a, 1)), assertz(p(b, 2)), assertz(p(_, any))", "retract(p(b, 2))")]
    [InlineData("assertz(p(_, any)), assertz(p(a, 1)), assertz(p(b, 2))", "retract(p(a, 1))")]
    [InlineData("assertz(p(a, 1)), assertz(p(_, any)), assertz(p(_, more)), assertz(p(b, 2))", "retract(p(a, 1))")]
    public void ANewKeyAfterARetract_HasTheClausesWithAVariable(string facts, string retract)
    {
        var hot = SameAnswersAsAPlainChain(facts, Warm, retract, "assertz(p(c, 3))", "retract(p(c, 3))");
        // ANTI-VACUITY: the predicate was indexed at both retracts, the new
        // key's chain made in place.
        Assert.Equal(2, hot.IndexedRetracts);
    }

    [Fact]
    public void ANewKeyAfterARetract_AnswersInTheStoresOrder()
    {
        var hot = Engine(hot: true);
        foreach (string step in new[]
                 {
                     "assertz(p(a, 1)), assertz(p(_, any)), assertz(p(b, 2))", Warm, "retract(p(a, 1))",
                     "assertz(p(c, 3))",
                 })
            Assert.True(hot.Query(step + ".").Success);
        Assert.True(hot.Query("findall(V, p(c, V), L), L == [any, 3].").Success);
    }

    // An asserta puts the new clause first in the store before its entries exist.
    [Theory]
    [InlineData("assertz(p(a, 1)), assertz(p(_, any)), assertz(p(b, 2))")]
    [InlineData("assertz(p(_, any)), assertz(p(a, 1)), assertz(p(b, 2))")]
    [InlineData("assertz(p(a, 1)), assertz(p(b, 2)), assertz(p(_, any))")]
    public void ANewKeyByAsserta_HasTheClausesWithAVariable(string facts)
    {
        var hot = SameAnswersAsAPlainChain(facts, Warm, "asserta(p(c, 3))", "retract(p(b, 2))",
            "asserta(p(d, 4))", "retract(p(d, 4))");
        Assert.Equal(2, hot.IndexedRetracts);
        Assert.True(hot.Query("findall(V, p(c, V), L), L == [3, any].").Success);
    }

    // A key of a kind the predicate has no switch for (an integer among
    // atoms): the clauses appended in place before it stay, and a retract
    // after it still takes its clause out of the running query's view.
    [Fact]
    public void AKeyOfANewKind_LosesNoClause_AndRetractsStillWork()
    {
        SameAnswersAsAPlainChain(
            "assertz(p(a, 1)), assertz(p(b, 2))", Warm, "assertz(p(_, any))", "assertz(p(1, int))",
            "assertz(p(2, int)), retract(p(2, int))",
            "assertz(p(f(x), s)), assertz(p(a, 9)), retract(p(a, 9)), retract(p(f(x), s))");
        var hot = Engine(hot: true);
        Assert.True(hot.Query("assertz(p(a, 1)), assertz(p(b, 2)).").Success);
        Assert.True(hot.Query(Warm + ".").Success);
        Assert.True(hot.Query(
            "assertz(p(_, any)), assertz(p(1, int)), findall(K-V, p(K, V), L), length(L, 4), "
            + "findall(V, p(1, V), L1), L1 == [any, int], "
            + "retract(p(1, int)), findall(V, p(1, V), L2), L2 == [any].").Success);
    }

    // Keys of every kind, a clause for all keys, the head and the tail, with
    // retracts between them.
    [Fact]
    public void AMixOfMutations_AnswersAsAPlainChain()
    {
        SameAnswersAsAPlainChain(
            "assertz(p(a, 1)), assertz(p(b, 2)), assertz(p(a, 3))", Warm,
            "retract(p(a, 1))", "asserta(p(b, 4))", "assertz(p(_, any))", "assertz(p(1, int))",
            "retract(p(b, 2))", "asserta(p(f(x), struct))", "assertz(p([h], list))", "retract(p(_, any))",
            "assertz(p(c, 5)), asserta(p(c, 6))", "retract(p(a, 3)), retract(p(b, 4))",
            "assertz(p(_, again))", "asserta(p(d, 7))", "retract(p(c, _))", "assertz(p(zzz, 8))");
    }

    // A predicate emptied and filled again, ten times in one query: before,
    // each generation stayed linked behind the next, and every call walked
    // them all.
    [Fact]
    public void ADrainedAndRefilledPredicate_DoesNotKeepItsDeadEntriesLinked()
    {
        var e = Engine(hot: true);
        Assert.True(e.Query("fill(40).").Success);
        Assert.True(e.Query("item(k(1), _), item(k(2), _).").Success);
        Assert.True(e.Query(
            "cycles(10, 40), findall(V, item(_, V), L), length(L, 40), msort(L, S), S = [1|_], last(S, 40).").Success);
        // ANTI-VACUITY: the predicate was indexed while it was drained, and
        // its dead entries were unlinked.
        Assert.True(e.IndexedRetracts >= 400, $"{e.IndexedRetracts} retracts on the indexed layout");
        Assert.True(e.IndexedSweeps >= 10, $"{e.IndexedSweeps} sweeps");

        Assert.InRange(e.IndexedDeadLinkedMax, 1, 40);
        Assert.True(e.Query("findall(V, item(k(7), V), L), L == [7].").Success);
    }

    // The logical update view: a goal that began before a retract goes on
    // seeing the clause. While it is walking the predicate nothing is
    // unlinked. The retracts themselves leave no choice point in the chains:
    // the one that holds the sweep back is the walking goal's.
    [Fact]
    public void AGoalInFlight_SeesItsViewThroughTheRetracts()
    {
        var e = Engine(hot: true);
        Assert.True(e.Query("fill(40).").Success);
        Assert.True(e.Query("item(k(1), _), item(k(2), _).").Success);
        Assert.True(e.Query("visit(L), numlist(1, 40, Up), reverse(Up, Down), L == Down.").Success);
        // ANTI-VACUITY: twenty retracts on the indexed layout, enough for a sweep.
        Assert.Equal(20, e.IndexedRetracts);
        Assert.True(e.Query("findall(V, item(_, V), L), length(L, 20).").Success);
    }

    // The same with the retracts made by a query nested in the one walking,
    // whose activation shares the buffer: on the indexed layout and on the
    // plain chain.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGoalSuspendedUnderANestedQuery_SeesItsViewThroughTheRetracts(bool hot)
    {
        var e = Engine(hot);
        Assert.True(e.Query("fill(40).").Success);
        Assert.True(e.Query("item(k(1), _), item(k(2), _).").Success);
        Assert.True(e.Query("visit_nested(L), numlist(1, 40, Up), reverse(Up, Down), L == Down.").Success);
        Assert.True(e.Query("findall(V, item(_, V), L), length(L, 20).").Success);
        // ANTI-VACUITY: the retracts ran on the layout asked for, and on the
        // chain they were enough for a sweep.
        Assert.Equal(hot ? 20 : 0, e.IndexedRetracts);
    }
}
