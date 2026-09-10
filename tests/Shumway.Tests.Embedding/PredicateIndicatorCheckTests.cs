using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An instantiation_error is a PROMISE: bind what is missing and the
/// term may be admissible. So must_be/2 owes one only when something is still
/// missing AND nothing already known refutes the term. foo/_ earns it; 1/_
/// does not, because no arity makes a non-atom name into a predicate
/// indicator. That is the same reading the library already gives chars and
/// codes, where a partial list counts as insufficient only while its KNOWN
/// prefix stays compatible.
///
/// <para>abolish/1 answers 1/_ with instantiation_error instead, and that is
/// deliberate: 8.9.4.3 is an error table checked in the standard's own order,
/// which puts the variable case first. must_be/2 characterises a term rather
/// than following one builtin's table, so the two part company exactly
/// there.</para></summary>
public sealed class PredicateIndicatorCheckTests
{
    private static bool Raises(string goal, string ball)
    {
        var e = new PrologEngine();
        return e.Query($"catch({goal}, error({ball}, _), true).").Success;
    }

    /// <summary>Something missing, nothing known against it: instantiation.
    /// This is the reported bug -- these were type_error before.</summary>
    [Theory]
    [InlineData("_/_")]
    [InlineData("foo/_")]
    [InlineData("_/2")]
    public void AnUnfinishedButStillPossibleIndicator_IsInstantiation(string pi)
    {
        Assert.True(Raises($"must_be(predicate_indicator, {pi})",
                           "instantiation_error"));
        // can_be/2 accepts the very same terms: they can still become one.
        var e = new PrologEngine();
        Assert.True(e.Query($"can_be(predicate_indicator, {pi}).").Success);
    }

    /// <summary>Nothing can rescue these, so neither check may promise that
    /// binding the rest would help. The first two carry an unbound half and
    /// are the point of the distinction; the rest are the anti-vacuity guard,
    /// proving the change did not turn every verdict into instantiation.
    /// </summary>
    [Theory]
    [InlineData("1/_")]
    [InlineData("_/(-1)")]
    [InlineData("foo/bar")]
    [InlineData("1/2")]
    [InlineData("foo/(-1)")]
    [InlineData("foo(x)")]
    [InlineData("foo")]
    public void AnIndicatorNothingCanRescue_IsATypeError(string pi)
    {
        Assert.True(Raises($"must_be(predicate_indicator, {pi})",
                           "type_error(predicate_indicator, _)"));
        Assert.True(Raises($"can_be(predicate_indicator, {pi})",
                           "type_error(predicate_indicator, _)"));
    }

    /// <summary>ANTI-VACUITY: a well-formed indicator still passes, and a bare
    /// variable still reports the way it always did.</summary>
    [Fact]
    public void AWellFormedIndicatorPasses_AndABareVariableIsUnchanged()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("must_be(predicate_indicator, foo/1).").Success);
        Assert.True(e.Query("must_be(predicate_indicator, foo/0).").Success);
        Assert.True(e.Query("can_be(predicate_indicator, foo/1).").Success);
        Assert.True(e.Query("can_be(predicate_indicator, _).").Success);
        Assert.True(Raises("must_be(predicate_indicator, _)", "instantiation_error"));
    }

    /// <summary>The precedent this follows, restated so that changing one and
    /// not the other shows up here: a partial list is insufficient for
    /// must_be/2 and acceptable to can_be/2.</summary>
    [Fact]
    public void ItFollowsTheSameSplitThatListsAlreadyUse()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("can_be(list, [a|_]).").Success);
        Assert.True(Raises("must_be(list, [a|_])", "instantiation_error"));
    }


    /// <summary>abolish/1's halves are judged in turn, name first: 1/_ reports
    /// the name that can never be an atom, not "not instantiated enough".
    /// Byte-checked against GNU on all nine terms.</summary>
    [Theory]
    [InlineData("_/_",      "instantiation_error")]
    [InlineData("foo/_",    "instantiation_error")]
    [InlineData("_/2",      "instantiation_error")]
    [InlineData("_/(-1)",   "instantiation_error")]
    [InlineData("1/_",      "type_error(atom, 1)")]
    [InlineData("1/2",      "type_error(atom, 1)")]
    [InlineData("foo/bar",  "type_error(integer, bar)")]
    [InlineData("foo/(-1)", "domain_error(not_less_than_zero, -1)")]
    [InlineData("foo(x)",   "type_error(predicate_indicator, foo(x))")]
    public void Abolish_JudgesTheHalvesInTurn(string pi, string ball)
    {
        Assert.True(Raises($"abolish({pi})", ball));
    }

    /// <summary>The two agree on WHEN it is an instantiation error, which is
    /// the whole point; they differ only in which culprit each names, because
    /// must_be/2's contract is type_error(Type, Value).</summary>
    [Theory]
    [InlineData("foo/_", true)]
    [InlineData("_/2",   true)]
    [InlineData("1/_",   false)]
    [InlineData("_/(-1)", false)]
    public void AbolishAndMustBeAgreeOnWhenInstantiationIsOwed(
        string pi, bool instantiation)
    {
        string mustBeBall = instantiation
            ? "instantiation_error" : "type_error(predicate_indicator, _)";
        Assert.True(Raises($"must_be(predicate_indicator, {pi})", mustBeBall));
        // abolish/1 names the half; only the KIND has to line up, and for
        // _/(-1) the unbound name is what it reaches first.
        if (instantiation)
            Assert.True(Raises($"abolish({pi})", "instantiation_error"));
    }

    /// <summary>current_predicate/1 is deliberately NOT changed with it:
    /// 8.8.2.3 gives it a single error condition over the whole term, with no
    /// per-half cases to report, so a term that is not an indicator is
    /// type_error(predicate_indicator, PI) and nothing finer.</summary>
    [Theory]
    [InlineData("1/_")]
    [InlineData("_/(-1)")]
    public void CurrentPredicate_KeepsItsWholeTermVerdict(string pi)
    {
        Assert.True(Raises($"current_predicate({pi})",
                           "type_error(predicate_indicator, _)"));
    }
}
