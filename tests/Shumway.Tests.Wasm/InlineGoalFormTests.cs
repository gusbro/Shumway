using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A meta-called goal the module already open-codes, run on the
/// GOAL's arguments instead of stepping aside.
///
/// <para>=/2 is the case that shows why this was missing. Written in a body
/// it is not a call at all -- the WAM lowers it to get/unify -- so the
/// module's inline form only ever saw the static one. Arriving through
/// call/1 it is a TERM that nobody compiled, the host dispatches it by
/// functor, and no call marker names it because it is not a predicate. The
/// meta-call knew one trick, jump to a predicate, and so it left.</para>
///
/// <para>The interpreter is the oracle throughout: these assert that the two
/// engines AGREE, never what the answer should be.</para></summary>
public sealed class InlineGoalFormTests(ITestOutputHelper o)
{
    private const string Corpus = """
        mk(X, (X = one)).
        mk2(X, Y, (X = Y)).
        both(A, B, (A = B)).

        varConst(X)   :- mk(X, G), call(G).
        varVar(X, Y)  :- mk2(X, Y, G), call(G).
        sameConst     :- mk2(a, a, G), call(G).
        diffConst     :- mk2(a, b, G), call(G).
        compound(X)   :- mk2(X, f(1, 2), G), call(G).
        twoBoundComp  :- mk2(f(1), f(1), G), call(G).
        twoBoundDiff  :- mk2(f(1), f(2), G), call(G).

        % The unify is not the clause's last goal, so what follows it has to
        % find the frame exactly as it left it.
        after(X, Z)   :- mk(X, G), call(G), Z = done.
        before(X, Z)  :- Z = first, mk(X, G), call(G).

        % A clause whose ONLY call is the meta-call builds no frame of its
        % own, which is the asymmetry that bit the carried cut.
        bare(X)       :- mk(X, G), call(G).

        % Backtracking across it: the bindings the inline form makes have to
        % be on the trail like any other.
        pick(1). pick(2). pick(3).
        chosen(N, V)  :- pick(N), mk2(V, N, G), call(G).

        % The rest of the family, reached the same way: a goal built at
        % runtime whose functor the module already open-codes.
        mkeq(A, B, (A == B)).
        mkne(A, B, (A \== B)).
        mktest(T, X, G)  :- G =.. [T, X].
        mkattr(X, M, V, get_attr(X, M, V)).

        eq(A, B)     :- mkeq(A, B, G), call(G).
        ne(A, B)     :- mkne(A, B, G), call(G).
        typ(T, X)    :- mktest(T, X, G), call(G).
        attr(X, M, V) :- mkattr(X, M, V, G), call(G).
        """;

    [Theory]
    [InlineData("varConst(X)", "X")]
    [InlineData("varVar(X, two)", "X")]
    [InlineData("compound(X)", "X")]
    [InlineData("after(X, _)", "X")]
    [InlineData("before(X, _)", "X")]
    [InlineData("bare(X)", "X")]
    public void TheTierAgreesWithTheInterpreter(string goal, string want)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);

        string q = $"findall({want}, {goal}, L), with_output_to(atom(A), writeq(L)).";
        string expected = plain.Query(q).Bindings["A"].ToString()!;
        string got = tiered.Query(q).Bindings["A"].ToString()!;
        o.WriteLine($"{goal} -> {expected}");
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("sameConst")]
    [InlineData("diffConst")]
    [InlineData("twoBoundComp")]
    [InlineData("twoBoundDiff")]
    public void SuccessAndFailureAgreeToo(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        bool expected = plain.Query($"{goal}.").Success;
        o.WriteLine($"{goal} -> {expected}");
        Assert.Equal(expected, tiered.Query($"{goal}.").Success);
    }

    /// <summary>Every binding the inline form makes has to be undone on the
    /// way back, or the second answer is built on the first one's leftovers.
    /// </summary>
    [Fact]
    public void TheBindingsUnwindOnBacktracking()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        const string Q =
            "findall(N-V, chosen(N, V), L), with_output_to(atom(A), writeq(L)).";
        string expected = plain.Query(Q).Bindings["A"].ToString()!;
        Assert.Equal("[1-1,2-2,3-3]", expected);       // the oracle, pinned
        Assert.Equal(expected, tiered.Query(Q).Bindings["A"].ToString()!);
    }

    /// <summary>An attributed variable still steps aside -- EmitUnifyTwo
    /// cannot run a wakeup -- and the answer has to be right anyway. This is
    /// the guard on "inline it" not quietly becoming "skip the hook".</summary>
    [Fact]
    public void AnAttributedVariableStillWakesItsHook()
    {
        const string P = """
            :- use_module(library(coroutining)).
            mk2(X, Y, (X = Y)).
            frozen(X, Seen) :- freeze(X, Seen = woke), mk2(X, bound, G), call(G).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        Assert.True(plain.Query("frozen(X, S), X == bound, S == woke.").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(P);
        Assert.True(tiered.Query("frozen(X, S), X == bound, S == woke.").Success,
            "the tier bound the attributed variable without running its hook");
    }

    /// <summary>==/2 and \==/2 as meta-called goals. The corpus covers the
    /// pairs the inline form DECIDES and the pairs it declines -- two bound
    /// compounds, a float -- because declining is a path of its own and it
    /// hands the instruction back to the host, which re-reads the goal out
    /// of X0.</summary>
    [Theory]
    [InlineData("eq(foo, foo)")]
    [InlineData("eq(foo, bar)")]
    [InlineData("eq(1, 1)")]
    [InlineData("eq(1, 2)")]
    [InlineData("eq(X, X)")]
    [InlineData("eq(_, _)")]
    [InlineData("eq(f(1), f(1))")]
    [InlineData("eq(f(1), f(2))")]
    [InlineData("eq(1.5, 1.5)")]
    [InlineData("eq([a,b], [a,b])")]
    [InlineData("ne(foo, bar)")]
    [InlineData("ne(foo, foo)")]
    [InlineData("ne(f(1), f(1))")]
    [InlineData("ne(X, X)")]
    [InlineData("ne(1.5, 2.5)")]
    public void ComparisonAsAMetaCalledGoalAgrees(string goal)
        => AgreeOnSuccess(goal);

    /// <summary>The type tests, built at runtime with =.. so nothing about
    /// them is static.</summary>
    [Theory]
    [InlineData("typ(var, _)")]
    [InlineData("typ(var, foo)")]
    [InlineData("typ(nonvar, foo)")]
    [InlineData("typ(nonvar, _)")]
    [InlineData("typ(atom, foo)")]
    [InlineData("typ(atom, 1)")]
    [InlineData("typ(atom, [])")]
    [InlineData("typ(integer, 1)")]
    [InlineData("typ(integer, 1.5)")]
    [InlineData("typ(integer, 123456789012345678901234567890)")]
    [InlineData("typ(float, 1.5)")]
    [InlineData("typ(float, 1)")]
    [InlineData("typ(number, 1.5)")]
    [InlineData("typ(number, foo)")]
    [InlineData("typ(atomic, foo)")]
    [InlineData("typ(atomic, f(1))")]
    [InlineData("typ(compound, f(1))")]
    [InlineData("typ(compound, [a])")]
    [InlineData("typ(compound, [])")]
    [InlineData("typ(compound, foo)")]
    public void ATypeTestAsAMetaCalledGoalAgrees(string goal)
        => AgreeOnSuccess(goal);

    private void AgreeOnSuccess(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        bool expected = plain.Query($"{goal}.").Success;
        o.WriteLine($"{goal} -> {expected}");
        Assert.Equal(expected, tiered.Query($"{goal}.").Success);
    }

    /// <summary>get_attr/3 meta-called, against a variable that has the
    /// attribute and one that does not.</summary>
    [Theory]
    [InlineData("put_attr(X, m, hello), attr(X, m, V), V == hello")]
    [InlineData("put_attr(X, m, hello), \\+ attr(X, other, _)")]
    [InlineData("attr(_, m, _)")]
    [InlineData("attr(foo, m, _)")]
    public void GetAttrAsAMetaCalledGoalAgrees(string goal)
        => AgreeOnSuccess(goal);

    /// <summary>A form that DECLINES must leave X0 holding the goal. It does
    /// not, if the arguments are copied into the registers first -- the host
    /// then re-dispatches this instruction, reads the goal's first argument
    /// where the goal should be, and raises. Two bound compounds are the
    /// pair ==/2 declines on.</summary>
    [Fact]
    public void ADecliningFormLeavesTheGoalWhereTheHostLooksForIt()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        const string Q = "findall(X, (member(X, [1, 2]), eq(f(X), f(X))), L), "
            + "with_output_to(atom(A), writeq(L)).";
        string expected = plain.Query(Q).Bindings["A"].ToString()!;
        Assert.Equal("[1,2]", expected);              // the oracle, pinned
        Assert.Equal(expected, tiered.Query(Q).Bindings["A"].ToString()!);
    }

    /// <summary>The counter-proof: the meta-called =/2 stops leaving.</summary>
    [DiagFact]
    public void TheMetaCalledUnifyStopsSteppingAside()
    {
        var (engine, _) = TieredEngine.Build("""
            mk2(X, Y, (X = Y)).
            spin(0, []).
            spin(N, [V|R]) :- N > 0, atom_length(ab, _),
                              mk2(V, N, G), call(G),
                              N1 is N - 1, spin(N1, R).
            """);
        Assert.True(engine.Query("spin(20, _).").Success);   // warm the markers
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("spin(20, L), length(L, 20).").Success);

        long lens = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "atom_length" && a == 2) lens = hits;
        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} atom_length/2={lens}");
        Assert.True(lens >= 20, $"the clause never ran on the tier ({lens} exits)");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>The same counter for the rest of the family: a comparison and
    /// a type test, both built at runtime, both meta-called.</summary>
    [DiagFact]
    public void TheRestOfTheFamilyStopsSteppingAsideToo()
    {
        var (engine, _) = TieredEngine.Build("""
            mkeq(A, B, (A == B)).
            mktest(T, X, G) :- G =.. [T, X].
            spin(0).
            spin(N) :- N > 0, atom_length(ab, _),
                       mkeq(N, N, G1), call(G1),
                       mktest(integer, N, G2), call(G2),
                       N1 is N - 1, spin(N1).
            """);
        Assert.True(engine.Query("spin(20).").Success);      // warm the markers
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("spin(20).").Success);

        long lens = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == "atom_length" && a == 2) lens = hits;
        o.WriteLine($"deopts={WasmTierDelegate.DiagDeopts} atom_length/2={lens}");
        Assert.True(lens >= 20, $"the clause never ran on the tier ({lens} exits)");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }
}
