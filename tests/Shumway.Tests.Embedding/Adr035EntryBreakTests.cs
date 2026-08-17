using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — "stop at the entry point" for a <c>--debug-wait</c> launch.
///
/// <para>Once a debugger has attached, the whole promise of <c>--debug-wait</c> is that the
/// program stops when it does — at its first goal, not somewhere the user has to guess at.
/// The engine side of that is <see cref="ChannelDebugSession.ArmEntryBreak"/> /
/// <see cref="DebugService.ArmEntryBreak"/>: the next debuggable call port fires the entry
/// break instead of running on. In a real session that entry break is <c>BreakHere</c> (a
/// managed <c>Debugger.Break()</c>, the <c>debugger_break/0</c> path — the one stop VS enters
/// break mode for without a step or async break already pending). There is no way to assert a
/// real break from a headless test, so these drive a bare <see cref="DebugService"/> with the
/// entry-break wired to a plain callback and pin the part that is ours to get right: that the
/// arm fires exactly once, at the first goal, and never fires unarmed.</para>
/// </summary>
public sealed class Adr035EntryBreakTests
{
    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        return engine;
    }

    [Fact]
    public void ArmEntryBreak_FiresOnceAtTheFirstGoalOfTheFirstQuery()
    {
        var engine = DebugEngine("""
            go :- first(X), second(X).
            first(1).
            second(_).
            """);

        var service = new DebugService(engine, (_, _) => { });
        int fired = 0;
        string topFrameAtFire = "";
        // Resolve the stack AT FIRE TIME: the activation's P moves on as the query runs, so
        // reading it after QueryAll would see wherever execution ended, not the entry stop.
        service.EntryBreak = act =>
        {
            fired++;
            var frames = engine.CaptureFrames(act);
            topFrameAtFire = frames.Count > 0 ? frames[0].Name : "";
        };
        engine.AttachDebugSession(service);
        service.ArmEntryBreak();

        Assert.True(engine.QueryAll("go.").Any());

        // Exactly once — the first goal the program ran, and no later port re-triggers it
        // (there are several: first/1, second/1).
        Assert.Equal(1, fired);

        // And it fired from INSIDE the user's own predicate (go), not at the synthesized
        // top-level wrapper that calls it. The wrapper is a disable_debug goal whose port maps
        // to the end of the file — landing there put the caret on the last source line instead
        // of the first goal.
        Assert.Equal("go", topFrameAtFire);
    }

    [Fact]
    public void ArmEntryBreak_FiresInsideTheEntryPredicate_OnTheBundleLoadPath()
    {
        // The linked --exe path: a bundle loaded with debug codegen on (what
        // shumway-link --exe --debug produces and PrologEngine.LoadBundle rehydrates). A linked
        // exe builds its predicate-address map differently from a ConsultString engine, so the
        // caller-side gate — "fire only once engine.P is a debuggable address" — has to be
        // proven here too, not just in-process: this is where a bad gate would either never
        // fire (program runs past the entry) or fire at the wrapper's end-of-file port.
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.LoadBundle(new Bundle(new[]
        {
            new BundleEntry("dbg",
                ":- public main/0.\n" +
                "main :- greet(hello), greet(world).\n" +
                "greet(_).\n"),
        }));

        var service = new DebugService(engine, (_, _) => { });
        int fired = 0;
        string topFrameAtFire = "";
        service.EntryBreak = act =>
        {
            fired++;
            var frames = engine.CaptureFrames(act);
            topFrameAtFire = frames.Count > 0 ? frames[0].Name : "";
        };
        engine.AttachDebugSession(service);
        service.ArmEntryBreak();

        Assert.True(engine.QueryAll("main.").Any());

        Assert.Equal(1, fired);
        // On the bundle-load path too, the entry break lands inside main, not the wrapper.
        Assert.Equal("main", topFrameAtFire);
    }

    [Fact]
    public void WithoutArming_TheEntryBreakNeverFires()
    {
        // Plain --debug (no wait), or a --debug-wait whose timeout elapsed with nobody there:
        // the program must run normally, with no stop at the entry.
        var engine = DebugEngine("""
            go :- first(_).
            first(_).
            """);

        var service = new DebugService(engine, (_, _) => { });
        int fired = 0;
        service.EntryBreak = _ => fired++;
        engine.AttachDebugSession(service);
        // deliberately no ArmEntryBreak()

        Assert.True(engine.QueryAll("go.").Any());
        Assert.Equal(0, fired);
    }

    [Fact]
    public void TheChannelSessionsWiredEntryBreak_IsASafeNoOpWithNoDebugger()
    {
        // ChannelDebugSession wires the entry break to BreakHere, guarded by IsAttached —
        // BreakHere calls Debugger.Break() unconditionally, so the guard is what keeps an
        // armed-but-then-detached session from breaking to nobody. With no debugger in this
        // test process the arm reaches the first goal and the guard swallows it: the program
        // runs to its answer, no hang, no crash.
        var engine = DebugEngine("""
            go :- step(_).
            step(_).
            """);
        using var session = new ChannelDebugSession(engine, notify: _ => { });

        session.ArmEntryBreak();
        Assert.True(engine.QueryAll("go.").Any());
    }
}
