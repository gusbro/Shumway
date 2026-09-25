using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-053, end to end: a foreign object survives exactly as long
/// as a FOREIGN cell naming it is reachable. The mechanism is pinned in
/// Shumway.Tests.Core.ForeignTableSweepTests; this is the Prolog-level
/// contract, over the two producers a program can actually reach.</summary>
public sealed class ForeignTableLifetimeTests(ITestOutputHelper o)
{
    private const string Churn = """
        churn(0) :- !.
        churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).
        """;

    /// <summary>A reftype slot held across a collection still materializes.
    /// This is the counter-proof: the sweep may only free what the trace
    /// disproved, and the slot lives in a Y slot over the collection.
    /// </summary>
    [Fact]
    public void AReftypeSlotSurvivesACollection()
    {
        var e = new PrologEngine();
        e.ConsultString(Churn + """

            held(T) :- '$new_reftype_slot'(S), fill_par(foo(1, bar), S),
                       churn(3000), garbage_collect,
                       reftype_term(T, S).
            """);
        Assert.True(e.Query("held(T), T == foo(1, bar).").Success,
            "a reftype slot held across a collection stopped resolving");
    }

    /// <summary>And the slot is still WRITABLE afterwards: materializing
    /// reads the object, but a swept-then-resurrected slot could read once
    /// and be empty next time.</summary>
    [Fact]
    public void AReftypeSlotIsStillUsableAfterACollection()
    {
        var e = new PrologEngine();
        e.ConsultString(Churn + """

            reused(T1, T2) :- '$new_reftype_slot'(S), fill_par(one, S),
                              churn(2000), garbage_collect,
                              reftype_term(T1, S),
                              fill_par(two(2), S), reftype_term(T2, S).
            """);
        Assert.True(e.Query("reused(A, B), A == one, B == two(2).").Success,
            "a reftype slot stopped accepting writes after a collection");
    }

    /// <summary>Many slots, all abandoned: the table must not grow with the
    /// loop. Asserted on heap-independent evidence -- the ids the engine
    /// hands out -- because the foreign table is not heap memory and
    /// statistics/0 cannot see it.
    ///
    /// <para>Ids are positional, so a fresh id after a collection that swept
    /// everything is LOW again. Before ADR-053 the id kept climbing with the
    /// loop, which is the leak stated as something a program can observe.
    /// </para></summary>
    [Fact]
    public void AbandonedSlotsDoNotGrowTheTable()
    {
        var e = new PrologEngine();
        e.ConsultString(Slots);
        long id = FreshSlotId(e, "slots(3000), garbage_collect, ");
        o.WriteLine($"id handed out after 3,000 abandoned slots + a collection: {id}");
        Assert.True(id < 100,
            $"the id after 3,000 abandoned slots is {id}: the table is still "
            + "growing with the loop");
    }

    private const string Slots = """
        slots(0) :- !.
        slots(N) :- '$new_reftype_slot'(_), M is N - 1, slots(M).
        """;

    /// <summary>The id the engine hands to the NEXT slot after the prefix
    /// goal. A foreign cell renders as the compound '$foreign'(N) in a
    /// binding, which is the only place the raw id is observable -- it does
    /// not UNIFY with that form, it is only written as one.</summary>
    private static long FreshSlotId(PrologEngine e, string prefix)
    {
        var r = e.Query(prefix + "'$new_reftype_slot'(S).");
        Assert.True(r.Success, prefix);
        string rendered = r.Bindings["S"].ToString()!;
        var m = System.Text.RegularExpressions.Regex.Match(rendered, @"(\d+)");
        Assert.True(m.Success, $"no id in the rendered slot: {rendered}");
        return long.Parse(m.Groups[1].Value);
    }

    /// <summary>ANTI-VACUITY: the loop really does allocate. Without a
    /// collection the id climbs with it, so the test above is measuring
    /// reclamation and not a loop that allocated nothing.</summary>
    [Fact]
    public void TheLoopReallyDoesAllocate()
    {
        var e = new PrologEngine();
        e.ConsultString(Slots);
        long id = FreshSlotId(e, "slots(3000), ");
        o.WriteLine($"id after 3,000 slots with NO collection: {id}");
        Assert.True(id >= 3000,
            $"only {id} ids were handed out for 3,000 slots");
    }

    /// <summary>A slot reachable only through a COMPOUND, not a variable:
    /// the id has to be recorded from the trace, not only from the roots.
    /// </summary>
    [Fact]
    public void ASlotNestedInATermSurvivesACollection()
    {
        var e = new PrologEngine();
        e.ConsultString(Churn + """

            nested(T) :- '$new_reftype_slot'(S), fill_par(deep(9), S),
                         W = wrapper(S, tail),
                         churn(2000), garbage_collect,
                         W = wrapper(S2, tail), reftype_term(T, S2).
            """);
        Assert.True(e.Query("nested(T), T == deep(9).").Success,
            "a reftype slot reachable only inside a compound was swept");
    }

    /// <summary>ADR-052 and ADR-053 COMPOSE: an attributed variable's
    /// attribute can hold a foreign object, and then the only path to the
    /// foreign id runs through the attribute table -- which ADR-052 just
    /// made a weak root.
    ///
    /// <para>So the chain is: a root reaches the variable, the AttVar case
    /// reaches its attribute value, and the Foreign case reaches the id. If
    /// either edge were missing the object would be swept under a live
    /// attribute, and this is the only test that exercises both at once.
    /// </para></summary>
    [Fact]
    public void AForeignObjectInsideAnAttributeSurvivesACollection()
    {
        var e = new PrologEngine();
        e.ConsultString(Churn + """

            attached(T) :- '$new_reftype_slot'(S), fill_par(in_attr(7), S),
                           put_attr(X, mymod, S),
                           churn(2500), garbage_collect,
                           get_attr(X, mymod, S2), reftype_term(T, S2).
            """);
        Assert.True(e.Query("attached(T), T == in_attr(7).").Success,
            "a foreign object reachable only through an attribute was swept");
    }

    /// <summary>And the other half of the composition: when the attributed
    /// variable itself dies, the foreign object it carried goes with it.
    /// Without this the pair would be satisfiable by never sweeping.
    /// </summary>
    [Fact]
    public void AForeignObjectInAnAbandonedAttributeIsReleased()
    {
        var e = new PrologEngine();
        e.ConsultString(Slots + """

            % The variable is abandoned behind the cut, so its attribute --
            % and the slot inside it -- are unreachable.
            abandon(0) :- !.
            abandon(N) :- ( '$new_reftype_slot'(S), put_attr(X, mymod, S),
                            nonvar(X) -> true ; true ),
                          M is N - 1, abandon(M).
            """);
        long id = FreshSlotId(e, "abandon(2000), garbage_collect, ");
        o.WriteLine($"id after 2,000 abandoned attributed slots: {id}");
        Assert.True(id < 100,
            $"the id after 2,000 abandoned attribute-held slots is {id}: "
            + "the attribute table is pinning them");
    }

    /// <summary>call_residue_vars/2 is the other producer: its snapshot is a
    /// foreign object too, and it is live for the duration of the goal.
    /// </summary>
    [Fact]
    public void ResidueVarSnapshotsSurviveACollectionInsideTheGoal()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).\n" + Churn + """

            inner(X) :- churn(2000), garbage_collect, X in 1..3.
            """);
        Assert.True(e.Query("call_residue_vars(inner(X), Vs), Vs == [X].").Success,
            "the residue-var snapshot did not survive a collection mid-goal");
    }
}
