using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// SWI-faithful SSU (<c>Head =&gt; Body</c>) semantics — SWI is the only
/// mainstream Prolog implementing <c>=&gt;</c>, so its behaviour is the
/// reference: single-sided head MATCHING (a pattern never binds a variable
/// of the caller's goal), commit on match(+guard), and
/// <c>existence_error(matching_rule, Goal)</c> when no rule applies.
/// </summary>
public class SsuSemanticsTests
{
    private static PrologEngine Load(string src)
    {
        var e = new PrologEngine();
        e.ConsultString(src);
        return e;
    }

    [Fact]
    public void Match_IsSingleSided_CallerVarStaysUnbound()
    {
        var e = Load("pat(s(X), R) => R = got(X).");
        // An unbound caller argument cannot be bound BY the pattern:
        // no rule matches, and the caller's variable comes back intact.
        // (The ball is a COPY per ISO catch/3, so the goal inside it carries
        // a fresh unbound var, not V itself.)
        Assert.True(e.Query(
            "catch(pat(V, _), error(E, _), true), var(V), "
            + "E = existence_error(matching_rule, pat(W, _)), var(W).").Success);
        // A sufficiently instantiated call matches and binds pattern vars.
        Assert.True(e.Query("pat(s(7), G), G == got(7).").Success);
    }

    [Fact]
    public void NoMatchingRule_RaisesExistenceError()
    {
        var e = Load("classify(0, R) => R = zero.\nclassify(1, R) => R = one.");
        Assert.True(e.Query(
            "catch(classify(9, _), error(existence_error(matching_rule, G), _), true), "
            + "G = classify(9, _).").Success);
    }

    [Fact]
    public void GuardFailure_TriesNextRule_ThenErrors()
    {
        var e = Load(
            "sign(N, S), N < 0 => S = neg.\n"
            + "sign(N, S), N > 0 => S = pos.");
        Assert.True(e.Query("sign(-2, S), S == neg.").Success);
        Assert.True(e.Query("sign(3, S), S == pos.").Success);
        // Both guards fail for 0 — no rule applies: error, not failure.
        Assert.True(e.Query(
            "catch(sign(0, _), error(existence_error(matching_rule, _), _), true).").Success);
    }

    [Fact]
    public void BodyFailure_AfterCommit_FailsWithoutError()
    {
        // A matched rule COMMITS; its body failing is plain failure (SWI):
        // neither a later rule nor the no-match trailer runs.
        var e = Load("q(N, R) => N > 0, R = pos.\nq(_, R) => R = other.");
        Assert.False(e.Query("q(0, _).").Success);
    }

    [Fact]
    public void CommitsOnFirstMatch_NoBacktrackIntoLaterRules()
    {
        var e = Load("c(0, R) => R = zero.\nc(_, R) => R = any.");
        Assert.Single(e.QueryAll("c(0, R)."));
    }
}
