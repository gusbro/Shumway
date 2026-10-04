using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-049: compiled code wakes where Tier-0 does, in front of
/// builtins and inlined calls. A wake inside a CP-free guard (ADR-031), or
/// inside a fail-direct chain inlined in one, pushes the choice points they
/// skipped and the interpreter runs the rest of the activation; without
/// continuation methods (regions) every wake point does. With ADR-033's
/// shared copies, the levels outside the copy come from the continuation
/// stack. The answers are the interpreter's, in regions, with the
/// continuation methods (ADR-061) and with the delegates alone.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CompiledWakePointTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;
    private readonly int _savedMaxBytes = IlPredicateCompiler.CpsMaxBytecodeBytes;
    private readonly bool _savedContinuations = IlPredicateCompiler.CpFreeGuardContinuations;

    public CompiledWakePointTests() => IlPredicateCompiler.CpsCompileEveryMethod = true;

    public void Dispose()
    {
        IlPredicateCompiler.CpFreeGuardContinuations = _savedContinuations;
        IlPredicateCompiler.CpsCompileEveryMethod = false;
        IlPredicateCompiler.CpsMaxBytecodeBytes = _savedMaxBytes;
        IlPredicateCompiler.CpsMode = _savedCpsMode;
    }

    // bl/3: a builtin after a binding, outside any guard. pg/2: a builtin in a
    // guard. pl/2: a builtin in a single-clause callee inlined in a guard.
    // pq/2: the same in a two-clause callee, a fail-direct chain. pr/3: a
    // chain whose clauses cut. pr2/2: a wake after the callee's cut. pm/2 and
    // pf/2: a wake at the callee's proceed, its alternatives left (memberchk).
    // pn/2: a chain in a chain. ps/2: a chain that calls itself last. pd/2: a
    // chain whose cut is deep, its level taken before the guard's choice point
    // existed. tok/2: a callee whose last clause calls another last (a cross
    // tail with shared copies). pp/3: two callees calling each other last, the
    // wake at the bottom. top/2: a callee called from a callee. pw/3: a
    // builtin right after a call whose head bound the frozen variable, the
    // argument staging between them. pcol/2 and pnum/2: facts indexed on their
    // first argument, called with an attributed variable there. The second
    // clauses count how often they run: a wake must not send the guard to
    // them while the callee still has alternatives.
    private const string Corpus = """
        bl(X, Z, L) :- X = a, atom_length(Z, L).
        pg(a, W) :- atom(W), !.
        pg(_, no).
        ql(a, W) :- atom(W).
        pl(X, W) :- ql(X, W), !.
        pl(_, 1) :- second.
        pa(a, Y, L) :- atom_length(Y, L), L > 2, !.
        pa(_, _, none).
        q(a, W) :- atom(W).
        q(_, 1).
        pq(X, W) :- q(X, W), !.
        pq(_, 1) :- second.
        r(a, W) :- atom(W), !.
        r(b, W) :- integer(W), !.
        r(_, none).
        pr(X, W, R) :- r(X, W), R = ok, !.
        pr(_, _, fail).
        r2(a, W) :- !, atom(W).
        r2(_, none).
        pr2(X, W) :- r2(X, W), !.
        pr2(_, other).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        pm(X, L) :- mem(X, L), !.
        pm(_, none).
        qf(a, x).
        qf(_, 1).
        pf(X, W) :- qf(X, W), !.
        pf(_, 2) :- second.
        inner(a, W) :- atom(W).
        inner(_, 1).
        outer(X, W) :- inner(X, W), W \== 7.
        outer(_, 2).
        pn(X, W) :- outer(X, W), !.
        pn(_, 3).
        st([], R, R).
        st([X|Xs], A, R) :- atom(X), !, st(Xs, [X|A], R).
        st([_|Xs], A, R) :- st(Xs, A, R).
        ps(L, R) :- st(L, [], R), !.
        ps(_, none).
        sa(X) :- atom(X).
        rd(a, W) :- sa(W), !.
        rd(_, none).
        pd(X, W) :- rd(X, W), W \== z, !.
        pd(_, other).
        espacio(0' ).
        espacio(C) :- tab_o_salto(C).
        tab_o_salto(9).
        tab_o_salto(10).
        tok(C, T) :- espacio(C), !, T = blanco.
        tok(_, otro).
        par2(a, 0).
        par2(X, N) :- N > 0, M is N - 1, impar2(X, M).
        impar2(X, N) :- N > 0, M is N - 1, par2(X, M).
        pp(X, N, T) :- par2(X, N), !, T = par.
        pp(_, _, impar).
        in2(a, W) :- atom(W).
        in2(_, 1).
        mid(X, W) :- in2(X, W), W \== 7.
        mid(_, 2).
        top(X, W) :- mid(X, W), !.
        top(_, 3).
        bx(a) :- bxn(0).
        bx(b) :- bxn(1).
        bxn(N) :- N < 5.
        pw(X, L, N) :- bx(X), atom_length(L, N).
        col(red).
        col(green).
        col(blue).
        num(1, one).
        num(2, two).
        num(3, three).
        pcol(X, Y) :- col(X), atom_length(X, Y).
        pnum(X, W) :- num(X, W), atom(W).
        second :- nb_getval(seconds, N), N1 is N + 1, nb_setval(seconds, N1).
        v(X, Y) :- ( var(X) -> Y = v ; Y = X ).
        """;

    private static readonly string[] Goals =
    {
        "findall(L, (freeze(X, (Z = hello ; Z = hi)), bl(X, Z, L)), R).",
        "findall(A-W, (freeze(X, W = yes), pg(X, W), v(X, A)), R).",
        "findall(A-W, (freeze(X, member(W, [1, yes])), pg(X, W), v(X, A)), R).",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, member(W, [1, yes])), pl(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, W = 1), pl(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "findall(A-Y-L, (freeze(X, member(Y, [ab, abc, abcd])), pa(X, Y, L), v(X, A)), R).",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, W = 1), pq(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, member(W, [1, yes])), pq(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, member(W, [1, 2])), pq(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "findall(A-W-K, (freeze(X, member(W, [1, z])), pr(X, W, K), v(X, A)), R).",
        "findall(A-W-K, (freeze(X, member(W, [1, 2])), pr(X, W, K), v(X, A)), R).",
        "findall(A-W, (freeze(X, member(W, [1, z])), pr2(X, W), v(X, A)), R).",
        "findall(K, pr(b, 3, K), R).",
        "nb_setval(seconds, 0), findall(W, pq(a, W), L), nb_getval(seconds, S), R = L-S.",
        "findall(A, (freeze(X, X \\== a), pm(X, [a, b, c]), v(X, A)), R).",
        "nb_setval(seconds, 0), findall(A-W, (freeze(X, fail), pf(X, W), v(X, A)), L), nb_getval(seconds, S), R = L-S.",
        "findall(A-W, (freeze(X, member(W, [1, yes])), pn(X, W), v(X, A)), R).",
        "findall(A-W, (freeze(X, W = 7), pn(X, W), v(X, A)), R).",
        "findall(L-T, (freeze(L, member(L, [[1, c], [a, b]])), ps(L, T)), R).",
        "findall(T, ps([1, a, b], T), R).",
        "findall(A-W, (freeze(X, member(W, [1, z])), pd(X, W), v(X, A)), R).",
        "findall(A-W, (freeze(X, member(W, [1, y])), pd(X, W), v(X, A)), R).",
        "findall(C-T, (freeze(C, C \\== 32), tok(C, T)), R).",
        "findall(T, tok(10, T), R).",
        "findall(T, (freeze(X, true), pp(X, 4, T)), R).",
        "findall(T, (freeze(X, fail), pp(X, 4, T)), R).",
        "findall(T, (freeze(X, true), pp(X, 3, T)), R).",
        "findall(A-W, (freeze(X, member(W, [1, yes])), top(X, W), v(X, A)), R).",
        "findall(A-W, (freeze(X, W = 7), top(X, W), v(X, A)), R).",
        "findall(X-N, (freeze(X, (L = hello ; L = hi)), pw(X, L, N)), R).",
        "findall(X, (freeze(X, true), bx(X)), R).",
        "findall(X-Y, (freeze(X, X \\== green), pcol(X, Y)), R).",
        "findall(X-W, (freeze(X, true), pnum(X, W)), R).",
    };

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Corpus);
        return e;
    }

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    // With a library loaded, user's predicates carry their module's prefix.
    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern("user$" + n, permanent: true).Id, a);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void WakesInFrontOfBuiltinsAndInGuards_AsTheInterpreterDoes(
        bool continuationMethods, bool delegatesOnly, bool sharedCopies)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        if (delegatesOnly) IlPredicateCompiler.CpsMaxBytecodeBytes = 1;
        IlPredicateCompiler.CpFreeGuardContinuations = sharedCopies;
        var plain = Engine(0);
        int cold0 = IlPredicateCompiler.CpsCompiledColdMethods;
        int levels0 = IlPredicateCompiler.WakeLevelRoutines;
        int handovers0 = IlPredicateCompiler.HandoverPoints;
        int windows0 = IlPredicateCompiler.WindowPoints;
        var tiered = Engine(1);
        for (int round = 0; round < 3; round++)
        {
            foreach (string g in Goals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: the predicates run compiled, with continuation methods
        // when asked for and unless only the delegates were.
        bool withCps = continuationMethods && !delegatesOnly;
        foreach (var (n, a) in new[] { ("bl", 3), ("pg", 2), ("pl", 2), ("pq", 2), ("pr", 3), ("pr2", 2),
                     ("pm", 2), ("pf", 2), ("pn", 2), ("ps", 2), ("pd", 2), ("tok", 2), ("pp", 3), ("top", 2) })
        {
            Assert.True(tiered.IlPromotion.IsPromoted(Fid(n, a)), $"{n}/{a} not promoted");
            Assert.Equal(withCps, tiered.IlPromotion.TryGetCps(Fid(n, a)) is not null);
        }
        foreach (var (n, a) in new[] { ("pw", 3), ("bx", 1), ("pcol", 2), ("pnum", 2) })
            Assert.True(tiered.IlPromotion.IsPromoted(Fid(n, a)), $"{n}/{a} not promoted");
        if (withCps)
            Assert.True(IlPredicateCompiler.CpsCompiledColdMethods > cold0, "no cold method compiled");
        Assert.True(IlPredicateCompiler.HandoverPoints > handovers0, "no wake point hands over");
        // ...and in regions, wakes that resume at a call's continuation.
        if (!continuationMethods)
            Assert.True(IlPredicateCompiler.WindowPoints > windows0, "no wake point resumes at a continuation");
        // ...and with shared copies, wakes inside them.
        if (sharedCopies)
            Assert.True(IlPredicateCompiler.WakeLevelRoutines > levels0, "no wake inside a shared copy compiled");

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
    }
}
