#if !NETFRAMEWORK
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection.Emit;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Nothing compiles on the engine's thread: a promoted predicate's
/// code (its delegate, and every continuation method) is compiled by the
/// compile worker before the engine switches to it, whether the code is
/// emitted at run time or a bundle carries it. The engine runs the bytecode
/// until then. The one exception is a bundle linked with --strip-wam, whose
/// delegates have nothing to run before them: they compile at their first
/// call, and only their continuation methods wait for the worker.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class EngineThreadJitTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    // A bundle's code waits for its predicate to be hot (the suite binds at load).
    public EngineThreadJitTests() => IlPromotionStore.DefaultPersistedThreshold = 32;

    public void Dispose()
    {
        IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;
        IlPredicateCompiler.CpsMode = _savedCpsMode;
    }

    // loop/1 runs long inside one call: a method that started unoptimized
    // would be replaced on the stack, compiled by the thread running it.
    private const string Program = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        pick(X, L, yes) :- mem(X, L), !.
        pick(_, _, no).
        loop(0) :- !.
        loop(N) :- M is N - 1, loop(M).
        run(R) :- nrev([a, b, c, d, e, f], L), len(L, N), pick(d, L, P), pick(z, L, Q), loop(30000), R = N-P-Q.
        """;

    private const string Goal = "run(R).";
    private const string Expected = "-(-(6, yes), no)";

    // The methods the runtime compiled, with the thread each compiled on.
    private sealed class JitListener : EventListener
    {
        public readonly ConcurrentQueue<(string Name, long Thread)> Compiled = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);   // JIT
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)
                || e.PayloadNames is null || e.Payload is null)
                return;
            int ns = e.PayloadNames.IndexOf("MethodNamespace"), name = e.PayloadNames.IndexOf("MethodName");
            if (ns < 0 || name < 0) return;
            Compiled.Enqueue(($"{e.Payload[ns]}::{e.Payload[name]}", e.OSThreadId));
        }
    }

    // A predicate's compiled code: a dynamic method or a continuation method
    // emitted at run time, or a method of a bundle's type.
    private static bool IsPredicateCode(string method) =>
        method.Contains("::ShumwayIl", StringComparison.Ordinal)
        || method.Contains("ShumwayCompiledPredicates::", StringComparison.Ordinal);

    private static bool IsBundleDelegate(string method) =>
        method.Contains("ShumwayCompiledPredicates::P_", StringComparison.Ordinal);

    // Compiles a method on this thread, to learn the id the events give it.
    private static long ThisThread(JitListener listener)
    {
        string probe = "EngineThreadProbe_" + Guid.NewGuid().ToString("N");
        var dm = new DynamicMethod(probe, typeof(int), Type.EmptyTypes);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_7);
        il.Emit(OpCodes.Ret);
        Assert.Equal(7, ((Func<int>)dm.CreateDelegate(typeof(Func<int>)))());
        for (int i = 0; i < 200; i++)
        {
            foreach (var (name, thread) in listener.Compiled)
                if (name.Contains(probe, StringComparison.Ordinal)) return thread;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("the runtime reported no JIT event for the probe");
    }

    // Lets the events of what has compiled so far arrive.
    private static void Settle(JitListener listener) => ThisThread(listener);

    private static List<string> OnThread(JitListener listener, long thread, Func<string, bool> which)
    {
        Settle(listener);
        return listener.Compiled.Where(c => c.Thread == thread && which(c.Name)).Select(c => c.Name).ToList();
    }

    private static void RunHot(PrologEngine e)
    {
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 40; i++) Assert.Equal(Expected, $"{e.Query(Goal)["R"]}");
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
    }

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodeEmittedAtRunTime_CompilesOnTheWorker(bool continuationMethods)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        using var listener = new JitListener();
        long engineThread = ThisThread(listener);
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 8;
        e.ConsultString(Program);
        RunHot(e);
        // ANTI-VACUITY: the predicates run compiled, with continuation methods
        // when asked for. A consulted predicate carries its module's prefix.
        int nrev = Fid("user$nrev", 2);
        Assert.True(e.IlPromotion.IsPromoted(nrev), "nrev/2 not promoted");
        Assert.Equal(continuationMethods, e.IlPromotion.TryGetCps(nrev) is not null);
        Settle(listener);
        Assert.Contains(listener.Compiled, c => IsPredicateCode(c.Name));

        Assert.Empty(OnThread(listener, engineThread, IsPredicateCode));
    }

    // The bundle's code at a fixed count of calls, whatever its size.
    private static void TakeAtThirtyTwoCalls(PrologEngine e) => e.IlPromotion.PersistedCallsPerByte = 0;

    private static byte[] Bundle(bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "m", ShmoBuildMode.Release) },
            EntryPoints = new PredicateRef[]
            {
                new("app", 3), new("nrev", 2), new("mem", 2), new("len", 2), new("pick", 3), new("loop", 1),
                new("run", 1),
            },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

    // The bundle's code is offered at load, and bound and compiled by the
    // worker once its predicate is hot: the engine runs the bytecode until then.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABundlesCode_CompilesOnTheWorker(bool continuationMethods)
    {
        IlPredicateCompiler.CpsMode = continuationMethods;
        byte[] bytes = Bundle(stripWam: false);
        IlPredicateCompiler.CpsMode = false;
        using var listener = new JitListener();
        long engineThread = ThisThread(listener);
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        TakeAtThirtyTwoCalls(e);
        int nrev = Fid("nrev", 2);
        Assert.True(e.IlPromotion.HasOffer(nrev), "nrev/2 has no offer");
        Assert.False(e.IlPromotion.IsPromoted(nrev), "nrev/2 bound at load");
        // Below the threshold the engine stays on the bytecode.
        Assert.Equal(Expected, $"{e.Query(Goal)["R"]}");
        Assert.False(e.IlPromotion.IsPromoted(nrev), "nrev/2 bound before it was hot");
        RunHot(e);
        // ANTI-VACUITY: the bundle's code took over, and the runtime compiled it.
        Assert.True(e.IlPromotion.IsPromoted(nrev), "nrev/2 not promoted");
        Assert.Equal(continuationMethods, e.IlPromotion.TryGetCps(nrev) is not null);
        Settle(listener);
        Assert.Contains(listener.Compiled, c => IsPredicateCode(c.Name));

        Assert.Empty(OnThread(listener, engineThread, IsPredicateCode));
    }

    // With --strip-wam a delegate is all there is to run: it is bound at load
    // and compiles at its first call. Its continuation methods still wait for
    // the worker.
    [Fact]
    public void AStrippedBundlesContinuationMethods_CompileOnTheWorker()
    {
        IlPredicateCompiler.CpsMode = true;
        byte[] bytes = Bundle(stripWam: true);
        IlPredicateCompiler.CpsMode = false;
        using var listener = new JitListener();
        long engineThread = ThisThread(listener);
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        TakeAtThirtyTwoCalls(e);
        int nrev = Fid("nrev", 2);
        Assert.True(e.IlPromotion.IsPromoted(nrev), "nrev/2 not bound at load");
        Assert.Null(e.IlPromotion.TryGetCps(nrev));
        RunHot(e);
        // ANTI-VACUITY: the continuation methods took over.
        Assert.NotNull(e.IlPromotion.TryGetCps(nrev));

        Assert.Empty(OnThread(listener, engineThread, m => IsPredicateCode(m) && !IsBundleDelegate(m)));
    }
}
#endif
