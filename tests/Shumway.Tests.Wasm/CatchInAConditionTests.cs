using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A <c>catch/3</c> whose goal is a builtin that raises, as the
/// condition of an if-then-else or the guard of a clause: when the recovery
/// fails the alternative runs, and when it succeeds the rest of the clause
/// does. The IL tier's guards got both wrong (ADR-031); this holds the wasm
/// tier to the same answers. Tier-0 is the oracle.</summary>
public sealed class CatchInAConditionTests
{
    private const string Corpus = @"
cond_fails(X, R) :-
    (   catch(atom_length(X, _), _, fail) -> R = then
    ;   R = else
    ).
cond_recovers(X, R) :-
    (   catch(atom_length(X, _), _, true) -> R = then
    ;   R = else
    ).
cond_binds(X, R) :-
    (   catch(atom_length(X, N), error(_, _), N = none) -> R = then(N)
    ;   R = else
    ).
guard_fails(X, R) :- catch(atom_length(X, _), _, fail), !, R = first.
guard_fails(_, second).
guard_recovers(X, R) :- catch(atom_length(X, _), _, true), !, R = first.
guard_recovers(_, second).
unmatched(X, R) :-
    (   catch(atom_length(X, _), another_ball, fail) -> R = then
    ;   R = else
    ).
outer(X, R) :- catch(unmatched(X, R), error(type_error(_, _), _), R = caught).

warm(0) :- !.
warm(N) :-
    cond_fails(abc, _), cond_recovers(abc, _), cond_binds(abc, _),
    guard_fails(abc, _), guard_recovers(abc, _), outer(abc, _),
    N1 is N - 1, warm(N1).
";

    private static string Answer(PrologEngine e, string goal)
        => string.Join(" ; ", e.QueryAll(goal).Select(
            s => string.Join(",", s.Bindings.Select(b => $"{b.Key}={b.Value}"))));

    private static readonly string[] Goals =
    {
        "cond_fails(abc, R).", "cond_fails(f(x), R).",
        "cond_recovers(abc, R).", "cond_recovers(f(x), R).",
        "cond_binds(abc, R).", "cond_binds(f(x), R).",
        "guard_fails(abc, R).", "guard_fails(f(x), R).",
        "guard_recovers(abc, R).", "guard_recovers(f(x), R).",
        "outer(abc, R).", "outer(f(x), R).",
    };

    [Fact]
    public void TheTierAnswersAsTier0()
    {
        var oracle = new PrologEngine();
        oracle.IlPromotion.Threshold = 0;
        oracle.ConsultString(Corpus);

        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query("warm(200).").Success);
        Assert.NotEmpty(members);
        foreach (string goal in Goals)
        {
            string expected = Answer(oracle, goal);
            Assert.NotEqual("", expected);
            Assert.Equal(expected, Answer(engine, goal));
        }
    }
}
