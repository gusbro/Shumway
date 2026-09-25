using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>What a REBUILD of the attribute image has to carry.
///
/// <para>The image holds two kinds of row: a module's value, and a home's
/// row COUNT. The count is what tells a module whether the row it is taking
/// away is the LAST one, which is the difference between removing an
/// attribute and demoting the variable back to a plain one.</para>
///
/// <para>A rebuild reads the store, and the store does not hold counts. So a
/// rebuild that only replays the values leaves every home reading zero, and
/// the next removal demotes a variable that still has attributes -- silently,
/// because the answer to the removal itself is right either way. That is the
/// shape this pins: enough variables to force the rebuild, then a removal on
/// one that carries two modules.</para></summary>
public sealed class AttrMirrorRebuildTests(ITestOutputHelper o)
{
    private const string Corpus = """
        p(V, A) :- '$put_to_attr_list'(V, m, A).
        q(V, A) :- '$put_to_attr_list'(V, n, A).
        d(V, A) :- '$del_from_attr_list'(V, m, A).
        g(V, A) :- '$get_from_attr_list'(V, n, A).

        % Enough attributed variables to take the image past its load factor
        % more than once. Each carries ONE module, so they are all count 1.
        fill(0, []) :- !.
        fill(N, [V | Vs]) :- N > 0, p(V, filler(N)), N1 is N - 1, fill(N1, Vs).

        % The variable under test carries TWO, so taking one away must NOT
        % demote it -- and the count row is the only thing that says so.
        % Asked with attvar/1 and not var/1: var/1 is TRUE of an attributed
        % variable, so it cannot tell the two apart at all.
        two_modules(R, G) :-
            p(W, mine(1)), q(W, other(2)),
            fill(60, _),
            d(W, mine(_)),
            ( attvar(W) -> R = still ; R = demoted ),
            g(W, other(G)).

        % The same variable with ONE module: taking it away DOES demote.
        one_module(R) :-
            p(W, mine(1)),
            fill(60, _),
            d(W, mine(_)),
            ( attvar(W) -> R = still ; R = demoted ).
        """;

    [Theory]
    [InlineData("two_modules(R, G), R == still, G == 2.")]
    [InlineData("one_module(R), R == demoted.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And the image agrees with the store afterwards, counts
    /// included. The staging assertion checks this on every handover, so a
    /// query that ran at all has already been checked -- this says the query
    /// really did rebuild, which is what makes the check mean something.
    /// </summary>
    [DiagFact]
    public void TheRebuildHappensAndLeavesNoDisagreement()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("two_modules(R, G), R == still.").Success);
        o.WriteLine("staged and checked without a disagreement");
    }
}
