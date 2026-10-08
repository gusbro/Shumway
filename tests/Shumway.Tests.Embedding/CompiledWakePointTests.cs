using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
/// continuation methods (ADR-061) and with the delegates alone, compiled at
/// run time or carried by a bundle.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CompiledWakePointTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;
    private readonly int _savedMaxBytes = IlPredicateCompiler.CpsMaxBytecodeBytes;
    private readonly bool _savedContinuations = IlPredicateCompiler.CpFreeGuardContinuations;
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    // A bundle's code waits for its predicate's first call (the suite binds at load).
    public CompiledWakePointTests() => IlPromotionStore.DefaultPersistedThreshold = 1;

    public void Dispose()
    {
        IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;
        IlPredicateCompiler.CpFreeGuardContinuations = _savedContinuations;
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

    // d/2: a dynamic predicate whose rule calls a builtin after a binding.
    // e/2: the same in the first of two clauses. f/2: in a branch of an
    // if-then-else. g/2: in a guard, which no snapshot compiles (it stays on
    // Tier-0). A dynamic predicate's compiled form is a snapshot of its
    // clauses (ADR-023), and its functor's bytecode is the live clause chain:
    // a wake there has no bytecode to hand the activation to.
    private const string DynamicCorpus = """
        :- dynamic d/2, e/2, f/2, g/2.
        d(X, L) :- X = a, atom_length(hello, L).
        e(a, L) :- atom_length(hi, L).
        e(_, 0).
        f(X, Y) :- ( X = a -> atom_length(abc, Y) ; Y = 0 ), Y >= 0.
        g(a, W) :- atom(W), !.
        g(_, no).
        v(X, Y) :- ( var(X) -> Y = v ; Y = X ).
        """;

    private static readonly string[] DynamicGoals =
    {
        "findall(A-L, (freeze(X, true), d(X, L), v(X, A)), R).",
        "findall(A-L, (freeze(X, (L = 5 ; L = 6)), d(X, L), v(X, A)), R).",
        "findall(A-L, (freeze(X, true), e(X, L), v(X, A)), R).",
        "findall(A-L, (freeze(X, L = 0), e(X, L), v(X, A)), R).",
        "findall(A-L, (freeze(X, true), f(X, L), v(X, A)), R).",
        "findall(A-W, (freeze(X, W = yes), g(X, W), v(X, A)), R).",
        "findall(A-W, (freeze(X, member(W, [1, yes])), g(X, W), v(X, A)), R).",
    };

    private static int DynamicFid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ADynamicPredicatesSnapshotWakes_AsTheInterpreterDoes(bool continuationMethods, bool sharedCopies)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        IlPredicateCompiler.CpFreeGuardContinuations = sharedCopies;
        PrologEngine Dynamic(int threshold)
        {
            var e = new PrologEngine();
            e.UseCoroutining();
            e.IlPromotion.Threshold = threshold;
            e.ConsultString(DynamicCorpus);
            return e;
        }
        var plain = Dynamic(0);
        var tiered = Dynamic(1);
        for (int round = 0; round < 3; round++)
        {
            foreach (string g in DynamicGoals) tiered.Query(g);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: the snapshot whose wake point is outside any guard runs compiled.
        Assert.True(tiered.IlPromotion.IsPromoted(DynamicFid("d", 2)), "d/2 not promoted");

        foreach (string g in DynamicGoals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
    }

    private static readonly PredicateRef[] EntryPoints =
    {
        new("bl", 3), new("pg", 2), new("pl", 2), new("pa", 3), new("pq", 2), new("pr", 3),
        new("pr2", 2), new("pm", 2), new("pf", 2), new("pn", 2), new("ps", 2), new("pd", 2),
        new("tok", 2), new("pp", 3), new("top", 2), new("pw", 3), new("bx", 1), new("pcol", 2),
        new("pnum", 2), new("v", 2),
    };

    // As shumway-link builds an --exe or a --dll.
    private static byte[] BundleBytes(bool stripWam, bool bakePrelude = false) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Corpus, "m", ShmoBuildMode.Release) },
            EntryPoints = EntryPoints,
            StripSource = true,
            BakePrelude = bakePrelude,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

    // A bundle's code wakes where Tier-0 does, with its continuation methods
    // when linked with them; under --strip-wam the predicates a wake hands
    // the activation to keep their WAM.
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ABundleWakesAsTheInterpreterDoes(bool continuationMethods, bool stripWam)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        var plain = Engine(0);
        int handovers0 = IlPredicateCompiler.HandoverPoints;
        byte[] bytes = BundleBytes(stripWam);
        Assert.True(IlPredicateCompiler.HandoverPoints > handovers0, "no wake point hands over");
        // The bundle carries its own code, whatever the loading process's mode.
        IlPredicateCompiler.CpsMode = false;
        var bundled = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        bundled.UseCoroutining();
        bundled.IlPromotion.PersistedCallsPerByte = 0;
        var compiled = new[] { ("bl", 3), ("pg", 2), ("pl", 2), ("pq", 2), ("pr", 3), ("pr2", 2),
                     ("pm", 2), ("pf", 2), ("pn", 2), ("ps", 2), ("pd", 2), ("tok", 2), ("pp", 3), ("top", 2) }
            .Select(p => FunctorTable.Intern(AtomTable.Intern(p.Item1, permanent: true).Id, p.Item2)).ToArray();
        // At load nothing is compiled: a predicate with bytecode runs on it
        // until it is hot. Under --strip-wam a predicate a wake hands the
        // activation to keeps its bytecode too; one without is bound.
        foreach (int fid in compiled)
        {
            Assert.NotEqual(bundled.IlPromotion.HasOffer(fid), bundled.IlPromotion.IsPromoted(fid));
            if (!stripWam) Assert.True(bundled.IlPromotion.HasOffer(fid), $"functor {fid} bound at load");
            Assert.Null(bundled.IlPromotion.TryGetCps(fid));
        }
        for (int round = 0; round < 3; round++)
        {
            foreach (string g in Goals) bundled.Query(g);
            Assert.True(bundled.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: the predicates run the bundle's code, which wakes, with
        // continuation methods when linked with them.
        foreach (int fid in compiled)
        {
            Assert.True(bundled.IlPromotion.IsPromoted(fid), $"functor {fid} not promoted");
            Assert.False(bundled.IlPromotion.IsBound(fid), $"functor {fid} bound without wake points");
            Assert.Equal(continuationMethods, bundled.IlPromotion.TryGetCps(fid) is not null);
        }

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(bundled, g));
    }

    // The same from an executable's bundle, which bakes the prelude and the
    // snapshots of the dynamic predicates it ships with clauses.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ABundlesDynamicPredicateWakes_AsTheInterpreterDoes(bool continuationMethods, bool stripWam)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        PrologEngine plain = new();
        plain.UseCoroutining();
        plain.ConsultString(DynamicCorpus);
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(DynamicCorpus, "m", ShmoBuildMode.Release) },
            EntryPoints = new PredicateRef[] { new("d", 2), new("e", 2), new("f", 2), new("g", 2), new("v", 2) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;
        IlPredicateCompiler.CpsMode = false;
        var bundled = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        bundled.UseCoroutining();
        bundled.IlPromotion.PersistedCallsPerByte = 0;
        for (int round = 0; round < 3; round++)
        {
            foreach (string g in DynamicGoals) bundled.Query(g);
            Assert.True(bundled.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: d/2 runs the snapshot the bundle carries compiled.
        Assert.True(bundled.IlPromotion.IsPromoted(DynamicFid("d", 2)), "d/2 not promoted");

        foreach (string g in DynamicGoals)
            Assert.Equal(Answer(plain, g), Answer(bundled, g));
    }

    // A wake hands the activation to bytecode by functor: the compiled code of
    // a bundle's module is the code of the bytecode that module ships. The
    // baked prelude's helpers ($prelude$$disj_N) are numbered by the compile
    // that made them.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABakedPreludesCompiledCodeIsItsBytecodes(bool continuationMethods)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        var bundle = BundleReader.FromBytes(BundleBytes(stripWam: false, bakePrelude: true));
        static (string, int) Indicator(int fid)
        {
            var (atom, arity) = FunctorTable.Lookup(fid);
            return (AtomTable.GetById(atom)!.Name, arity);
        }
        int helpers = 0;
        foreach (var entry in bundle.Entries)
        {
            if (entry.CompiledIlEntries is null) continue;
            var shipped = CompiledModuleCodec.Decode(entry.CompiledBytecode!).Predicates
                .Where(p => p.Bytecode.Length > 0).Select(p => Indicator(p.FunctorId)).ToHashSet();
            var compiled = IlPersistedEntryCodec.Decode(entry.CompiledIlEntries);
            helpers += compiled.Count(e => e.Name.StartsWith("$prelude$$", StringComparison.Ordinal));
            Assert.Empty(compiled.Where(e => !shipped.Contains((e.Name, e.Arity)))
                .Select(e => $"{entry.ModuleName}: {e.Name}/{e.Arity}"));
        }
        Assert.True(helpers > 50, $"only {helpers} prelude helpers compiled");
    }

    // Persisted IL runs in an assembly of its own, without the access checks
    // waived (a collectible one has them waived): every member it references
    // outside it is public.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABundleReferencesOnlyPublicMembers(bool continuationMethods)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        var hidden = new List<string>();
        int references = 0;
        foreach (var entry in BundleReader.FromBytes(BundleBytes(stripWam: false, bakePrelude: true)).Entries)
            if (entry.CompiledIl is { Length: > 0 } il) references += HiddenReferences(il, hidden);
        Assert.True(references > 0, "no member referenced");
        Assert.Empty(hidden.Distinct().OrderBy(s => s));
    }

    private static int HiddenReferences(byte[] il, List<string> hidden)
    {
        var module = Assembly.Load(il).ManifestModule;
        using var pe = new PEReader(new MemoryStream(il));
        var md = pe.GetMetadataReader();
        static bool Visible(Type? t) => t is null || t.IsVisible;
        foreach (var h in md.TypeReferences)
            if (module.ResolveType(MetadataTokens.GetToken(h)) is { IsVisible: false } t)
                hidden.Add(t.FullName!);
        foreach (var h in md.MemberReferences)
        {
            var m = module.ResolveMember(MetadataTokens.GetToken(h))!;
            bool open = m switch
            {
                MethodBase mb => mb.IsPublic,
                FieldInfo f => f.IsPublic,
                _ => true,
            } && Visible(m.DeclaringType);
            if (!open) hidden.Add($"{m.DeclaringType?.FullName}.{m.Name}");
        }
        for (int row = 1; row <= md.GetTableRowCount(TableIndex.MethodSpec); row++)
            if (module.ResolveMethod(MetadataTokens.GetToken(MetadataTokens.MethodSpecificationHandle(row))) is { } ms
                && !(ms.IsPublic && Visible(ms.DeclaringType)))
                hidden.Add($"{ms.DeclaringType?.FullName}.{ms.Name}");
        return md.MemberReferences.Count;
    }
}
