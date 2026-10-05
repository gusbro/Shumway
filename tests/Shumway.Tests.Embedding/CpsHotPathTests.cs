#if !NETFRAMEWORK
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// A nondeterministic foreign predicate whose iterator counts its disposals:
// a cut that discards its choice point disposes it (the side-stack entry's
// prune hook).
public partial class CpsHotPathTracked
{
    public static int Opened, Disposed;

    [PrologPredicate("hot_tracked/1", NonDeterministic = true)]
    public static IEnumerable<int> Tracked()
    {
        Opened++;
        try
        {
            for (int i = 1; i <= 5; i++) yield return i;
        }
        finally
        {
            Disposed++;
        }
    }

    [PrologPredicate("hot_open/1")]
    public static int Open() => Opened - Disposed;
}

/// <summary>ADR-061: what a hot method does without leaving for its cold
/// method. A binding that has to be trailed, a unification that fails on its
/// first test, a chain of references and a cut that discards a choice point
/// are the common paths of a nondeterministic predicate; each left for the
/// cold method, which then ran the rest of the clause. The observable is the
/// count of entries into cold methods.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CpsHotPathTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;
    private readonly bool _savedCount = IlPredicateCompiler.CountColdEntries;

    public CpsHotPathTests()
    {
        IlPredicateCompiler.CpsMode = true;
        IlPredicateCompiler.CountColdEntries = true;
    }

    public void Dispose()
    {
        IlPredicateCompiler.CountColdEntries = _savedCount;
        IlPredicateCompiler.CpsMode = _savedCpsMode;
    }

    // part/4 binds its third or fourth argument, a variable of its caller,
    // under its own choice point (a trailed binding) and then cuts it.
    // first/2 cuts the choice point its callee left. chain/1 reaches a
    // variable through three references, for a list and for an atom.
    // color/4 as called is indexed on its second argument only and tries its
    // three clauses: two fail on the third argument's atom. app/3 fails on a
    // third argument that is an atom. (A goal inside a condition or a
    // negation runs under the dispatch loop, not in these methods.)
    // over_builtin/1 cuts a builtin's choice point, which has a side-stack
    // entry to pop: over_wam/1, with a frame of the same size, then pushes a
    // choice point where that one was and backtracks into it. scope_cut/1 cuts out of a setup_call_cleanup/3 whose goal
    // left a choice point (the cut runs the cleanup); in_scope/1 cuts
    // inside one. sized/2 binds its second argument, a trailed binding,
    // before a guard that fails with its frame allocated: the hot method
    // pops the frame and the cold method undoes the binding.
    private const string Program = """
        part([], _, [], []).
        part([X|L], Y, [X|L1], L2) :- X =< Y, !, part(L, Y, L1, L2).
        part([X|L], Y, L1, [X|L2]) :- part(L, Y, L1, L2).
        qs([], R, R).
        qs([X|T], R, C) :- part(T, X, S, B), qs(S, R, [X|R1]), qs(B, R1, C).
        data([27,74,17,33,94,18,46,83,65,2,32,53,28,85,99,47,28,82,6,11,55,29,39,81,90,37,10,0,66,51,7,21]).
        sorted(S) :- data(L), qs(L, S, []).
        runs(0) :- !.
        runs(N) :- sorted(_), M is N - 1, runs(M).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        first(X, L) :- mem(X, L), X > 2, !.
        firsts(0) :- !.
        firsts(N) :- first(X, [1, 2, 3, 4]), X == 3, M is N - 1, firsts(M).
        vars(f(_, _, _, _)).
        link(X, X).
        linked(A, D) :- vars(f(A, B, C, D)), link(C, D), link(B, C), link(A, B).
        head([H|_], H).
        is_a(a).
        chain(R) :- linked(A, D), head(D, a), linked(B, E), is_a(E), R = A-B.
        chains(0) :- !.
        chains(N) :- chain([H|_]-B), H == a, B == a, M is N - 1, chains(M).
        color(a, 1, red, N) :- N > 0.
        color(a, 2, green, N) :- N > 0.
        color(a, 3, blue, N) :- N > 0.
        colors(0) :- !.
        colors(N) :- color(a, K, blue, 1), K == 3, M is N - 1, colors(M).
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        prefix(A, B, C, yes) :- app(A, B, C).
        prefix(_, _, _, no).
        shorts(0) :- !.
        shorts(N) :- prefix([a, b, c], [d], [a, b|x], R), R == no, M is N - 1, shorts(M).
        ones(0, []) :- !.
        ones(N, [1|T]) :- M is N - 1, ones(M, T).
        ok(X) :- X > 0, X < 9.
        keep([], []).
        keep([X|L], [X|L1]) :- ok(X), !, keep(L, L1).
        keep([_|L], L1) :- keep(L, L1).
        kept(N, Used) :- ones(N, L), keep(L, S), statistics(trail_stack, [Used|_]), length(S, N).
        over_builtin(Y) :- between(3, 5, Y), Y > 2, !.
        over_retried(Y) :- between(1, 5, Y), Y > 2, !.
        over_wam(Y) :- mem(Y, [1, 2, 3, 4, 5]), mem(Z, [Y]), Z > 2.
        after_builtin(R) :- over_builtin(_), over_wam(R).
        after_retried(R) :- over_retried(_), over_wam(R).
        scope_cut(R) :- setup_call_cleanup(true, (mem(X, [1, 2, 3]), X > 1), Done = yes), !, R = X-Done.
        in_scope(R) :- setup_call_cleanup(true, first(X, [1, 2, 3, 4]), Done = yes), R = X-Done.
        first_tracked(X) :- hot_tracked(X), !.
        later_tracked(X) :- hot_tracked(X), X > 2, !.
        tracked(0) :- !.
        tracked(N) :- first_tracked(A), A == 1, hot_open(O1), O1 == 0,
            later_tracked(B), B == 3, hot_open(O2), O2 == 0, M is N - 1, tracked(M).
        sized(X, small) :- X < 10, !, ok(X), X > 0.
        sized(X, big(Y)) :- Y is X + 1.
        :- dynamic(counter/1).
        :- dynamic(tag/1).
        counter(0).
        tag(c).
        bump :- retract(counter(N)), M is N + 1, assertz(counter(M)).
        retag :- retract(tag(T)), assertz(tag(T)).
        reads(X) :- counter(X), X >= 0.
        picks(X) :- mem(X, [a, b, c]), tag(X).
        mixed(0) :- !.
        mixed(N) :- bump, retag, reads(_), picks(X), X == c, M is N - 1, mixed(M).
        bind_all([]).
        bind_all([x|T]) :- bind_all(T).
        under_trail(N, K) :- length(Vs, N), mem(_, [a, b]), bind_all(Vs), runs(K), !.
        """;

    private static readonly (string Name, int Arity)[] Hot =
    {
        ("part", 4), ("qs", 3), ("mem", 2), ("first", 2), ("firsts", 1), ("runs", 1),
        ("chain", 1), ("color", 4), ("head", 2), ("is_a", 1), ("app", 3), ("prefix", 4),
        ("over_builtin", 1), ("over_retried", 1), ("over_wam", 1), ("after_builtin", 1), ("after_retried", 1), ("scope_cut", 1), ("in_scope", 1), ("sized", 2),
    };

    private static readonly string[] Goals =
    {
        "sorted(R).",
        "findall(S-B, part([3, 1, 2, 5, 4], 3, S, B), R).",
        "findall(X, mem(X, [a, b, c]), R).",
        "findall(X-Y, (mem(X, [1, 2, 3]), first(Y, [X, 5])), R).",
        "findall(S, (mem(P, [1, 4, 9]), part([5, 2, 8], P, S, _)), R).",
        "findall(K-C, color(_, K, C, 1), R).",
        "findall(K, color(a, K, green, 1), R).",
        "findall(C, color(a, 2, C, 1), R).",
        "findall(K, color(b, K, _, 1), R).",
        "( chain(L) -> R = L ; R = none ).",
        "findall(X, app(X, _, [a, b]), R).",
        "( app([a, b], [c], [a|x]) -> R = yes ; R = no ).",
        "findall(Y, (mem(L, [[a, b|x], [a, b, d]]), prefix([a, b], [d], L, Y)), R).",
        "findall(X, (first(X, [1, 2, 3, 4]) ; X = alt), R).",
        "after_builtin(R).",
        "after_retried(R).",
        "findall(Y-X, (over_builtin(Y), mem(X, [a, b])), R).",
        "findall(X-Y, (mem(X, [a, b]), over_builtin(Y)), R).",
        "sized(20, R).",
        "sized(5, R).",
        "findall(X-R0, (mem(X, [3, 30, 0]), sized(X, R0)), R).",
        "scope_cut(R).",
        "in_scope(R).",
        "findall(A-B, (scope_cut(A), in_scope(B)), R).",
    };

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern("user$" + n, permanent: true).Id, a);

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.UseCoroutining();   // user's predicates carry their module's prefix
        e.IlPromotion.Threshold = threshold;
        e.RegisterPredicates(typeof(CpsHotPathTracked));
        e.ConsultString(Program);
        return e;
    }

    private static string[] Loops(int n) => new[]
    {
        $"runs({n}).", $"firsts({n}).", $"chains({n}).", $"colors({n}).", $"shorts({n}).",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    // Promoted, with its continuation methods, before anything is measured.
    private static PrologEngine Compiled()
    {
        var e = Engine(1);
        for (int round = 0; round < 4; round++)
        {
            foreach (string g in Loops(40))
                Assert.True(e.Query(g).Success, g);
            foreach (string g in Goals) e.Query(g);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: the predicates run their continuation methods.
        foreach (var (n, a) in Hot)
            Assert.True(e.IlPromotion.TryGetCps(Fid(n, a)) is not null, $"{n}/{a} has no continuation methods");
        return e;
    }

    [Fact]
    public void TheCommonPaths_StayInTheHotMethods()
    {
        int trails0 = IlPredicateCompiler.HotTrailSites, cuts0 = IlPredicateCompiler.HotCutSites;
        var e = Compiled();
        // ANTI-VACUITY: the methods trail and cut inline.
        Assert.True(IlPredicateCompiler.HotTrailSites > trails0, "no binding trailed inline");
        Assert.True(IlPredicateCompiler.HotCutSites > cuts0, "no cut made inline");

        long before = IlPredicateCompiler.ColdEntries;
        foreach (string g in Loops(2000))
            Assert.True(e.Query(g).Success, g);
        long entries = IlPredicateCompiler.ColdEntries - before;
        // Before, each of the 10,000 iterations left for a cold method, most
        // of them many times (runs: 110 a sort). What is left is a
        // compaction of the trails every thousand entries or so.
        Assert.InRange(entries, 0, 1000);
    }

    // counter/1 and tag/1 change at every iteration and stay on their
    // bytecode. reads/1 calls one and continues after it; picks/1 calls the
    // other three times, and it fails twice into the choice point of mem/2.
    // The calls return to the dispatch loop from the hot method, and the
    // loop enters the continuation method after a call and the alternatives
    // method on a failure.
    [Fact]
    public void AroundACalleeOnItsBytecode_TheMethodsAreTheHotOnes()
    {
        var e = Compiled();
        for (int round = 0; round < 4; round++)
        {
            Assert.True(e.Query("mixed(40).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        foreach (string n in new[] { "reads", "picks" })
            Assert.True(e.IlPromotion.TryGetCps(Fid(n, 1)) is not null, $"{n}/1 has no continuation methods");
        long cold = IlPredicateCompiler.ColdEntries;
        long continuations = IlPredicateCompiler.LoopContinuations, alternatives = IlPredicateCompiler.LoopAlternatives;
        Assert.True(e.Query("mixed(2000).").Success);
        // ANTI-VACUITY: the callees ran on their bytecode to the end.
        foreach (string n in new[] { "counter", "tag" })
            Assert.True(e.IlPromotion.TryGetCps(Fid(n, 1)) is null, $"{n}/1 has continuation methods");
        // Before: a cold entry per call, 8,000 and more.
        Assert.InRange(IlPredicateCompiler.ColdEntries - cold, 0, 1000);
        // At every iteration: the return into reads/1 and, tag/1 being the
        // last call of picks/1, the return into mixed/1; and two failures
        // into mem/2.
        Assert.InRange(IlPredicateCompiler.LoopContinuations - continuations, 4_000, 4_500);
        Assert.InRange(IlPredicateCompiler.LoopAlternatives - alternatives, 4_000, 4_500);
    }

    // The cut of first_tracked/1 follows the foreign call in its method; the
    // cut of later_tracked/1 is reached after a retry of the foreign choice
    // point. No iterator is open at the goal after either.
    [Fact]
    public void ACutOverAForeignChoicePoint_DisposesItsIterator()
    {
        var e = Compiled();
        for (int round = 0; round < 4; round++)
        {
            Assert.True(e.Query("tracked(40).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        foreach (string n in new[] { "first_tracked", "later_tracked" })
            Assert.True(e.IlPromotion.TryGetCps(Fid(n, 1)) is not null, $"{n}/1 has no continuation methods");
        int opened = CpsHotPathTracked.Opened, disposed = CpsHotPathTracked.Disposed;
        Assert.True(e.Query("tracked(2000).").Success);
        Assert.Equal(4000, CpsHotPathTracked.Opened - opened);
        Assert.Equal(4000, CpsHotPathTracked.Disposed - disposed);
    }

    // The cold method transfers through the dispatch loop and takes the
    // JIT's tiers: no tail call (the JIT gives a method with one no first
    // tier) and no request to optimize at once. The hot methods have both.
    [Fact]
    public void TheColdMethod_HasNoTailCallAndTakesTheJitsTiers()
    {
        const int AggressiveOptimization = 0x0200;
        var e = Compiled();
        int hotTailCalls = 0;
        foreach (var (n, a) in Hot)
        {
            var cps = e.IlPromotion.TryGetCps(Fid(n, a))!;
            var cold = cps.ColdDelegate.Method;
            Assert.Equal(0, (int)cold.GetMethodImplementationFlags() & AggressiveOptimization);
            Assert.Equal(0, TailPrefixes(cold));
            var entry = cps.EntryDelegate.Method;
            Assert.NotEqual(0, (int)entry.GetMethodImplementationFlags() & AggressiveOptimization);
            hotTailCalls += TailPrefixes(entry);
        }
        // ANTI-VACUITY: the count sees a tail call where there is one.
        Assert.True(hotTailCalls > 0, "no entry method has a tail call");

        // A cold method's stretch ends at its next transfer, in the loop,
        // which enters a hot method again: part/4 leaves for its cold method
        // some 200 times (the trails' compaction), and that method's
        // recursive call is the loop's.
        long cold0 = IlPredicateCompiler.ColdEntries, calls0 = IlPredicateCompiler.LoopCalls;
        Assert.True(e.Query("runs(2000).").Success);
        long coldEntries = IlPredicateCompiler.ColdEntries - cold0;
        Assert.InRange(coldEntries, 50, 1000);
        Assert.InRange(IlPredicateCompiler.LoopCalls - calls0, coldEntries / 2, coldEntries * 4);
    }

    // The emitter gives a call before the method's return the tail. prefix,
    // except in a method that takes none.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ACallBeforeTheReturn_IsATailCallUnlessTheMethodTakesNone(bool noTailCalls, bool tail)
    {
        var emit = IlEmit.NewDynamicMethod("tail_or_not", record: true);
        emit.NoTailCalls = noTailCalls;
        emit.LoadArgument(0);
        emit.Call(typeof(CpsHotPathTests).GetMethod(nameof(IsNull))!);
        emit.Return();
        Assert.Equal(tail, emit.Instructions().Contains("tail."));
        Assert.True(emit.CreateDelegate(initLocals: false)(null!, 0));
    }

    public static bool IsNull(Activation? engine) => engine is null;

    private static readonly Dictionary<short, System.Reflection.Emit.OperandType> Operands =
        typeof(System.Reflection.Emit.OpCodes).GetFields()
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value, o => o.OperandType);

    // The tail. prefixes in a method's IL, decoded instruction by instruction.
    private static int TailPrefixes(System.Reflection.MethodInfo method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        int count = 0;
        for (int i = 0; i < il.Length;)
        {
            short op = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            if (op == System.Reflection.Emit.OpCodes.Tailcall.Value) count++;
            i += Operands[op] switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget
                    or System.Reflection.Emit.OperandType.ShortInlineI
                    or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI8
                    or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
        return count;
    }

    [Fact]
    public void TheAnswers_AreTheInterpreters()
    {
        var plain = Engine(0);
        var e = Compiled();
        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(e, g));
    }

    // 20,000 bindings that every compaction keeps (their variables are older
    // than the choice point under them): the room the cuts get is counted
    // from what a compaction left, not from an empty trail.
    [Fact]
    public void UnderALargeTrail_TheCutsStayInTheHotMethods()
    {
        var e = Compiled();
        long before = IlPredicateCompiler.ColdEntries;
        Assert.True(e.Query("under_trail(20000, 500).").Success);
        // 500 sorts make 40,000 cuts that discard a choice point.
        Assert.InRange(IlPredicateCompiler.ColdEntries - before, 1, 2000);
    }

    // A hot method's cut leaves the trails as they are; the cold method's
    // compacts them when they have grown by 1,024 entries, or by as much
    // again. keep/2 trails a binding and cuts a choice point per
    // element (the call before its cut keeps the choice point real):
    // 200,000 of them in one pass, read before anything else cuts.
    [Fact]
    public void TheTrails_StayBoundedUnderTheHotMethodsCuts()
    {
        var e = Compiled();
        for (int round = 0; round < 4; round++)
        {
            Assert.True(e.Query("kept(300, _).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        Assert.True(e.IlPromotion.TryGetCps(Fid("keep", 2)) is not null);
        long before = IlPredicateCompiler.ColdEntries;
        var r = e.Query("kept(200000, Used).");
        Assert.True(r.Success);
        long used = long.Parse($"{r["Used"]}") / 8;
        Assert.InRange(used, 0, 2048);
        // ANTI-VACUITY: the cuts were the hot method's; a cold method made
        // one in a thousand.
        Assert.InRange(IlPredicateCompiler.ColdEntries - before, 100, 1000);
    }
}
#endif
