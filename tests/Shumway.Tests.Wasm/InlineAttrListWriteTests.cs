using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>'$put_to_attr_list'/3</c> and <c>'$del_from_attr_list'/3</c>
/// written from inside the module.
///
/// <para>Together they were 448 of the 730 builtin requests clp(Z) makes in
/// one goal, 61%. The write is three things and the module reaches two: it
/// writes the IMAGE, because that is what it reads back in the same chain,
/// and the TRAIL entry, because that entry has to sit in order between
/// whatever else the chain trails. The store and the attribute log it parks,
/// and the host puts them away at the next sync -- before any managed code
/// runs, which is what makes the image leading the store sound.</para>
///
/// <para>Updates only. An insert would have to place a new key in an
/// open-addressed table and keep its load factor.</para>
///
/// <para>The half no answer can show is the TRAIL: a wrong log index would
/// have an unwind restore one attribute's old value onto another. So the
/// cases here backtrack through the write and check what came back, and the
/// interleaved one checks that two attributes unwind to their own
/// values.</para></summary>
public sealed class InlineAttrListWriteTests(ITestOutputHelper o)
{
    private const string Corpus = """
        % get/2 is a stream predicate of the engine, so these are named
        % apart: a corpus that collides with a builtin is ignored with a
        % warning and every answer below it moves.
        att_put(V, A) :- '$put_to_attr_list'(V, m, A).
        att_del(V, A) :- '$del_from_attr_list'(V, m, A).
        att_get(V, A) :- '$get_from_attr_list'(V, m, A).

        % The first put on a variable is an INSERT; every one after is an
        % update, which is the shape the module takes.
        seeded(V) :- att_put(V, foo(0)), att_put(V, bar(0)).

        % Replacing an element, keeping the others.
        replaces(F, B) :- seeded(V), att_put(V, foo(1)),
                          att_get(V, foo(F)), att_get(V, bar(B)).
        % Adding one the list did not have.
        adds(F, B, Z) :- seeded(V), att_put(V, baz(9)),
                         att_get(V, foo(F)), att_get(V, bar(B)), att_get(V, baz(Z)).
        % Removing one, keeping the rest.
        removes(R, B) :- seeded(V), att_del(V, foo(_)),
                         ( att_get(V, foo(_)) -> R = still ; R = gone ),
                         att_get(V, bar(B)).
        % A delete that matches nothing changes nothing.
        del_miss(F, B) :- seeded(V), att_del(V, quux(_)),
                          att_get(V, foo(F)), att_get(V, bar(B)).
        % A delete that empties the list: a REMOVAL, which stays the host's.
        empties(R) :- att_put(V, only(1)), att_del(V, only(_)),
                      ( att_get(V, only(_)) -> R = still ; R = gone ).

        % Backtracking THROUGH the write. The write is undone, so the old
        % value is what comes back -- and it comes back from the trail
        % entry the module wrote and the log record the host parked.
        restores(Before, After) :-
            seeded(V), att_get(V, foo(Before)),
            ( att_put(V, foo(7)), att_get(V, foo(X)), X == 7, fail
            ; att_get(V, foo(After)) ).

        % Two attributes on ONE variable, written in turn and unwound
        % together: a wrong log index shows up here as one attribute
        % restoring the other's value.
        interleaved(F, B) :-
            seeded(V),
            ( att_put(V, foo(1)), att_put(V, bar(2)),
              att_get(V, foo(A)), A == 1, att_get(V, bar(C)), C == 2, fail
            ; att_get(V, foo(F)), att_get(V, bar(B)) ).

        % A SECOND module on a variable that is already attributed. This
        % is the row INSERT: the record exists, this module's row does not.
        % (The first put on a fresh variable is a different thing -- it
        % PROMOTES the cell to an attributed variable -- and stays the
        % host's.)
        other_put(V, A) :- '$put_to_attr_list'(V, n, A).
        other_get(V, A) :- '$get_from_attr_list'(V, n, A).
        second_module(F, G) :- seeded(V), other_put(V, gee(3)),
                               att_get(V, foo(F)), other_get(V, gee(G)).
        % And the two do not see each other's lists.
        modules_apart(R) :- seeded(V), other_put(V, gee(3)),
                            ( other_get(V, foo(_)) -> R = crossed ; R = apart ).

        % Two DIFFERENT variables, so the parked rows must not cross.
        two_vars(A, B) :-
            seeded(V), seeded(W),
            att_put(V, foo(1)), att_put(W, foo(2)),
            att_get(V, foo(A)), att_get(W, foo(B)).
        """;

    [Theory]
    [InlineData("replaces(F, B), F == 1, B == 0.")]
    [InlineData("adds(F, B, Z), F == 0, B == 0, Z == 9.")]
    [InlineData("removes(R, B), R == gone, B == 0.")]
    [InlineData("del_miss(F, B), F == 0, B == 0.")]
    [InlineData("empties(R), R == gone.")]
    [InlineData("restores(X, Y), X == 0, Y == 0.")]
    [InlineData("interleaved(F, B), F == 0, B == 0.")]
    [InlineData("two_vars(A, B), A == 1, B == 2.")]
    [InlineData("second_module(F, G), F == 0, G == 3.")]
    [InlineData("modules_apart(R), R == apart.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>An UPDATE is written inside: no request for it leaves.
    /// </summary>
    [DiagFact]
    public void AnUpdateIsWrittenInTheModule()
        => Assert.Equal(0L, WriteExitsOf("replaces(F, B), F == 1."));

    /// <summary>And a delete that matches nothing writes nothing, which is
    /// the cheapest correct answer there is.</summary>
    [DiagFact]
    public void ADeleteThatMatchesNothingDoesNotLeave()
        => Assert.Equal(0L, WriteExitsOf("del_miss(F, B), F == 0."));

    /// <summary>A row INSERT is written inside too: the record is already
    /// there, only this module's row is missing, and the probe knows where
    /// it would go. It spends from the budget the host staged, which is the
    /// distance to the load factor the table rebuilds at.</summary>
    [DiagFact]
    public void ARowInsertIsWrittenInTheModule()
        => Assert.Equal(0L, WriteExitsOf("second_module(F, G), G == 3."));

    /// <summary>The counterproofs, in red. A delete that empties the list
    /// REMOVES a row rather than writing one, and promoting a plain
    /// variable to an attributed one is not a row write at all. Without
    /// these the tests above would pass just as well with a module that
    /// wrote whatever it liked.</summary>
    [DiagTheory]
    [InlineData("empties(R), R == gone.")]
    public void WhatItCannotWriteStaysTheHosts(string goal)
        => Assert.True(WriteExitsOf(goal) > 0, $"{goal} was written in the module");

    /// <summary>Requests for the two writing builtins in one warm run,
    /// less the inserts that are expected to leave. Seeding a variable is
    /// ONE insert, not two: the first put creates the module's row and the
    /// second already updates it.</summary>
    private long WriteExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");

        long puts = 0, dels = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
        {
            if (name == "$put_to_attr_list" && arity == 3) puts = hits;
            if (name == "$del_from_attr_list" && arity == 3) dels = hits;
        }
        o.WriteLine($"{goal} -> put={puts} del={dels}"
            + $" deopts={WasmTierDelegate.DiagDeopts}");
        return puts + dels - SeedingInserts(goal);
    }

    private static long SeedingInserts(string goal)
        => goal.StartsWith("two_vars") ? 2 : 1;
}
