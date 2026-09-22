using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>clp(Z)'s <c>unwrap_with/3</c>, lifted out of clp(Z).
///
/// <para>Attributed to the tier's builtin requests by caller, one pair is
/// the entire run: 2,178,045 of 2,178,888 calls to <c>=../2</c> come from
/// <c>clpz$unwrap_with/3</c>. Tier 0 finishes the same goal having
/// allocated 39,408 cells in total. The predicate needs nothing from
/// clp(Z) but the <c>#</c> operator and maplist, so it can be asked the
/// question directly.</para>
///
/// <para>The clauses are its own, and the shape that matters is in the
/// first one: the head unifies the second and third arguments, so it
/// matches whenever the output is unbound and only <c>var/1</c> then
/// decides -- with a cut. A cut that does not commit, or a type test that
/// answers differently for an ATTRIBUTED variable, both turn this walk
/// into a rewalk.</para></summary>
public sealed class UnwrapWithShapeTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(lists)).
        :- use_module(library(clpfd)).
        :- op(150, fx, #).
        uw(_, V, V) :- var(V), !.
        uw(Goal, #V0, V) :- !, call(Goal, V0, V).
        uw(Goal, T0, T) :- T0 =.. [F|A0], maplist(uw(Goal), A0, A), T =.. [F|A].
        same(X, X).
        drive(N, T) :- numlist(1, N, L), uw(same, L, T).
        wrap([], []).
        wrap([X|Xs], [#(X)|Ys]) :- wrap(Xs, Ys).
        % The clp(Z) shape: the closure carries the ATOM =, and the
        % second clause meta-calls it with two arguments appended, so
        % the callee is a BUILTIN reached through call/3.
        drive_hash(N, T) :- numlist(1, N, L), wrap(L, W), uw(=, W, T).
        % The one structural difference left between this and what
        % clp(Z) hands the walk: the terms carry ATTRIBUTED variables,
        % which is what a propagator goal is made of. The first clause
        % is a var test with a cut, and its head unifies the second
        % argument with the third -- so meeting one binds an attributed
        % variable, which is a wakeup, mid-walk.
        mkattr(0, []) :- !.
        mkattr(N, [X|Xs]) :- X in 1..9, N1 is N - 1, mkattr(N1, Xs).
        drive_attr(N, T) :- mkattr(N, L), uw(same, L, T).
        """;

    /// <summary>A list of N elements is a term nested N deep, which is what
    /// this walk actually meets in clp(Z): the browser's chain was 71,414
    /// maplist frames alternating with 71,412 of unwrap_with's.</summary>
    [DiagFact]
    public void TheWalkAnswersTheSameAndCostsTheSame()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var p = plain.Query("drive(400, T), length(T, N), N == 400.");
        Assert.True(p.Success, "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        string outcome;
        try
        {
            outcome = tiered.Query("drive(400, T), length(T, N), N == 400.").Success
                ? "ok" : "failed";
        }
        catch (System.Exception ex) { outcome = ex.Message; }

        long univ = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "=.." && arity == 2) univ = hits;
        o.WriteLine($"tier: {outcome}  =../2 exits={univ} "
            + $"chains={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal("ok", outcome);
        // Two univ calls per level, 400 levels, plus the leaf. A walk that
        // rewalks cannot stay near that.
        Assert.True(univ < 4000,
            $"=../2 ran {univ} times over a 400-deep walk: it is rewalking");
    }

    /// <summary>The same walk over #-wrapped elements with = as the
    /// closure, which is what clp(Z) passes: the second clause then
    /// meta-calls a BUILTIN through call/3.</summary>
    [DiagFact]
    public void TheHashClauseMetaCallsABuiltin()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("drive_hash(200, T), length(T, N), N == 200.").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        string outcome;
        try
        {
            outcome = tiered.Query("drive_hash(200, T), length(T, N), N == 200.").Success
                ? "ok" : "failed";
        }
        catch (System.Exception ex) { outcome = ex.Message; }
        long univ = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "=.." && arity == 2) univ = hits;
        o.WriteLine($"hash tier: {outcome}  =../2 exits={univ} "
            + $"chains={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal("ok", outcome);
        Assert.True(univ < 3000,
            $"=../2 ran {univ} times over a 200-deep walk: it is rewalking");
    }

    /// <summary>And the same walk over a term carrying ATTRIBUTED
    /// variables, which is what a propagator goal is.</summary>
    [DiagFact]
    public void TheWalkOverAttributedVariables()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("drive_attr(150, T), length(T, N), N == 150.").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        WasmTierDelegate.ResetDiag();
        string outcome;
        try
        {
            outcome = tiered.Query("drive_attr(150, T), length(T, N), N == 150.").Success
                ? "ok" : "failed";
        }
        catch (System.Exception ex) { outcome = ex.Message; }
        long univ = 0;
        foreach (var (name, arity, hits) in WasmTierDelegate.BuiltinRanking())
            if (name == "=.." && arity == 2) univ = hits;
        o.WriteLine($"attr tier: {outcome}  =../2 exits={univ} "
            + $"chains={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal("ok", outcome);
        Assert.True(univ < 3000,
            $"=../2 ran {univ} times over a 150-deep walk: it is rewalking");
    }
}
