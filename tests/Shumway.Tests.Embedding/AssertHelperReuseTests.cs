using System.IO;
using System.Reflection;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A rule asserted at run time lowers its control constructs to helper
/// predicates named by the engine's sequence; a helper's name is a permanent
/// atom (ADR-003), and the late-helper registry keeps its code for the engine's
/// life. A rule asserted again (the assert/retract cycle of a rules engine), or
/// derived again at a query's setup, takes the helpers its earlier variant took,
/// so the atom table, the functor table and the registry stay bounded by the
/// distinct rules — and the rule behaves as it always did: on both tiers, under
/// the logical update view, in clause/2 and listing/1.
/// <para>Measured on the engine, not on the global tables: every helper name is
/// <c>$kind_N</c> for an N the engine's sequence handed out, so a sequence that
/// does not advance is a helper name, atom and functor not minted — and the
/// tables other tests intern into meanwhile say nothing about this engine.</para></summary>
public sealed class AssertHelperReuseTests
{
    // Every kind of construct the transform lowers to a helper.
    private const string Rule =
        "assertz((r(X, L) :- ( X > 0 -> S = pos ; S = neg ), \\+ S == none,"
        + " once(member(S, [pos, neg])), findall(Z, (member(Z, [1, 2, 3]), Z >= X), L)))";

    private static PrologEngine Engine(int ilThreshold = 0)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = ilThreshold;
        e.ConsultString(":- dynamic(r/2). :- dynamic(q/1).");
        return e;
    }

    private static int RegisteredHelpers(PrologEngine e)
    {
        var field = typeof(PrologEngine).GetField(
            "_runtimeAssertHelperPreds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.ICollection)field.GetValue(e)!).Count;
    }

    // The engine's helper-id sequence (NextMetaHelperId): the names of the
    // helpers it has minted so far.
    private static int HelperIds(PrologEngine e)
    {
        var field = typeof(PrologEngine).GetField(
            "_metaHelperSeq", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)field.GetValue(e)!;
    }

    private static (int HelperIds, int Helpers) Counts(PrologEngine e)
        => (HelperIds(e), RegisteredHelpers(e));

    private static void AssertBounded((int HelperIds, int Helpers) before, PrologEngine e)
    {
        var after = Counts(e);
        Assert.True(before.HelperIds == after.HelperIds,
            $"helper names minted: the id sequence advanced from {before.HelperIds} to {after.HelperIds}");
        Assert.True(before.Helpers == after.Helpers,
            $"registered helpers grew from {before.Helpers} to {after.Helpers}");
    }

    [Fact]
    public void AnAssertRetractLoopInOneQueryMintsNoHelpers()
    {
        var e = Engine();
        Assert.True(e.Query($"{Rule}, retract((r(_, _) :- _)).").Success);
        Assert.True(e.Query($"{Rule}, retract((r(_, _) :- _)).").Success);
        var before = Counts(e);

        Assert.True(e.Query($"between(1, 300, _), {Rule}, retract((r(_, _) :- _)), fail ; true.").Success);

        AssertBounded(before, e);
        Assert.True(e.Query($"{Rule}, r(2, L), L == [2, 3].").Success);
    }

    [Fact]
    public void AnAssertRetractCycleAcrossQueriesMintsNoHelpers()
    {
        var e = Engine();
        for (int round = 0; round < 2; round++)
        {
            Assert.True(e.Query($"{Rule}.").Success);
            Assert.True(e.Query("r(2, L), L == [2, 3].").Success);
            Assert.True(e.Query("retract((r(_, _) :- _)).").Success);
        }
        var before = Counts(e);

        for (int round = 0; round < 50; round++)
        {
            Assert.True(e.Query($"{Rule}.").Success);
            // The setup derives the predicate from the store again.
            Assert.True(e.Query("r(2, L), L == [2, 3].").Success);
            Assert.True(e.Query("r(-1, L), L == [1, 2, 3].").Success);
            Assert.True(e.Query("retract((r(_, _) :- _)).").Success);
        }

        AssertBounded(before, e);
    }

    [Fact]
    public void AModulesOwnDynamic_TakesTheHelpersItsSetupDerivationTakes()
    {
        // A dynamic a module names (ADR-038, amendment) compiles in the
        // module's context, and the setup derives it under the module's
        // name: the assert's memo is keyed the same way, or every setup
        // after an assert minted the helpers again.
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- module(ahr_m, [go/2]).\n:- dynamic(rule/2).\ngo(X, L) :- rule(X, L).\n");
        const string rule =
            "assertz(ahr_m:(rule(X, L) :- ( X > 0 -> S = pos ; S = neg ), \\+ S == none,"
            + " findall(Z, (member(Z, [1, 2, 3]), Z >= X), L)))";
        // The assert takes the rule's helpers; the first setup after it,
        // deriving the predicate under the module's name, finds them.
        Assert.True(e.Query($"{rule}.").Success);
        var before = Counts(e);
        Assert.True(e.Query("go(2, L), L == [2, 3].").Success);
        AssertBounded(before, e);
        Assert.True(e.Query("retract(ahr_m:(rule(_, _) :- _)).").Success);

        for (int round = 0; round < 20; round++)
        {
            Assert.True(e.Query($"{rule}.").Success);
            Assert.True(e.Query("go(2, L), L == [2, 3].").Success);
            Assert.True(e.Query("go(-1, L), L == [1, 2, 3].").Success);
            Assert.True(e.Query("retract(ahr_m:(rule(_, _) :- _)).").Success);
        }

        AssertBounded(before, e);
    }

    [Fact]
    public void ARuleAssertedBesideAVariantSharesItsHelpers()
    {
        var e = Engine();
        Assert.True(e.Query("assertz((q(X) :- (X = 1 ; X = 2))).").Success);
        var before = Counts(e);
        Assert.True(e.Query("assertz((q(Y) :- (Y = 1 ; Y = 2))).").Success);
        Assert.Equal(before, Counts(e));
        Assert.True(e.Query("findall(X, q(X), L), L == [1, 2, 1, 2].").Success);

        // A rule that is not a variant takes its own.
        Assert.True(e.Query("assertz((q(Y) :- (Y = 10 ; Y = 20))).").Success);
        Assert.Equal(before.Helpers + 1, RegisteredHelpers(e));
        Assert.True(e.Query("findall(X, q(X), L), L == [1, 2, 1, 2, 10, 20].").Success);

        // Each clause runs on its own; a retracted variant leaves the other one.
        Assert.True(e.Query("retract((q(_) :- (_ = 1 ; _ = 2))).").Success);
        Assert.True(e.Query("findall(X, q(X), L), L == [1, 2, 10, 20].").Success);
        Assert.True(e.Query("retract((q(_) :- (_ = 10 ; _ = 20))).").Success);
        Assert.True(e.Query("findall(X, q(X), L), L == [1, 2].").Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AReassertedRuleBehavesAsItDid(int ilThreshold)
    {
        var e = Engine(ilThreshold);
        const string check =
            "r(2, L1), L1 == [2, 3], r(-1, L2), L2 == [1, 2, 3], \\+ r(0, []),"
            + " findall(S, (member(V, [3, -3]), r(V, S)), Ss), Ss == [[3], [1, 2, 3]]";
        for (int round = 0; round < 3; round++)
        {
            Assert.True(e.Query($"{Rule}.").Success);
            Assert.True(e.Query($"{check}.").Success);
            Assert.True(e.Query($"{check}, {check}.").Success);
            Assert.True(e.Query("retract((r(_, _) :- _)).").Success);
            Assert.True(e.Query("\\+ r(_, _).").Success);
        }
        // A cut inside a branch cuts the clause, as it did before: the
        // else-branch alternatives are gone, the second clause is not.
        for (int round = 0; round < 2; round++)
        {
            Assert.True(e.Query("assertz((q(X) :- ( X = 1, ! ; X = 2 ; X = 3 ))).").Success);
            Assert.True(e.Query("assertz(q(4)).").Success);
            Assert.True(e.Query("findall(X, q(X), L), L == [1].").Success);
            Assert.True(e.Query("findall(X, (q(X), X > 1), L), L == [].").Success);
            Assert.True(e.Query("retract((q(_) :- _)), retract(q(4)).").Success);
        }
    }

    [Fact]
    public void AnOlderCallKeepsRunningTheRuleItStartedOn()
    {
        var e = Engine();
        Assert.True(e.Query("assertz((q(X) :- (X = 1 ; X = 2 ; X = 3))).").Success);
        // Retracted and asserted again, as a variant, inside the disjunction:
        // the running call completes on the clause it started on (the logical
        // update view), the next call runs the new one.
        Assert.True(e.Query(
            "findall(X, (q(X), (X == 1 -> retract((q(_) :- _)),"
            + " assertz((q(Y) :- (Y = 1 ; Y = 2 ; Y = 3))) ; true)), L), L == [1, 2, 3],"
            + " findall(X, q(X), L2), L2 == [1, 2, 3].").Success);
        // The same, with a rule that is not a variant.
        Assert.True(e.Query(
            "findall(X, (q(X), (X == 1 -> retract((q(_) :- _)),"
            + " assertz((q(Y) :- (Y = 10 ; Y = 20))) ; true)), L), L == [1, 2, 3],"
            + " findall(X, q(X), L2), L2 == [10, 20].").Success);
    }

    [Fact]
    public void ClauseAndListingShowTheRuleAsWritten()
    {
        var e = Engine();
        Assert.True(e.Query($"{Rule}.").Success);
        Assert.True(e.Query($"{Rule}.").Success);
        Assert.True(e.Query(
            "findall(B, clause(r(_, _), B), Bs), Bs = [B1, B2], B1 =@= B2,"
            + " B1 = ((_ -> _ ; _), \\+ _, once(_), findall(_, _, _)).").Success);
        var sw = new StringWriter();
        e.Out = sw;
        Assert.True(e.Query("listing(r/2).").Success);
        string listing = sw.ToString();
        Assert.Contains("->", listing);
        Assert.Contains("findall", listing);
        Assert.DoesNotContain("$disj", listing);
        Assert.DoesNotContain("$neg", listing);
    }

    [Fact]
    public void AReconsultOfTheSameTextMintsNoHelpers()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        const string text =
            "sign(X, S) :- ( X > 0 -> S = pos ; X < 0 -> S = neg ; S = zero ).\n"
            + "odd(X) :- \\+ 0 is X mod 2.\n"
            + "evens(L, Es) :- findall(E, (member(E, L), \\+ odd(E)), Es).\n";
        for (int round = 0; round < 2; round++)
        {
            e.ReconsultString(text);
            Assert.True(e.Query("sign(-3, S), S == neg, evens([1, 2, 3, 4], Es), Es == [2, 4].").Success);
        }
        var before = Counts(e);

        for (int round = 0; round < 20; round++)
        {
            e.ReconsultString(text);
            Assert.True(e.Query("sign(0, S), S == zero, odd(3), evens([5, 6], Es), Es == [6].").Success);
        }

        AssertBounded(before, e);
    }

    [Fact]
    public void AModeDeclaredLaterReachesTheReassertedRule()
    {
        var e = Engine();
        Assert.True(e.Query("assertz((q(X) :- (X = 1 ; X = 2))).").Success);
        Assert.True(e.Query("findall(X, q(X), L), L == [1, 2].").Success);
        Assert.True(e.Query("retract((q(_) :- _)).").Success);
        // Deterministic modes append a cut to each clause of the predicate: the
        // memoized transform is stale and is not taken.
        e.ConsultString(":- mode q(-) is semidet.");
        Assert.True(e.Query("assertz((q(X) :- (X = 1 ; X = 2))).").Success);
        Assert.True(e.Query("findall(X, q(X), L), L == [1].").Success);
    }

    [Fact]
    public void ARunWithSsuRulesKeepsItsNoMatchTrailer()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(
            ":- dynamic(s/2).\n"
            + "s(X, S), X > 0 => ( X > 10 -> S = big ; S = small ).\n"
            + "s(0, S) => S = zero.\n");
        Assert.True(e.Query("s(20, S1), S1 == big, s(3, S2), S2 == small, s(0, S3), S3 == zero.").Success);
        Assert.True(e.Query("catch(s(-1, _), error(existence_error(matching_rule, _), _), true).").Success);
    }
}
