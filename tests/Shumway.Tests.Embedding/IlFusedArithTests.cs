using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The fused a_int_bin and a_int_cmp run on method locals, with
/// FusedBin and FusedCmp as the slow lane: every operand kind (a literal, a
/// register, a Y slot), every target kind (a register or a Y slot unified, a
/// first-occurrence register or Y slot stored), the lane's operators, and
/// what leaves the lane (a float, a bignum, an unbound or non-numeric
/// operand, a zero divisor, a result past 60 bits). The answers, errors
/// included, must be Tier-0's.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlFusedArithTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public IlFusedArithTests() => IlPredicateCompiler.CpsMode = false;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // Every goal ends a chunk: a variable of the head and the goal is a
    // temporary (kind 3), one of two goals a permanent (kind 4 with its cell,
    // 6 at its first occurrence), a singleton of the goal a fresh register (5).
    private const string Corpus = """
        fb_add(X, Y, R) :- R is X + Y.
        fb_sub(X, Y, R) :- R is X - Y.
        fb_mul(X, Y, R) :- R is X * Y.
        fb_div(X, Y, R) :- R is X // Y.
        fb_mod(X, Y, R) :- R is X mod Y.
        fb_min(X, Y, R) :- R is min(X, Y).
        fb_max(X, Y, R) :- R is max(X, Y).
        fb_lit(X, R) :- R is X + 7.
        fb_litl(X, R) :- R is 1000 * X.
        fb_litd(X, R) :- R is X // 0.
        fb_y6(X, R) :- Z is X - 1, fb_use(Z), R = Z.
        fb_y4(X, R) :- fb_use(R), R is X + 1.
        fb_yy(X, Y, R) :- fb_use(X), Z is X * Y, fb_use(Z), R = Z.
        fb_x5(X) :- _Z is X + 1.
        fb_same(X, R) :- R is X * X.
        fb_nonlane(X, Y, R) :- R is X / Y.
        fb_shift(X, R) :- R is X >> 1.
        fb_use(_).
        fc_lt(X, Y) :- X < Y.
        fc_le(X, Y) :- X =< Y.
        fc_gt(X, Y) :- X > Y.
        fc_ge(X, Y) :- X >= Y.
        fc_eq(X, Y) :- X =:= Y.
        fc_ne(X, Y) :- X =\= Y.
        fc_lit(X) :- X > 100.
        fc_y(X, Y) :- fb_use(X), X < Y, fb_use(Y).
        """;

    private static string Try(string goal)
        => $"catch(({goal} -> R = yes ; R = no), error(R1, _), R = R1)";

    private static readonly string[] Goals =
    {
        Try("fb_add(1, 2, R0)") + ", X = R0.",
        Try("fb_add(-1, -2, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("fb_add(M, 1, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("fb_add(N, -1, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("fb_sub(N, 1, R0)") + ", X = R0.",
        Try("fb_sub(5, 7, R0)") + ", X = R0.",
        Try("fb_mul(-3, 4, R0)") + ", X = R0.",
        "B is 2^29, " + Try("fb_mul(B, B, R0)") + ", X = R0.",
        "B is 2^30, " + Try("fb_mul(B, B, R0)") + ", X = R0.",
        "B is 2^31, " + Try("fb_mul(B, B, R0)") + ", X = R0.",
        "B is 2^31 - 1, C is -B, " + Try("fb_mul(B, C, R0)") + ", X = R0.",
        "B is 2^40, " + Try("fb_same(B, R0)") + ", X = R0.",
        Try("fb_same(7, R0)") + ", X = R0.",
        Try("fb_div(7, 2, R0)") + ", X = R0.", Try("fb_div(-7, 2, R0)") + ", X = R0.",
        Try("fb_div(7, -2, R0)") + ", X = R0.", Try("fb_div(1, 0, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("fb_div(N, -1, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("fb_div(M, -1, R0)") + ", X = R0.",
        Try("fb_mod(7, 2, R0)") + ", X = R0.", Try("fb_mod(-7, 2, R0)") + ", X = R0.",
        Try("fb_mod(7, -2, R0)") + ", X = R0.", Try("fb_mod(-7, -2, R0)") + ", X = R0.",
        Try("fb_mod(1, 0, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("fb_mod(N, 7, R0)") + ", X = R0.",
        Try("fb_min(3, 5, R0)") + ", X = R0.", Try("fb_min(-3, -5, R0)") + ", X = R0.",
        Try("fb_max(3, 5, R0)") + ", X = R0.", Try("fb_max(-3, -5, R0)") + ", X = R0.",
        Try("fb_lit(5, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("fb_lit(M, R0)") + ", X = R0.",
        Try("fb_litl(5, R0)") + ", X = R0.",
        "B is 2^50, " + Try("fb_litl(B, R0)") + ", X = R0.",
        "B is 2^58, " + Try("fb_litl(B, R0)") + ", X = R0.",
        Try("fb_litd(5, R0)") + ", X = R0.",
        // The targets: a first-occurrence Y slot, a Y slot with its cell
        // (bound to the result, to another number, unbound), two Y operands,
        // a fresh register.
        Try("fb_y6(5, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("fb_y6(M, R0)") + ", X = R0.",
        "N is -(2^59), " + Try("fb_y6(N, R0)") + ", X = R0.",
        Try("fb_y4(5, R0)") + ", X = R0.",
        Try("fb_y4(5, 6)") + ".", Try("fb_y4(5, 7)") + ".", Try("fb_y4(5, a)") + ".",
        "M is 2^59 - 1, " + Try("fb_y4(M, R0)") + ", X = R0.",
        Try("fb_yy(6, 7, R0)") + ", X = R0.",
        "B is 2^31, " + Try("fb_yy(B, B, R0)") + ", X = R0.",
        Try("fb_x5(5)") + ".", Try("fb_x5(a)") + ".",
        // The register target bound already.
        Try("fb_add(1, 2, 3)") + ".", Try("fb_add(1, 2, 4)") + ".", Try("fb_add(1, 2, a)") + ".",
        Try("fb_add(1, 2, 3.0)") + ".",
        // What leaves the lane: a float, a bignum, an unbound or non-numeric
        // operand; the operators without an integer form.
        Try("fb_add(1.5, 1, R0)") + ", X = R0.",
        Try("fb_mul(2.5, 2, R0)") + ", X = R0.",
        "B is 2^100, " + Try("fb_add(B, 1, R0)") + ", X = R0.",
        "B is 2^100, " + Try("fb_y6(B, R0)") + ", X = R0.",
        Try("fb_add(_, 1, R0)") + ", X = R0.",
        Try("fb_add(1, _, R0)") + ", X = R0.",
        Try("fb_add(a, 1, R0)") + ", X = R0.",
        Try("fb_add(f(1), 1, R0)") + ", X = R0.",
        Try("fb_y6(_, R0)") + ", X = R0.",
        Try("fb_nonlane(7, 2, R0)") + ", X = R0.", Try("fb_nonlane(6, 2, R0)") + ", X = R0.",
        Try("fb_shift(5, R0)") + ", X = R0.",
        // Comparisons: every relation on integers, a literal, two Y operands,
        // and what leaves the lane.
        Try("fc_lt(1, 2)") + ".", Try("fc_lt(2, 1)") + ".", Try("fc_lt(1, 1)") + ".",
        Try("fc_le(1, 1)") + ".", Try("fc_le(2, 1)") + ".",
        Try("fc_gt(2, 1)") + ".", Try("fc_gt(1, 2)") + ".",
        Try("fc_ge(1, 1)") + ".", Try("fc_ge(1, 2)") + ".",
        Try("fc_eq(3, 3)") + ".", Try("fc_eq(3, 4)") + ".", Try("fc_eq(-1, -1)") + ".",
        Try("fc_ne(3, 4)") + ".", Try("fc_ne(3, 3)") + ".",
        "M is 2^59 - 1, N is -(2^59), " + Try("fc_lt(N, M)") + ".",
        Try("fc_lit(101)") + ".", Try("fc_lit(100)") + ".",
        Try("fc_y(1, 2)") + ".", Try("fc_y(2, 1)") + ".",
        Try("fc_eq(1, 1.0)") + ".", Try("fc_lt(1.5, 2)") + ".", Try("fc_lt(2, 1.5)") + ".",
        "B is 2^100, " + Try("fc_lt(1, B)") + ".",
        Try("fc_lt(_, 1)") + ".", Try("fc_lt(1, a)") + ".",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "false";
        return string.Join(", ", r.Bindings.Where(kv => kv.Key is "R" or "X")
            .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    [Fact]
    public void TheFusedFormsAnswerAsTheInterpreterDoes()
    {
        var plain = new PrologEngine { Out = new StringWriter() };
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        int sites = IlPredicateCompiler.InlineFusedSites;
        var tiered = new PrologEngine { Out = new StringWriter() };
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        // Promotion is asynchronous: run and drain until every predicate the
        // goals exercise has promoted (ANTI-VACUITY), within a few rounds.
        string[] expected = { "fb_add/3", "fb_sub/3", "fb_mul/3", "fb_div/3", "fb_mod/3", "fb_min/3", "fb_max/3",
            "fb_lit/2", "fb_litl/2", "fb_litd/2", "fb_y6/2", "fb_y4/2", "fb_yy/3", "fb_x5/1", "fb_same/2",
            "fb_nonlane/3", "fb_shift/2", "fc_lt/2", "fc_le/2", "fc_gt/2", "fc_ge/2", "fc_eq/2", "fc_ne/2",
            "fc_lit/1", "fc_y/2" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            promoted.Clear();
            foreach (int fid in tiered.IlPromotion.PromotedFunctorIds())
            {
                var (atomId, arity) = FunctorTable.Lookup(fid);
                string name = AtomTable.GetById(atomId)?.Name ?? "";
                promoted.Add($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}");
            }
        }
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi),
                $"{pi} did not promote\n{tiered.IlPromotion.DescribePromotionState()}");
        Assert.True(IlPredicateCompiler.InlineFusedSites > sites, "no fused site was emitted on method locals");

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
        // And the answers are the right ones, not only the same wrong ones, on
        // both tiers: Tier-0 runs the fused helper's own integer lane.
        foreach (string check in new[] {
            "M is 2^59 - 1, fb_add(M, 1, X), X =:= 2^59.",
            "N is -(2^59), fb_sub(N, 1, X), X =:= -(2^59) - 1.",
            "B is 2^30, fb_mul(B, B, X), X =:= 2^60.",
            "B is 2^31, fb_mul(B, B, X), X =:= 2^62.",
            "B is 2^40, fb_same(B, X), X =:= 2^80.",
            "fb_div(-7, 2, -3).", "fb_div(7, -2, -3).",
            "N is -(2^59), fb_div(N, -1, X), X =:= 2^59.",
            "fb_mod(-7, 2, 1).", "fb_mod(7, -2, -1).", "fb_mod(-7, -2, -1).",
            "catch(fb_div(1, 0, _), error(evaluation_error(zero_divisor), _), true).",
            "catch(fb_litd(5, _), error(evaluation_error(zero_divisor), _), true).",
            "catch(fb_mod(1, 0, _), error(evaluation_error(zero_divisor), _), true).",
            "catch(fb_add(_, 1, _), error(instantiation_error, _), true).",
            "catch(fb_add(a, 1, _), error(type_error(evaluable, a/0), _), true).",
            "fb_add(1.5, 1, 2.5).", "fb_mul(2.5, 2, 5.0).",
            "B is 2^100, fb_add(B, 1, X), X =:= 2^100 + 1.",
            "M is 2^59 - 1, fb_y6(M, X), X =:= 2^59 - 2.",
            "N is -(2^59), fb_y6(N, X), X =:= -(2^59) - 1.",
            "fb_y4(5, 6).", "\\+ fb_y4(5, 7).", "fb_y4(5, X), X == 6.",
            "B is 2^31, fb_yy(B, B, X), X =:= 2^62.",
            "fb_add(1, 2, 3).", "\\+ fb_add(1, 2, 4).", "\\+ fb_add(1, 2, 3.0).",
            "fb_litl(5, 5000).", "B is 2^58, fb_litl(B, X), X =:= 2^58 * 1000.",
            "fc_lt(1, 2).", "\\+ fc_lt(1, 1).", "fc_le(1, 1).", "fc_eq(1, 1.0).", "\\+ fc_ne(3, 3).",
            "M is 2^59 - 1, N is -(2^59), fc_lt(N, M).",
            "B is 2^100, fc_lt(1, B).", "fc_lit(101).", "\\+ fc_lit(100).",
            "catch(fc_lt(_, 1), error(instantiation_error, _), true).",
        })
        {
            Assert.True(plain.Query(check).Success, "Tier-0: " + check);
            Assert.True(tiered.Query(check).Success, check);
        }
    }
}
