using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-025 — the inline if-then-else / disjunction lowering (`jump` opcode +
/// try_me_else/cut/trust_me in the host clause), gated by
/// <see cref="PrologEngine.EnableInlineIte"/> (stage (c): default OFF).
/// Every semantic case runs with the flag ON; the differential test checks the
/// flag-OFF (helper) form computes identical answers.
/// </summary>
public class Adr025InlineIteTests
{
    private static PrologEngine Activation(string program, bool inline = true)
    {
        var e = new PrologEngine { EnableInlineIte = inline };
        e.ConsultString(program);
        return e;
    }

    // ---- if-then-else semantics ----

    [Fact]
    public void Ite_TakesThen_WhenCondSucceeds()
    {
        var e = Activation("classify(X, R) :- (X > 0 -> R = pos ; R = nonpos).");
        Assert.True(e.Query("classify(5, R), R == pos.").Success);
        Assert.True(e.Query("classify(-1, R), R == nonpos.").Success);
        Assert.True(e.Query("classify(0, R), R == nonpos.").Success);
    }

    [Fact]
    public void Ite_IsDeterministic_NoElseOnBacktrack()
    {
        var e = Activation("classify(X, R) :- (X > 0 -> R = pos ; R = nonpos).");
        // ISO: once the condition succeeds, the else branch is unreachable.
        Assert.True(e.Query("findall(R, classify(5, R), L), L == [pos].").Success);
        Assert.True(e.Query("findall(R, classify(0, R), L), L == [nonpos].").Success);
    }

    [Fact]
    public void Ite_CondBindings_FlowIntoThen_AndAreUndoneInElse()
    {
        var e = Activation("""
            p(1).
            p(2).
            pick(X, R) :- (p(X) -> R = found(X) ; R = none(X)).
            """);
        // Cond binds X=1 (first solution, committed); then sees the binding.
        Assert.True(e.Query("pick(X, R), X == 1, R == found(1).").Success);
        // Cond fails for X=9; the else runs with X still 9 (cond made no binding).
        Assert.True(e.Query("pick(9, R), R == none(9).").Success);
    }

    [Fact]
    public void Ite_CommitsFirstCondSolution()
    {
        var e = Activation("""
            q(a).
            q(b).
            first(R) :- (q(X) -> R = X ; R = none).
            """);
        // ISO ->/2 commits the condition's FIRST solution.
        Assert.True(e.Query("findall(R, first(R), L), L == [a].").Success);
    }

    [Fact]
    public void Ite_ConjunctionsInAllParts()
    {
        var e = Activation("r(X, R) :- (X > 0, X < 10 -> Y is X * 2, R = small(Y) ; Y is X + 100, R = big(Y)).");
        Assert.True(e.Query("r(3, R), R == small(6).").Success);
        Assert.True(e.Query("r(50, R), R == big(150).").Success);
    }

    [Fact]
    public void Ite_BareIfThen_FailsWhenCondFails()
    {
        var e = Activation("only_pos(X, R) :- (X > 0 -> R = pos).");
        Assert.True(e.Query("only_pos(1, R), R == pos.").Success);
        Assert.False(e.Query("only_pos(-1, _).").Success);
    }

    [Fact]
    public void Ite_MidClause_AndMultiplePerClause()
    {
        var e = Activation("""
            grade(N, G) :-
              (N >= 90 -> A = high ; A = low),
              (N >= 50 -> B = pass ; B = fail_grade),
              G = g(A, B).
            """);
        Assert.True(e.Query("grade(95, G), G == g(high, pass).").Success);
        Assert.True(e.Query("grade(60, G), G == g(low, pass).").Success);
        Assert.True(e.Query("grade(10, G), G == g(low, fail_grade).").Success);
    }

    // ---- plain disjunction ----

    [Fact]
    public void Disjunction_BacktracksThroughBothBranches()
    {
        var e = Activation("d(X) :- (X = a ; X = b).");
        Assert.True(e.Query("findall(X, d(X), L), L == [a, b].").Success);
        Assert.True(e.Query("d(b).").Success);
        Assert.False(e.Query("d(c).").Success);
    }

    [Fact]
    public void Disjunction_ElseSeesCleanState()
    {
        var e = Activation("s(X, R) :- (X = 1, R = one ; R = other(X)).");
        Assert.True(e.Query("s(1, R), R == one.").Success);
        // First branch fails for X=2 (X=1 unifiable? no) — bindings undone,
        // second branch sees the original X.
        Assert.True(e.Query("s(2, R), R == other(2).").Success);
        Assert.True(e.Query("findall(R, s(1, R), L), L == [one, other(1)].").Success);
    }

    // ---- ineligible shapes keep the helper path and stay correct ----

    [Fact]
    public void Ineligible_BranchCut_StillWorksViaHelper()
    {
        // A `!` in a branch needs the chunk-408 barrier — helper path.
        var e = Activation("""
            h(X, R) :- (X > 0 -> !, R = pos ; R = other).
            h(_, fallback).
            """);
        Assert.True(e.Query("h(1, R), R == pos.").Success);
        Assert.True(e.Query("findall(R, h(1, R), L), L == [pos].").Success);   // cut committed host
        Assert.True(e.Query("findall(R, h(-1, R), L), L == [other, fallback].").Success);
    }

    [Fact]
    public void Ineligible_NestedControl_StillWorks()
    {
        var e = Activation("n(X, R) :- (X > 0 -> (X > 10 -> R = big ; R = small) ; R = neg).");
        Assert.True(e.Query("n(20, R), R == big.").Success);
        Assert.True(e.Query("n(5, R), R == small.").Success);
        Assert.True(e.Query("n(-1, R), R == neg.").Success);
    }

    // ---- flag OFF (default): identical answers via the helper form ----

    [Fact]
    public void Differential_InlineVsHelper_SameAnswers()
    {
        const string program =
            "classify(X, R) :- (X > 0 -> R = pos ; R = nonpos).\n" +
            "d(X) :- (X = a ; X = b).\n" +
            "grade(N, G) :- (N >= 90 -> A = high ; A = low), G = g(A).\n";
        string[] queries =
        {
            "findall(R, classify(5, R), L), L == [pos].",
            "findall(R, classify(0, R), L), L == [nonpos].",
            "findall(X, d(X), L), L == [a, b].",
            "grade(95, G), G == g(high).",
            "grade(10, G), G == g(low).",
        };
        var inline = Activation(program, inline: true);
        var helper = Activation(program, inline: false);
        foreach (var q in queries)
        {
            try { Assert.True(inline.Query(q).Success, "inline: " + q); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            { Assert.Fail($"inline threw on '{q}': {ex.Message}"); }
            try { Assert.True(helper.Query(q).Success, "helper: " + q); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            { Assert.Fail($"helper threw on '{q}': {ex.Message}"); }
        }
    }

    // ---- Tier-1 interaction: the shape is rejected GRACEFULLY (stays Tier-0)
    //      until ADR-025 stage (b) lands IL support. ----

    [Fact]
    public void IlPromotion_RejectsInlineIteGracefully()
    {
        var e = new PrologEngine { EnableInlineIte = true };
        e.IlPromotion.Threshold = 1;
        e.ConsultString("""
            :- public c2/2.
            c2(X, R) :- (X > 0 -> R = pos ; R = nonpos).
            """);
        // Repeated calls: correct answers throughout, no crash — the predicate
        // just stays on Tier-0 (the IL describe path rejects the jump opcode).
        for (int i = 0; i < 10; i++)
        {
            Assert.True(e.Query("c2(5, R), R == pos.").Success);
            Assert.True(e.Query("c2(-5, R), R == nonpos.").Success);
        }
    }

    // ---- deep recursion through an inline ITE (frame/CP interaction) ----

    [Fact]
    public void Ite_RecursionThroughInlineIte()
    {
        var e = Activation("""
            count(0, Acc, Acc).
            count(N, Acc, R) :- N > 0, (N mod 2 =:= 0 -> A2 is Acc + N ; A2 = Acc), N2 is N - 1, count(N2, A2, R).
            """);
        // sum of evens 1..100 = 2550.
        Assert.True(e.Query("count(100, 0, R), R == 2550.").Success);
    }
}
