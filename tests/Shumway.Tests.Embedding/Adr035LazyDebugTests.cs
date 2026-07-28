using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 D5+ — LAZY full debug (<see cref="DebugOptions.ActivateOnAttach"/>):
/// a debug-COMPILED program whose runtime debug machinery (ports, trail-everything, LCO
/// off) stays off — near-release Tier-0 speed — until a debugger attaches or the host
/// calls <see cref="ChannelDebugSession.ActivateFullDebug"/>. Compile-time debuggability
/// is unchanged; the flag decides when the runtime starts paying for it.
///
/// <para>The stop-behavior tests drive a test-notify session (the real transport's
/// detach-awareness would disarm on the first hit in a debugger-less test process); the
/// EnableDebugging wiring is asserted separately on the engine flags.</para></summary>
public class Adr035LazyDebugTests
{
    private readonly ITestOutputHelper _log;
    public Adr035LazyDebugTests(ITestOutputHelper log) => _log = log;

    //  2: :- dynamic(log/1).
    //  3: run(Out) :-
    //  4:     step(X),
    //  5:     note(X),
    //  6:     Out = X.
    //  7: step(1).
    //  8: note(T) :- assertz(log(T)).
    private const string Program =
        ":- dynamic(log/1).\n" +
        "run(Out) :-\n    step(X),\n    note(X),\n    Out = X.\n" +
        "step(1).\nnote(T) :- assertz(log(T)).\n";

    private static PrologEngine DebugCompiledEngine(string? program = null)
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- set_prolog_flag(compile_mode, debug).\n" + (program ?? Program));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    /// <summary>A lazily-armed session over a test notify: what EnableDebugging builds
    /// with ActivateOnAttach, minus the real transport.</summary>
    private static ChannelDebugSession LazySession(
        PrologEngine engine, Action<int> notify)
    {
        var session = new ChannelDebugSession(engine, notify) { ActivateOnAttach = true };
        engine.DebugFullyArmed = false;
        engine.DebugLcoWhenArmed = false;   // LCO off once armed, like the default
        engine.SetDebugLastCall(true);      // and ON while lazy
        return session;
    }

    [Fact]
    public void EnableDebugging_ActivateOnAttach_OpensUnarmed()
    {
        var engine = DebugCompiledEngine();
        var session = engine.EnableDebugging(new DebugOptions { ActivateOnAttach = true });
        try
        {
            Assert.False(engine.DebugFullyArmed);
            Assert.True(session.ActivateOnAttach);
        }
        finally { session.Dispose(); }

        // And the default remains full-from-startup.
        var engine2 = DebugCompiledEngine();
        var session2 = engine2.EnableDebugging();
        try
        {
            Assert.True(engine2.DebugFullyArmed);
            Assert.False(session2.ActivateOnAttach);
        }
        finally { session2.Dispose(); }
    }

    [Fact]
    public void Lazy_BeforeArming_BreakpointsDoNotStop()
    {
        // The session is open but not armed: a query runs the fast path — no ports, no
        // stops — even with a breakpoint set.
        var engine = DebugCompiledEngine();
        int stops = 0;
        using var session = LazySession(engine, _ => stops++);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        var sols = engine.QueryAll("run(Out).").ToList();

        Assert.Equal(0, stops);
        Assert.Single(sols);
    }

    [Fact]
    public void ActivateFullDebug_BetweenQueries_ArmsTheNextQuery()
    {
        var engine = DebugCompiledEngine();
        int stops = 0;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, _ =>
        {
            stops++;
            session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
        }) { ActivateOnAttach = true };
        engine.DebugFullyArmed = false;
        engine.DebugLcoWhenArmed = false;
        engine.SetDebugLastCall(true);
        Assert.True(engine.AddBreakpoint("<string>", 5) > 0);

        using (session)
        {
            engine.QueryAll("run(_).").ToList();
            Assert.Equal(0, stops);              // lazy: ran free

            session.ActivateFullDebug();

            var sols = engine.QueryAll("run(Out).").ToList();
            Assert.True(stops > 0, "the armed session must stop at the breakpoint");
            Assert.Single(sols);
        }
        _log.WriteLine($"stops after arming: {stops}");
    }

    [Fact]
    public void ActivateFullDebug_MidQuery_ArmsAtTheNextGoalBoundary()
    {
        // The mid-run attach: a long query is in flight when the arm request lands from
        // another thread (as the idle watcher would send it). The activation applies it
        // at its next safe point — later iterations stop at the breakpoint.
        //  2: :- dynamic(log/1).
        //  3: loop :-
        //  4:     between(1, 40000, I),
        //  5:     tick(I),
        //  6:     fail.
        //  7: loop.
        //  8: tick(30000) :- !, assertz(log(seen)).
        //  9: tick(_).
        // (Trigger at I = 30000 ≈ hundreds of ms of debug-mode iterations —
        // ~25× the armer's 30 ms sleep, same guarantee as the original 150000
        // at a fraction of the wall time.)
        var engine = DebugCompiledEngine(
            ":- dynamic(log/1).\n" +
            "loop :-\n    between(1, 40000, I),\n    tick(I),\n    fail.\nloop.\n" +
            "tick(30000) :- !, assertz(log(seen)).\ntick(_).\n");
        int stops = 0;
        ChannelDebugSession? session = null;
        session = new ChannelDebugSession(engine, _ =>
        {
            stops++;
            session!.Channel.WriteCommands(new DebugCommand(DebugCommandKind.Continue));
        }) { ActivateOnAttach = true };
        engine.DebugFullyArmed = false;
        engine.DebugLcoWhenArmed = false;
        engine.SetDebugLastCall(true);
        // Inside the cut clause — reached exactly once, at I = 30000, long after the
        // arm lands.
        Assert.True(engine.AddBreakpoint("<string>", 8) > 0);

        using (session)
        {
            var armer = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(30);   // let the loop get going, unarmed
                session.ActivateFullDebug();
            });
            armer.Start();
            engine.QueryAll("loop.").ToList();
            armer.Join();
        }

        _log.WriteLine($"stops: {stops}");
        Assert.True(stops > 0, "the mid-run arm must reach the running query");
        Assert.Single(engine.QueryAll("log(seen).").ToList());
    }
}
