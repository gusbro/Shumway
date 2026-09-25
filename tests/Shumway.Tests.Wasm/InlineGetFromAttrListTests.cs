using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>'$get_from_attr_list'/3</c> answered inside the module.
///
/// <para>What every get_atts/2 does, and after functor/3 left the ranking it
/// is the heaviest exit clp(Z) makes: 810 of 1,971 builtin requests in one
/// goal, 41%. It needs nothing get_attr/3's inline form does not already
/// have -- the attribute image and its probe -- plus a walk of the heap list
/// the probe finds, so the two share the probe.</para>
///
/// <para>Compound Attr only. The builtin keys on the functor, so an atom or
/// a constant keys on something the module cannot intern, and those step
/// aside.</para></summary>
public sealed class InlineGetFromAttrListTests(ITestOutputHelper o)
{
    private const string Corpus = """
        g(V, A) :- '$get_from_attr_list'(V, m, A).

        % A list with three elements of DIFFERENT functors, plus an atom and
        % an integer among them: the walk has to step over everything whose
        % functor is not the one asked for, not just over near misses.
        loaded(V) :-
            '$put_to_attr_list'(V, m, seven),
            '$put_to_attr_list'(V, m, 42),
            '$put_to_attr_list'(V, m, bar(9)),
            '$put_to_attr_list'(V, m, foo(1, 2)),
            '$put_to_attr_list'(V, m, baz(x, y, z)).

        finds(A, B)  :- loaded(V), g(V, foo(A, B)).
        finds_last(N) :- loaded(V), g(V, bar(N)).
        % Same name, different arity: a different functor, so no match.
        misses_arity(R) :- loaded(V), ( g(V, foo(_)) -> R = yes ; R = no ).
        misses_name(R)  :- loaded(V), ( g(V, quux(_)) -> R = yes ; R = no ).
        % A bound argument inside Attr is COMPARED, not overwritten.
        checks(R)  :- loaded(V), ( g(V, foo(1, 2)) -> R = yes ; R = no ).
        rejects(R) :- loaded(V), ( g(V, foo(1, 9)) -> R = yes ; R = no ).
        % No attribute at all, and no attributes in THIS module.
        bare(R)  :- ( g(_, foo(_, _)) -> R = yes ; R = no ).
        other(R) :- loaded(V), ( '$get_from_attr_list'(V, other, foo(_, _))
                                 -> R = yes ; R = no ).
        % The shapes it declines still answer.
        atom_attr(R) :- loaded(V), ( g(V, seven) -> R = yes ; R = no ).
        int_attr(R)  :- loaded(V), ( g(V, 42) -> R = yes ; R = no ).
        """;

    [Theory]
    [InlineData("finds(A, B), A == 1, B == 2.")]
    [InlineData("finds_last(N), N == 9.")]
    [InlineData("misses_arity(R), R == no.")]
    [InlineData("misses_name(R), R == no.")]
    [InlineData("checks(R), R == yes.")]
    [InlineData("rejects(R), R == no.")]
    [InlineData("bare(R), R == no.")]
    [InlineData("other(R), R == no.")]
    [InlineData("atom_attr(R), R == yes.")]
    [InlineData("int_attr(R), R == yes.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>And it really is inline: a compound lookup leaves no builtin
    /// request behind, whether it finds the element or walks off the end.
    /// </summary>
    [DiagTheory]
    [InlineData("finds(A, B), A == 1.")]
    [InlineData("misses_name(R), R == no.")]
    [InlineData("bare(R), R == no.")]
    public void ACompoundLookupDoesNotLeaveTheModule(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>A constant Attr is answered inside too: an atom or an
    /// integer keys on ITSELF, which one cell comparison asks -- the same
    /// comparison the host makes of an atom's identity or an integer's
    /// value.</summary>
    [DiagTheory]
    [InlineData("atom_attr(R), R == yes.")]
    [InlineData("int_attr(R), R == yes.")]
    public void AConstantAttrIsAnsweredInside(string goal)
        => Assert.Equal(0L, ExitsOf(goal));

    /// <summary>The counterproof, in red: an UNBOUND Attr owes an
    /// instantiation error, and errors are the engine's. Reached through the
    /// corpus predicate, because a builtin named in the QUERY text runs
    /// interpreted and proves nothing about the module.</summary>
    [DiagFact]
    public void AnUnboundAttrStaysTheEngines()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("finds(A, B), A == 1.").Success);   // promoted
        WasmTierDelegate.ResetDiag();
        try { tiered.Query("loaded(V), g(V, _)."); }
        catch (System.Exception) { }

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "$get_from_attr_list" && arity == 3) n = hits;
        o.WriteLine($"unbound attr -> exits={n}");
        Assert.True(n > 0, "an unbound Attr was answered in the module");
    }

    /// <summary>Builtin requests for '$get_from_attr_list'/3 in one warm run.
    /// The first run is the one that promotes, so it is not the one counted.
    /// </summary>
    private long ExitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success);

        long n = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "$get_from_attr_list" && arity == 3) n = hits;
        o.WriteLine($"exits={n} deopts={WasmTierDelegate.DiagDeopts}");
        return n;
    }
}
