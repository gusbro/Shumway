using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-049: compiled code leaves a wake test out where the queue is
/// known empty, from a clause's entry or the last test over code that binds
/// no existing variable and calls nothing, and keeps the test after every
/// binding, builtin and call. Each frozen goal here binds a second variable
/// the goal after the binding reads, so a test left out where it was needed
/// answers differently from Tier-0. Exclusive: the counter and the switch
/// are statics of the compiler.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlWakeHoistTests : IDisposable
{
    private readonly bool _savedCps = IlPredicateCompiler.CpsMode;
    private readonly bool _savedHoist = IlPredicateCompiler.HoistWakeTests;

    public IlWakeHoistTests() => IlPredicateCompiler.CpsMode = false;

    public void Dispose()
    {
        IlPredicateCompiler.CpsMode = _savedCps;
        IlPredicateCompiler.HoistWakeTests = _savedHoist;
    }

    private const string Corpus = """
        :- use_module(library(coroutining)).
        wa(1, Y, R) :- R is Y + 1.
        wb(X, Y, R) :- R is X + Y.
        wc1(X) :- X = 1.
        wc(X, Y, R) :- wc1(X), R is Y + 1.
        wd(1, Y) :- !, Y == 9.
        wd(_, _).
        we(X, Y) :- atom(X), Y = 1.
        we(_, 2).
        wf(1, _, first) :- fail.
        wf(_, Y, second) :- var(Y).
        wg(X, Y, R) :- ( X = 1 -> R is Y + 1 ; R = no ).
        wt2(A, B, R) :- R = A, B = 2.
        wt1(X, Y, R) :- X1 is X - 1, wt2(X1, Y, R1), Y1 is Y - 1, R is R1 + Y1.
        wi2(Y, R) :- R is Y * 2.
        wi(X, Y, R) :- X = 1, wi2(Y, R).
        wj(X, Y, R) :- X > 0, Y = 1, wj2(X, R).
        wj2(X, R) :- R is X + 1.
        wk([X|_], Y, R) :- R is Y + X.
        """;

    private static readonly string[] Goals =
    {
        // a: the head binds the frozen variable; the arithmetic reads what
        // the hook bound.
        "freeze(X, Y = 5), wa(X, Y, R).",
        // b: nothing binds before the arithmetic.
        "freeze(X, true), wb(2, 3, R).",
        "freeze(X, Y = 1), wb(X, Y, R).",
        // c: the callee binds and returns without waking.
        "freeze(X, Y = 7), wc(X, Y, R).",
        // d: the head binds; the cut fires the wake before committing.
        "freeze(X, Y = 9), wd(X, Y), R = X.",
        // e: a type test after a prefix that binds nothing.
        "freeze(X, true), we(X, R).", "we(a, R).",
        // f: the first head binds, then fails; the second sees it undone.
        "freeze(X, Y = 3), wf(X, Y, R).",
        // g: an inline if-then-else whose condition binds.
        "freeze(X, Y = 10), wg(X, Y, R).", "wg(2, _, R).",
        // h: a body of arithmetic into fresh variables and calls.
        "freeze(Y, Z = 100), wt1(5, Y, R0), R is R0 - Z.",
        // i: an inline unification binds, then a call.
        "freeze(X, Y = 4), wi(X, Y, R).",
        // j: a comparison, a unification that wakes, a call.
        "freeze(Y, Z = 6), wj(3, Y, R0), R is R0 - Z.",
        // k: a list head binds the frozen variable.
        "freeze(L, Y = 20), wk(L, Y, R).",
    };

    // An error is an answer too (the arithmetic over a frozen variable).
    private static string Wrap(string goal) => $"catch(({goal[..^1]}), error(R1, _), R = R1).";

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(Wrap(goal));
        if (!r.Success) return "false";
        return string.Join(", ", r.Bindings.Where(kv => kv.Key is "R")
            .Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    private static PrologEngine Promoted()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Corpus);
        string[] expected = { "wa/3", "wb/3", "wc/3", "wd/2", "we/2", "wf/3", "wg/3", "wt1/3", "wi/3", "wj/3", "wk/3" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) e.Query(Wrap(g));
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            promoted.Clear();
            foreach (int fid in e.IlPromotion.PromotedFunctorIds())
            {
                var (atomId, arity) = FunctorTable.Lookup(fid);
                string name = AtomTable.GetById(atomId)?.Name ?? "";
                promoted.Add($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}");
            }
        }
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi), $"{pi} did not promote\n{e.IlPromotion.DescribePromotionState()}");
        return e;
    }

    [Fact]
    public void TheHoistedCodeWakesWhereTheInterpreterDoes()
    {
        var plain = new PrologEngine { Out = new StringWriter() };
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        // The answers the frozen goals shape, on Tier-0; an attributed
        // variable in arithmetic is an unbound one.
        Assert.Equal("R = 6", Answer(plain, Goals[0]));
        Assert.Equal("R = instantiation_error", Answer(plain, Goals[2]));
        Assert.Equal("R = 8", Answer(plain, Goals[3]));
        Assert.Equal("R = second", Answer(plain, Goals[7]));
        Assert.Equal("R = 11", Answer(plain, Goals[8]));
        Assert.Equal("R = -95", Answer(plain, Goals[10]));
        Assert.Equal("R = 8", Answer(plain, Goals[11]));
        Assert.Equal("R = -2", Answer(plain, Goals[12]));

        IlPredicateCompiler.HoistWakeTests = true;
        int hoistedBefore = IlPredicateCompiler.HoistedWakeTests;
        var hoisted = Promoted();
        // ANTI-VACUITY: tests were left out of this corpus (b, the arithmetic
        // of wt1 before its call, the type test of e, the cut of wd's second
        // clause among them).
        Assert.True(IlPredicateCompiler.HoistedWakeTests > hoistedBefore, "no wake test was left out");
        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(hoisted, g));

        IlPredicateCompiler.HoistWakeTests = false;
        int keptBefore = IlPredicateCompiler.HoistedWakeTests;
        var kept = Promoted();
        Assert.Equal(keptBefore, IlPredicateCompiler.HoistedWakeTests);
        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(kept, g));
    }
}
