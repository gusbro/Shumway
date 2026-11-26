using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An inline call/1 in a clause with no frame builds one of its own
/// to keep the clause's continuation while the goal runs. In an indexed
/// predicate the clauses are laid out one after another, and the scan for the
/// clause's frame stops at the end of the clause before it. The call takes
/// the framed path once the meta-call cache knows the goal, which the install
/// of a baked library publishes.</summary>
public sealed class InlineMetaCallFrameTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-metaframe-" + Guid.NewGuid().ToString("N"));

    public InlineMetaCallFrameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WasmPredicateCompiler.DebugLoopGuard = false;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // The library: d/1 is indexed, its first clause has a frame (it calls a
    // dynamic predicate), the second has none.
    private const string Dispatch = """
        :- module(dispatch).
        :- dynamic(hook/1).
        d(E) :- hook(E), fail.
        d(goal(G)) :- call(G).
        """;

    // The shape of Logtalk's '$lgt_debug'/2.
    private const string LogtalkDispatch = """
        :- module(dispatch).
        :- dynamic(trace_event/3).
        :- dynamic(active_debug_handler_/2).
        dbg(Event, ExCtx) :- trace_event(Event, ExCtx, _), fail.
        dbg(Event, ExCtx) :- active_debug_handler_(Provider, _), !, debug_handler(Provider, Event, ExCtx, _).
        dbg(top_goal(_, G), _) :- call(G).
        dbg(goal(_, G), _) :- call(G).
        dbg(fact(_, _, _, _, _), _).
        dbg(rule(_, _, _, _, _), _).
        debug_handler(_, goal(_, G), _, _) :- call(G).
        """;

    // A library whose module is baked into its archive, loaded by use_module
    // into an engine whose tier installs the module from it. The loop guard
    // turns a module that spins into an error.
    private PrologEngine EngineWithBakedLibrary(string source)
    {
        WasmPredicateCompiler.DebugLoopGuard = true;
        var obj = ShmoCompiler.CompileSource(source, "dispatch");
        byte[] archive = Librarian.CreateArchive(
            new[] { new BundleArchiveMember("dispatch.shmo", ShmoWriter.ToBytes(obj)) },
            b => WasmBundleTier.Bake(b, stdlib: false));
        WasmPredicateCompiler.DebugLoopGuard = false;
        File.WriteAllBytes(Path.Combine(_dir, "dispatch.shum"), archive);

        var world = new DesktopWasmWorld();
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes, linked) => WasmBundleTier.Install(eng, world, bytes, linked),
        };
        engine.AddLibraryDirectory(_dir);
        engine.ConsultString(":- use_module(library(dispatch)).");
        Assert.True(engine.Query("true.").Success);
        return engine;
    }

    // The answer within a time limit: a module that spins does not look at
    // the engine's interrupt. The thread is left running when it does not
    // end, and the world it runs in is not disposed.
    private static string Within(PrologEngine engine, string goal, int seconds = 20)
    {
        string answer = "still running";
        var thread = new Thread(() =>
        {
            try { answer = engine.Query(goal).Success ? "true" : "false"; }
            catch (Exception ex) { answer = ex.GetType().Name + ": " + ex.Message; }
        }, 64 * 1024 * 1024) { IsBackground = true };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(seconds));
        return answer;
    }

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void ACallInAClauseWithNoFrame_AfterAClauseWithOne_ComesBackToItsCaller()
    {
        var engine = EngineWithBakedLibrary(Dispatch);
        Assert.Contains(Fid("dispatch$d", 1), engine.IlPromotion.Wasm!.BundleFids);
        engine.ConsultString("""
            q(V) :- V = 1.
            p(V) :- d(goal(q(_))), d(goal(q(V))).
            """);
        Assert.Equal("true", Within(engine, "p(V), V == 1."));
    }

    // The shape it hung in: a predicate that calls itself through the
    // dispatch, the first call through it giving the goal to the host and the
    // second taking it from the meta-call cache.
    [Theory]
    [InlineData("ack(1, 0, V), V == 2.")]
    [InlineData("ack(2, 3, V), V == 9.")]
    public void APredicateThatCallsItselfThroughTheDispatch_GetsItsAnswer(string goal)
    {
        var engine = EngineWithBakedLibrary(LogtalkDispatch);
        Assert.Contains(Fid("dispatch$dbg", 2), engine.IlPromotion.Wasm!.BundleFids);
        engine.ConsultString("""
            ack(M, N, V) :- dbg(goal(a, ack1(M, N, V)), c).
            ack1(0, N, V) :- !, V is N + 1.
            ack1(M, 0, V) :- !, M2 is M - 1, dbg(goal(d, ack1(M2, 1, V)), c).
            ack1(M, N, V) :- M2 is M - 1, N2 is N - 1,
                dbg(goal(e, ack1(M, N2, V2)), c), dbg(goal(f, ack1(M2, V2, V)), c).
            """);
        Assert.Equal("true", Within(engine, goal));
    }
}
