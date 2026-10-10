using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-061 stage 5 on the wasm tier: under CPS a module is made of
/// continuation functions, each holding one entry point (a member's entry or
/// the return of a predicate call, with the alternatives after it) or whole
/// members up to the partition budget; a known transfer is
/// a tail call carrying the machine in its arguments, and a resume row names
/// the function. The answers are Tier-0's, and no transfer stacks a
/// frame.</summary>
public sealed class WasmContinuationFunctionsTests(ITestOutputHelper o) : IDisposable
{
    private readonly bool _savedCps = WasmPredicateCompiler.CpsMode;
    private readonly WasmCpsGrain _savedGrain = WasmPredicateCompiler.CpsGrain;

    private readonly bool _cpsOn = WasmPredicateCompiler.CpsMode = true;

    public void Dispose()
    {
        WasmPredicateCompiler.CpsMode = _savedCps;
        WasmPredicateCompiler.CpsGrain = _savedGrain;
        WasmPredicateCompiler.CountTransfers = false;
    }

    private const string Corpus = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        classify(X, neg) :- X < 0, !.
        classify(0, zero) :- !.
        classify(_, pos).
        tak(X, Y, Z, A) :- X =< Y, !, Z = A.
        tak(X, Y, Z, A) :-
            X1 is X - 1, tak(X1, Y, Z, A1),
            Y1 is Y - 1, tak(Y1, Z, X, A2),
            Z1 is Z - 1, tak(Z1, X, Y, A3),
            tak(A1, A2, A3, A).
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        perm([], []).
        perm(L, [H|T]) :- sel(H, L, R), perm(R, T).
        safe([]).
        safe([Q|Qs]) :- noatt(Q, Qs, 1), safe(Qs).
        noatt(_, [], _).
        noatt(Q, [Q1|Qs], D) :- Q =\= Q1 + D, Q =\= Q1 - D, D1 is D + 1, noatt(Q, Qs, D1).
        queens(N, Qs) :- numl(1, N, L), perm(L, Qs), safe(Qs).
        numl(N, N, [N]) :- !.
        numl(I, N, [I|T]) :- I1 is I + 1, numl(I1, N, T).
        step(N, M) :- M is N - 1.
        count(0) :- !.
        count(N) :- step(N, M), count(M).
        bt(L, N) :- mem(X, L), X >= N, !.
        mklist(0, []) :- !.
        mklist(N, [N|T]) :- M is N - 1, mklist(M, T).
        """;

    private static readonly string[] Goals =
    {
        "nrev([1,2,3,4,5,6,7,8], R).",
        "findall(X, mem(X, [a,b,c,d]), L).",
        "findall(C, (mem(X, [-3,0,7]), classify(X, C)), L).",
        "tak(18, 12, 6, A).",
        "findall(Q, queens(6, Q), L), length(L, N).",
        "mklist(2000, L), len(L, N).",
    };

    /// <summary>Every promotion recompiles the promoted set as one module,
    /// the browser's batch grain: calls between members are transfers
    /// between functions of the module.</summary>
    private static PrologEngine GroupEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (pred, linkedBase) =>
            {
                var candidate = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                members.Add(candidate);
                try
                {
                    TieredEngine.Install(world, members, env);
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    members.Remove(candidate);
                    if (members.Count > 0) TieredEngine.Install(world, members, env);
                    return null;
                }
            },
        };
        return engine;
    }

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "false";
        var parts = new List<string>();
        foreach (var (name, value) in r.Bindings) parts.Add($"{name}={value}");
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    [Theory]
    [InlineData(WasmCpsGrain.EntryPoint)]
    [InlineData(WasmCpsGrain.Budget)]
    public void AGroupAnswersAsTier0(WasmCpsGrain grain)
    {
        WasmPredicateCompiler.CpsGrain = grain;
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var e = GroupEngine();
        foreach (string g in Goals)
        {
            string expected = Answer(plain, g);
            for (int round = 0; round < 2; round++)
                Assert.Equal(expected, Answer(e, g));
        }
        // ANTI-VACUITY: the predicates ran on the tier.
        Assert.True(e.IlPromotion.PromotedFunctorIds().Count() >= 10,
            $"promoted: {e.IlPromotion.PromotedFunctorIds().Count()}");
    }

    [Theory]
    [InlineData(WasmCpsGrain.EntryPoint)]
    [InlineData(WasmCpsGrain.Budget)]
    public void AModulePerPredicateAnswersAsTier0(WasmCpsGrain grain)
    {
        WasmPredicateCompiler.CpsGrain = grain;
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var (e, members) = TieredEngine.Build(Corpus);
        foreach (string g in Goals)
        {
            string expected = Answer(plain, g);
            for (int round = 0; round < 2; round++)
                Assert.Equal(expected, Answer(e, g));
        }
        Assert.True(members.Count >= 10, $"promoted: {members.Count}");
    }

    // Millions of transfers between functions of one module: a loop whose
    // every iteration is a call and its return, a deep non-tail recursion,
    // and a search that backtracks into one choice point 300,000 times (the
    // lists stay inside the desktop image). A transfer that stacked a frame
    // would overflow the .NET stack long before the end.
    [Theory]
    [InlineData(WasmCpsGrain.EntryPoint)]
    [InlineData(WasmCpsGrain.Budget)]
    public void ManyTransfersKeepTheStackFlat(WasmCpsGrain grain)
    {
        WasmPredicateCompiler.CpsGrain = grain;
        var e = GroupEngine();
        Assert.True(e.Query("count(10), mklist(10, L), len(L, _), numl(1, 10, K), bt(K, 10).").Success);
        // ANTI-VACUITY: count, step, mklist, len, numl, bt and mem are compiled.
        Assert.True(e.IlPromotion.PromotedFunctorIds().Count() >= 7,
            $"promoted: {e.IlPromotion.PromotedFunctorIds().Count()}");
        Assert.True(e.Query("count(2000000).").Success);
        Assert.True(e.Query("mklist(150000, L), len(L, N), N == 150000.").Success);
        Assert.True(e.Query("numl(1, 300000, L), bt(L, 300000).").Success);
    }

    // The same code runs in both forms, only the transfers differ, so
    // time/1's heap cells are equal, not merely close: the tally's base rides
    // in the mailbox across transfers and is banked again at each backtrack.
    [Theory]
    [InlineData("findall(Q, queens(6, Q), L), length(L, _)", WasmCpsGrain.EntryPoint)]
    [InlineData("nrev([1,2,3,4,5,6,7,8,9,10,11,12], _)", WasmCpsGrain.EntryPoint)]
    [InlineData("mklist(3000, L), len(L, _)", WasmCpsGrain.EntryPoint)]
    [InlineData("findall(Q, queens(6, Q), L), length(L, _)", WasmCpsGrain.Budget)]
    [InlineData("nrev([1,2,3,4,5,6,7,8,9,10,11,12], _)", WasmCpsGrain.Budget)]
    public void HeapCellsAreThoseOfTheOtherForm(string goal, WasmCpsGrain grain)
    {
        WasmPredicateCompiler.CpsGrain = grain;
        long Cells(bool cps)
        {
            WasmPredicateCompiler.CpsMode = cps;
            var e = GroupEngine();
            Assert.True(e.Query(goal + ".").Success);   // promote
            var w = new StringWriter();
            e.Out = w;
            Assert.True(e.Query($"time(({goal})).").Success);
            string text = w.ToString();
            int at = text.IndexOf(" heap cells", StringComparison.Ordinal);
            Assert.True(at > 0, text);
            int start = text.LastIndexOf(' ', at - 1) + 1;
            return long.Parse(text[start..at].Replace(",", ""),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        long budget = Cells(cps: false), cps = Cells(cps: true);
        Assert.True(budget > 100, $"the goal claimed too little: {budget}");
        Assert.Equal(budget, cps);
    }

    // How each form moves control, counted rather than timed. In a function
    // that holds the whole loop a backtrack and a call return without leaving
    // it; one function per entry point leaves at the backtrack into member/2
    // and at its return, two transfers, its alternatives being in its entry
    // function. A forward jump inside a function skips the dispatch loop, so
    // the budget grain dispatches less than the partitions of the other form
    // that run the same code.
    [DiagFact]
    public void TheFormsMoveControlAsTheyAreBuilt()
    {
        const int n = 2000;
        var rows = new Dictionary<string, (long Dispatches, long Transfers)>();
        void Run(string form, bool cps, WasmCpsGrain grain, string goal)
        {
            WasmPredicateCompiler.CpsMode = cps;
            WasmPredicateCompiler.CpsGrain = grain;
            WasmPredicateCompiler.CountTransfers = true;
            var e = GroupEngine();
            Assert.True(e.Query(goal + ".").Success);    // promote
            Assert.True(e.Query(goal + ".").Success);    // the group, complete
            WasmTierDelegate.ResetDiag();
            Assert.True(e.Query(goal + ".").Success);
            rows[form + " " + goal] = (WasmTierDelegate.DiagDispatches, WasmTierDelegate.DiagTransfers);
            o.WriteLine($"{form,-10} {goal,-40} dispatches {WasmTierDelegate.DiagDispatches,8}"
                + $"  transfers {WasmTierDelegate.DiagTransfers,8}");
        }
        string back = $"numl(1, {n}, L), bt(L, {n})", loop = $"count({n})";
        foreach (string goal in new[] { back, loop, "tak(18, 12, 6, _)" })
        {
            Run("partitions", false, WasmCpsGrain.Budget, goal);
            Run("cps-entry", true, WasmCpsGrain.EntryPoint, goal);
            Run("cps-budget", true, WasmCpsGrain.Budget, goal);
        }
        // Each backtrack into member/2 is a transfer there and one back.
        long entryBack = rows["cps-entry " + back].Transfers;
        Assert.InRange(entryBack, 2L * (n - 1), 2L * (n - 1) + 20);
        // The whole corpus is one function at the budget grain.
        Assert.True(rows["cps-budget " + back].Transfers < 20,
            $"budget grain, backtracking: {rows["cps-budget " + back].Transfers} transfers");
        Assert.True(rows["cps-budget " + loop].Transfers < 20,
            $"budget grain, call and return: {rows["cps-budget " + loop].Transfers} transfers");
        // ANTI-VACUITY for the forward branches: fewer dispatches than the
        // partitions of the other form, over the same code.
        Assert.True(rows["cps-budget " + back].Dispatches < rows["partitions " + back].Dispatches,
            $"dispatches: {rows["cps-budget " + back].Dispatches} against {rows["partitions " + back].Dispatches}");
    }

    // The cut is at the entry points, not at a size.
    [Fact]
    public void EachEntryPointStartsAFunction()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var env = new EngineWasmCompileEnv();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            if (ar != 4 || !(Shumway.Core.AtomTable.GetById(aid)?.Name ?? "").EndsWith("tak"))
                continue;
            var m = new[] { new WasmGroupMember(pred, addr, null) };
            int budget = FunctionsOf(WasmPredicateCompiler.CompileGroup(m, env, cps: false).Module) - 8;
            byte[] module = WasmPredicateCompiler.CompileGroup(m, env, cps: true,
                grain: WasmCpsGrain.EntryPoint).Module;
            int cps = FunctionsOf(module) - 9;
            // tak/4 alone fits the budget; under CPS its entry and the returns
            // of its three non-tail calls each start one.
            Assert.Equal(1, budget);
            Assert.True(cps >= 4, $"tak/4 under CPS: {cps} functions");
            AssertTransfersAreTailCalls(module, cps);
            return;
        }
        Assert.Fail("tak/4 not found");
    }

    // Every transfer between the module's functions (the partitions, the
    // resolver after them, the router last) is a return_call, and every one
    // through the table a return_call_indirect. A call
    // followed by a return would stack a frame per transfer in the browser;
    // on the desktop the JIT happens to turn that pair into a tail call, so
    // the run above cannot see it and this has to.
    private static void AssertTransfersAreTailCalls(byte[] bytes, int partitions,
                                                    bool mustTransfer = true)
    {
        var module = WebAssembly.Module.ReadFromBinary(new MemoryStream(bytes));
        uint imported = (uint)module.Imports.Count(i => i is WebAssembly.Import.Function);
        uint router = imported + (uint)module.Functions.Count - 1;
        int calls = 0, tails = 0, indirect = 0, tailIndirect = 0;
        foreach (var body in module.Codes)
            foreach (var ins in body.Code)
            {
                if (ins is WebAssembly.Instructions.Call c && IsTransfer(c.Index)) calls++;
                if (ins is WebAssembly.Instructions.ReturnCall rc && IsTransfer(rc.Index)) tails++;
                if (ins is WebAssembly.Instructions.CallIndirect) indirect++;
                if (ins is WebAssembly.Instructions.ReturnCallIndirect) tailIndirect++;
            }
        Assert.Equal(0, calls);
        Assert.Equal(0, indirect);
        if (!mustTransfer) return;
        Assert.True(tails > partitions, $"{tails} tail calls between {partitions} functions");
        // Through the table too: a resume row names the function.
        Assert.True(tailIndirect > 0, "no transfer through the table");

        bool IsTransfer(uint index) =>
            (index >= imported + 1 && index <= imported + (uint)partitions + 1) || index == router;
    }

    // At the budget grain a function holds whole members, as a partition of
    // the other form does: the whole corpus as one group is cut the same way
    // in both forms, and its transfers are tail calls all the same.
    [Fact]
    public void TheBudgetGrainCutsAsThePartitionsDo()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var m = new WasmGroupMember(pred, addr, null);
            try { WasmPredicateCompiler.CompileGroup(new[] { m }, env, cps: false); }
            catch (WasmCompileException) { continue; }
            members.Add(m);
        }
        Assert.True(members.Count >= 20, $"only {members.Count} compilable");
        int partitions = FunctionsOf(WasmPredicateCompiler.CompileGroup(members, env, cps: false).Module) - 8;
        byte[] module = WasmPredicateCompiler.CompileGroup(members, env, cps: true,
            grain: WasmCpsGrain.Budget).Module;
        int functions = FunctionsOf(module) - 9;
        int perEntry = FunctionsOf(WasmPredicateCompiler.CompileGroup(members, env, cps: true,
            grain: WasmCpsGrain.EntryPoint).Module) - 9;
        Assert.Equal(partitions, functions);
        Assert.True(perEntry > functions, $"{perEntry} entry functions, {functions} budget ones");
        AssertTransfersAreTailCalls(module, functions, mustTransfer: false);
    }

    private static int FunctionsOf(byte[] module)
    {
        int at = 8;
        while (at < module.Length)
        {
            int id = module[at++];
            long size = ReadLeb(module, ref at);
            if (id == 3) { int p = at; return (int)ReadLeb(module, ref p); }
            at += (int)size;
        }
        throw new Xunit.Sdk.XunitException("no function section");
    }

    private static long ReadLeb(byte[] b, ref int at)
    {
        long v = 0; int shift = 0;
        while (true)
        {
            byte x = b[at++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
        }
    }
}
