using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>all_distinct/1 leaves in each domain exactly the values some
/// assignment of distinct values to all the variables gives: no fewer (it
/// would lose solutions) and no more. Checked against a brute-force
/// enumeration on generated instances, right after posting and after a
/// later narrowing wakes it.</summary>
public sealed class ClpfdAllDistinctTests
{
    private const string Program = """
        :- use_module(library(clpfd)).

        dom_expr([X], X).
        dom_expr([X, Y|Ys], X \/ E) :- dom_expr([Y|Ys], E).

        cell(int(K), K).
        cell(dom(Vals), V) :- dom_expr(Vals, E), V in E.

        values_of(V, L) :- ( integer(V) -> L = [V] ; findall(X, (fd_dom(V, D), X in D, label([X])), L) ).

        % The values each position takes over every assignment of distinct
        % values from the specs, after the removal (Pos-Value, or none).
        brute(Specs, Removal, Supported) :-
            findall(As, ( maplist(spec_value, Specs, As), distinct_all(As), allowed(Removal, As) ), Sols),
            length(Specs, N), numlist(1, N, Ps),
            maplist(position_values(Sols), Ps, Supported).
        spec_value(int(K), K).
        spec_value(dom(Vals), V) :- member(V, Vals).
        distinct_all(As) :- msort(As, S), length(As, N), sort(As, S2), length(S2, N), S == S2.
        allowed(none, _).
        allowed(P-V, As) :- nth1(P, As, X), X =\= V.
        position_values(Sols, P, Vals) :- findall(X, (member(As, Sols), nth1(P, As, X)), Xs), sort(Xs, Vals).

        fd(Specs, Removal, Supported) :-
            (   maplist(cell, Specs, Vs), all_distinct(Vs), remove(Removal, Vs)
            ->  maplist(values_of, Vs, Supported)
            ;   Supported = failed
            ).
        remove(none, _).
        remove(P-V, Vs) :- nth1(P, Vs, X), X #\= V.

        agree(Specs, Removal) :-
            brute(Specs, Removal, B0),
            ( member([], B0) -> B = failed ; B = B0 ),
            fd(Specs, Removal, F),
            F == B.
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        return e;
    }

    /// <summary>Instances of 2 to 5 cells over the values 1..6, some cells
    /// given as integers; a third of them with a value taken out after
    /// posting. Seeded, so a failure names a case that stays failing.</summary>
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int s = 0; s < 8; s++) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void LeavesExactlyTheSupportedValues(int seed)
    {
        var rng = new Random(seed);
        var e = Engine();
        int failedInstances = 0;
        for (int c = 0; c < 60; c++)
        {
            int n = rng.Next(2, 6);
            var specs = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (rng.Next(6) == 0) { specs.Add($"int({rng.Next(1, 7)})"); continue; }
                var vals = Enumerable.Range(1, 6).Where(_ => rng.Next(3) != 0).ToList();
                if (vals.Count == 0) vals.Add(rng.Next(1, 7));
                specs.Add($"dom([{string.Join(",", vals)}])");
            }
            string removal = rng.Next(3) == 0 ? $"{rng.Next(1, n + 1)}-{rng.Next(1, 7)}" : "none";
            string goal = $"agree([{string.Join(", ", specs)}], {removal}).";
            Assert.True(e.Query(goal).Success, goal);
            if (!e.Query($"brute([{string.Join(", ", specs)}], {removal}, B), \\+ member([], B).").Success)
                failedInstances++;
        }
        // Anti-vacuity: most instances must have solutions to compare.
        Assert.True(failedInstances < 45, $"{failedInstances} of 60 instances had no solution");
    }

    [Theory]
    // Two variables own 1 and 3, so the third is 2: no interval says so.
    [InlineData("X in 1 \\/ 3, Y in 1 \\/ 3, Z in 1..3, all_distinct([X, Y, Z])", "Z", "2")]
    [InlineData("X in 1..2, Y in 1..2, Z in 1..3, all_distinct([X, Y, Z])", "Z", "3")]
    [InlineData("X in 1 \\/ 4, Y in 1 \\/ 4, Z in 1..5, all_distinct([X, Y, Z]), fd_dom(Z, D)", "D", "2..3 \\/ 5")]
    // Too wide to list: interval reasoning still prunes.
    [InlineData("X in 1..2, Y in 1..2, Z in 1..sup, all_distinct([X, Y, Z]), fd_inf(Z, I)", "I", "3")]
    // Domains that were too wide and are no longer: the next wake prunes values again.
    [InlineData("X in 1..sup, Y in 1..sup, Z in 1..sup, all_distinct([X, Y, Z]), "
        + "X in 1 \\/ 3, Y in 1 \\/ 3, Z in 1..3", "Z", "2")]
    [InlineData("X in 1..200000, Y in 1..200000, Z in 1..200000, all_distinct([X, Y, Z]), "
        + "X in 1 \\/ 3, Y in 1 \\/ 3, Z in 1..3", "Z", "2")]
    public void Prunes(string goal, string variable, string expected)
        => Assert.True(Engine().Query($"{goal}, {variable} == ({expected}).").Success, goal);

    [Theory]
    [InlineData("X in 1 \\/ 3, Y in 1 \\/ 3, Z in 1 \\/ 3, all_distinct([X, Y, Z])")]
    [InlineData("all_distinct([1, X, 1])")]
    [InlineData("X in 1..2, Y in 1..2, Z in 1..2, all_distinct([X, Y, Z])")]
    public void Fails(string goal) => Assert.False(Engine().Query(goal + ".").Success, goal);
}
