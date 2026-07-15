using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — a control construct rewritten to a synthesised helper (catch/3, \+, once/ignore,
/// findall/bagof/setof/forall) must keep the SOURCE LINE of the goal it replaced.
///
/// <para>The report: stopped on <c>main</c>'s first goal (a writeln), F10 printed the text but
/// the caret did not move, and a second F10 ran the entire body of the following <c>catch/3</c>.
/// The cause was in <c>MetaTransform</c>: the rewrite replaced <c>catch(...)</c> with a call to
/// a fresh <c>'$catchgoal_N'</c> helper, and the fresh compound had no source position — so the
/// debug compiler mapped its call port to the PREVIOUS goal's line. The step DID stop on the
/// catch; it just reported the writeln's line, so it looked like nothing had happened. The
/// replacement now carries the construct's own position.</para>
/// </summary>
public class Adr035CatchRepro
{
    private readonly ITestOutputHelper _log;
    public Adr035CatchRepro(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    private List<DebugStopEvent> Walk(PrologEngine engine, string goal, params StepMode[] steps)
    {
        var stops = new List<DebugStopEvent>();
        int next = 0;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops.Add(e);
            if (next < steps.Length) s.Resume(steps[next++]);
        });
        engine.AttachDebugSession(svc);
        engine.QueryAll(goal).ToList();
        engine.AttachDebugSession(null);
        foreach (var s in stops)
            _log.WriteLine($"{s.Reason,-12} {s.Goal,-16} depth={s.Depth} {s.File}:{s.Line}");
        return stops;
    }

    [Fact]
    public void SteppingOverAGoalBeforeACatch_LandsOnTheCatchLine_NotThePreviousGoals()
    {
        //   1: :- set_prolog_flag(compile_mode, debug).
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     catch(work, _E, recover),
        //   5:     done.
        //   6: work :- writeln(working).
        //   7: recover :- writeln(recovered).
        //   8: done.
        var engine = DebugEngine(
            "main :-\n    writeln(hello),\n    catch(work, _E, recover),\n    done.\n" +
            "work :- writeln(working).\nrecover :- writeln(recovered).\ndone.\n");
        engine.AddBreakpoint("<string>", 3);   // writeln(hello)

        var stops = Walk(engine, "main.", StepMode.Over, StepMode.Over);

        // Step over writeln(hello) and the caret lands ON the catch, at line 4 — the feedback
        // the user needs before choosing F10 (over the whole catch) or F11 (into it). Before
        // the fix this reported line 3, so the caret never moved.
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(4, stops[1].Line);
    }

    [Fact]
    public void SteppingOverAGoalBeforeANegation_LandsOnTheNegationLine()
    {
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     \+ absent,
        //   5:     done.
        var engine = DebugEngine(
            "main :-\n    writeln(hello),\n    \\+ absent,\n    done.\n" +
            "absent :- fail.\ndone.\n");
        engine.AddBreakpoint("<string>", 3);

        var stops = Walk(engine, "main.", StepMode.Over);

        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(4, stops[1].Line);
    }

    [Fact]
    public void SteppingOverAGoalBeforeAFindall_LandsOnTheFindallLine()
    {
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     findall(X, item(X), _Xs),
        //   5:     done.
        var engine = DebugEngine(
            "main :-\n    writeln(hello),\n    findall(X, item(X), _Xs),\n    done.\n" +
            "item(1).\nitem(2).\ndone.\n");
        engine.AddBreakpoint("<string>", 3);

        var stops = Walk(engine, "main.", StepMode.Over);

        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(4, stops[1].Line);
    }

    [Fact]
    public void SteppingThroughADcgRule_LandsEachGoalOnItsOwnLine()
    {
        // A DCG body is translated to difference-list goals — a terminal `[x]` becomes a
        // `S0 = [x|S]` unify, a non-terminal `nt` becomes `nt(S0, S)`. Both are stop sites; a
        // fresh goal with no position mapped to the wrong line, exactly like the meta-construct
        // helpers. Each element must keep its own DCG-body line. (A LEADING terminal is peeled
        // into the head by the fail-fast lowering, so the body starts with a non-terminal here
        // to keep every element a real, on-its-own-line stop site.)
        //   2: greet -->
        //   3:     pre,
        //   4:     [world],
        //   5:     post.
        //   6: pre --> [hello].
        //   7: post --> [end].
        var engine = DebugEngine(
            "greet -->\n    pre,\n    [world],\n    post.\n" +
            "pre --> [hello].\npost --> [end].\n");
        engine.AddBreakpoint("<string>", 3);   // the `pre` non-terminal

        var stops = Walk(engine, "phrase(greet, [hello, world, end]).",
            StepMode.Into, StepMode.Into, StepMode.Into, StepMode.Into, StepMode.Into);

        // Each element of greet's body stops on its OWN line: pre on 3, the [world] terminal on
        // 4, post on 5 — not all collapsed onto the first. Before the fix, [world]'s unify goal
        // had no position and reported line 3.
        var lines = stops.Where(s => s.File == "<string>").Select(s => s.Line).Distinct().ToList();
        _log.WriteLine("DCG lines: " + string.Join(",", lines));
        Assert.Contains(3, lines);
        Assert.Contains(4, lines);
        Assert.Contains(5, lines);
    }
}
