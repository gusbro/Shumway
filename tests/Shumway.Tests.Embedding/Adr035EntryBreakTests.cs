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
        var engine = DebugEngine(
            "go :- first(X), second(X).\n" +
            "first(1).\n" +
            "second(_).\n");

        var service = new DebugService(engine, (_, _) => { });
        int fired = 0;
        Activation? at = null;
        service.EntryBreak = act => { fired++; at = act; };
        engine.AttachDebugSession(service);
        service.ArmEntryBreak();

        Assert.True(engine.QueryAll("go.").Any());

        // Exactly once — the first goal the program ran, and no later port re-triggers it
        // (there are several: first/1, second/1). And it hands over the running activation,
        // which is what BreakHere needs to render the entry stack.
        Assert.Equal(1, fired);
        Assert.NotNull(at);
    }

    [Fact]
    public void WithoutArming_TheEntryBreakNeverFires()
    {
        // Plain --debug (no wait), or a --debug-wait whose timeout elapsed with nobody there:
        // the program must run normally, with no stop at the entry.
        var engine = DebugEngine("go :- first(_).\nfirst(_).\n");

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
        var engine = DebugEngine("go :- step(_).\nstep(_).\n");
        using var session = new ChannelDebugSession(engine, notify: _ => { });

        session.ArmEntryBreak();
        Assert.True(engine.QueryAll("go.").Any());
    }
}
