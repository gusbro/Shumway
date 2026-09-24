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

        % An ATOM attribute: the walk keys on a functor of arity one or
        % more, so this one stays the host's.
        atom_attr(V) :- '$put_to_attr_list'(V, m, plain).
        declines(R) :- seeded(V), atom_attr(V),
                       ( '$get_from_attr_list'(V, m, plain) -> R = yes ; R = no ).

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
    [InlineData("declines(R), R == yes.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>Every shape of the write is made inside now: an update, a
    /// row INSERT on a variable that is already attributed, the PROMOTION
    /// of a plain one, a delete that matches nothing, and the REMOVAL that
    /// takes the last row and demotes the cell with it.</summary>
    [DiagTheory]
    [InlineData("replaces(F, B), F == 1.")]
    [InlineData("second_module(F, G), G == 3.")]
    [InlineData("del_miss(F, B), F == 0.")]
    [InlineData("removes(R, B), R == gone.")]
    [InlineData("empties(R), R == gone.")]
    [InlineData("restores(X, Y), X == 0.")]
    [InlineData("interleaved(F, B), F == 0.")]
    [InlineData("two_vars(A, B), A == 1.")]
    public void EveryShapeIsWrittenInTheModule(string goal)
        => Assert.Equal(0L, WriteExitsOf(goal));

    /// <summary>A constant attribute is written inside too: it keys on
    /// ITSELF, which one cell comparison asks.</summary>
    [DiagFact]
    public void AConstantAttrIsWrittenInside()
        => Assert.Equal(0L, WriteExitsOf("declines(R), R == yes."));

    /// <summary>The counterproof, in red: an UNBOUND attribute owes an
    /// instantiation error, and errors are the host's. Reached through the
    /// corpus predicate, because a builtin named in the QUERY text runs
    /// interpreted and proves nothing about the module.</summary>
    [DiagFact]
    public void WhatItCannotWriteStaysTheHosts()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("replaces(F, B), F == 1.").Success);  // promoted
        WasmTierDelegate.ResetDiag();
        try { tiered.Query("seeded(V), att_put(V, _)."); }
        catch (System.Exception) { }

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "$put_to_attr_list" && arity == 3) n = hits;
        o.WriteLine($"unbound attr -> exits={n}");
        Assert.True(n > 0, "an unbound attribute was written in the module");
    }

    /// <summary>Requests for the two writing builtins in one warm run.
    /// Nothing is subtracted any more: the promotion that used to leave is
    /// made inside too.</summary>
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
        return puts + dels;
    }
}
