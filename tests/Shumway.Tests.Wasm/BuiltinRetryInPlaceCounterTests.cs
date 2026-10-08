using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A backtrackable builtin's choice point is retried in place: one
/// push per enumeration, however many solutions it gives, where popping it
/// and pushing it again cost one per solution. Counted by the diagnostic
/// build, which is why it lives here (see IlLocalBacktrackCounterTests).
/// </summary>
[Collection("exclusive")]
public sealed class BuiltinRetryInPlaceCounterTests(ITestOutputHelper o)
{
    private const string Corpus = """
        rp(N) :- nb_setval(rpc, 0), repeat, nb_getval(rpc, X), X1 is X + 1,
            nb_setval(rpc, X1), X1 >= N, !.
        letters(N, A) :- length(L, N), maplist(=(a), L), atom_chars(A, L).
        """;

    private long Pushes(PrologEngine engine, string goal)
    {
        Activation.DiagBuiltinCpPushes = 0;
        Assert.True(engine.Query(goal).Success, goal);
        long pushes = Activation.DiagBuiltinCpPushes;
        o.WriteLine($"{goal} -> {pushes} pushes");
        return pushes;
    }

    [DiagTheory]
    [InlineData("( between(1, N, _), fail ; true ).")]
    [InlineData("rp(N).")]
    [InlineData("numlist(1, N, L), ( nth1(_, L, _), fail ; true ).")]
    [InlineData("numlist(1, N, L), ( nth0(_, L, _), fail ; true ).")]
    [InlineData("letters(N, A), ( sub_atom(A, _, 1, _, _), fail ; true ).")]
    [InlineData("letters(N, A), ( atom_concat(_, _, A), fail ; true ).")]
    [InlineData("letters(N, A), atom_string(A, S), ( string_concat(_, _, S), fail ; true ).")]
    [InlineData("append(X, _, _), length(X, N), !.")]
    public void AnEnumerationPushesItsChoicePointOnce(string template)
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        long small = Pushes(engine, template.Replace("N", "500"));
        long large = Pushes(engine, template.Replace("N", "1000"));
        Assert.True(small >= 1, "no choice point counted: the enumeration did not run");
        Assert.Equal(small, large);
    }
}
