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
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                catch(work, _E, recover),
                done.
            work :- writeln(working).
            recover :- writeln(recovered).
            done.
            """);
        engine.AddBreakpoint("<string>", 3);   // writeln(hello)

        var stops = Walk(engine, "main.", StepMode.Over, StepMode.Over);

        // Step over writeln(hello) and the caret lands ON the catch, at line 4 — the feedback
        // the user needs before choosing F10 (over the whole catch) or F11 (into it). Before
        // the fix this reported line 3, so the caret never moved.
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(4, stops[1].Line);

        // And it is named for the construct the user wrote — catch/3 — not the '$catchgoal_N'
        // helper the meta transform lowered it to.
        Assert.Equal("catch/3", stops[1].Goal);
    }

    [Fact]
    public void SteppingOverAGoalBeforeANegation_LandsOnTheNegationLine()
    {
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     \+ absent,
        //   5:     done.
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                \+ absent,
                done.
            absent :- fail.
            done.
            """);
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
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                findall(X, item(X), _Xs),
                done.
            item(1).
            item(2).
            done.
            """);
        engine.AddBreakpoint("<string>", 3);

        var stops = Walk(engine, "main.", StepMode.Over);

        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(4, stops[1].Line);
    }

    [Fact]
    public void StepOut_FromInsideACatchGoal_StopsAtTheNextGoal_NotRunToEnd()
    {
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     catch(work, _E, recover),
        //   5:     done.
        //   6: work :-
        //   7:     inner(V),
        //   8:     use(V).
        //   9: inner(V) :-
        //  10:     leaf(V).
        //  11: leaf(1).
        //  12: use(_).
        //  13: recover.
        //  14: done.
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                catch(work, _E, recover),
                done.
            work :-
                inner(V),
                use(V).
            inner(V) :-
                leaf(V).
            leaf(1).
            use(_).
            recover.
            done.
            """);
        engine.AddBreakpoint("<string>", 10);   // leaf(V), inside inner, inside work, inside catch

        var stops = Walk(engine, "main.", StepMode.Out);

        _log.WriteLine("PORTS: " + string.Join(" | ", stops.Select(s => $"{s.Reason} {s.Goal}@{s.Line}")));

        // Stopped on leaf(V) (line 10) inside inner/1. Step Out leaves inner/1 and lands on the
        // next goal an ENCLOSING clause runs — use(V), line 8, back in work/1. Before the fix
        // it ran to the end of the program (the catch helper frames confused the depth walk).
        Assert.True(stops.Count >= 2, "Step Out must stop somewhere, not run to the end");
        Assert.Equal(StopReason.Call, stops[1].Reason);
        Assert.Equal(8, stops[1].Line);
    }

    [Fact]
    public void StepOut_FromInsideAnInlineCatchConjunction_StopsAtTheNextGoal()
    {
        // The Blint shape: catch wraps an INLINE conjunction, so its goals are compiled INTO
        // the '$catchgoal' helper (not a separate predicate). Stepping into one of them and
        // then Step Out must land on the NEXT goal of that conjunction, not run to the end.
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     catch((first(V), second(V)), _E, recover),
        //   5:     done.
        //   6: first(V) :-
        //   7:     leaf(V).
        //   8: leaf(1).
        //   9: second(_).
        //  10: recover.
        //  11: done.
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                catch((first(V), second(V)), _E, recover),
                done.
            first(V) :-
                leaf(V).
            leaf(1).
            second(_).
            recover.
            done.
            """);
        engine.AddBreakpoint("<string>", 7);   // leaf(V), inside first, inside the catch conjunction

        var stops = Walk(engine, "main.", StepMode.Out);

        _log.WriteLine("PORTS: " + string.Join(" | ", stops.Select(s => $"{s.Reason} {s.Goal}@{s.Line}")));

        // Step Out of first/1 lands on second(V) — the next goal of the catch's conjunction.
        // The bug report: it ran the whole program instead.
        Assert.True(stops.Count >= 2, "Step Out must stop at the next goal, not run to the end");
        Assert.Equal("second/1", stops[1].Goal);
    }

    [Fact]
    public void StepOut_FromInsideTheLastCatchGoal_CrossesCatchEnd_AndStopsAfterTheCatch()
    {
        // Step Out from inside the LAST goal of the catch conjunction: after it, the only thing
        // left in the '$catchgoal' helper is the internal '$catch_end', then control returns to
        // main's next goal. Step Out must cross that internal boundary and stop on done, not run
        // to the end.
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     catch((first(V), last(V)), _E, recover),
        //   5:     done.
        //   6: first(V) :-
        //   7:     V = 1.
        //   8: last(V) :-
        //   9:     leaf(V).
        //  10: leaf(1).
        //  11: recover.
        //  12: done.
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                catch((first(V), last(V)), _E, recover),
                done.
            first(V) :-
                V = 1.
            last(V) :-
                leaf(V).
            leaf(1).
            recover.
            done.
            """);
        engine.AddBreakpoint("<string>", 9);   // leaf(V), inside last — the catch's final goal

        var stops = Walk(engine, "main.", StepMode.Out);

        _log.WriteLine("PORTS: " + string.Join(" | ", stops.Select(s => $"{s.Reason} {s.Goal}@{s.Line}")));

        Assert.True(stops.Count >= 2, "Step Out must stop after the catch, not run to the end");
        Assert.Equal("done/0", stops[1].Goal);
        Assert.Equal(5, stops[1].Line);
    }

    [Fact]
    public void F10ToCatch_ThenF11IntoIt_ThenStepOut_FollowsTheStackBackOut()
    {
        // Mirrors the user's keystrokes exactly: F10 to the catch, F11 into it and down through
        // a nested goal (concat), then Step Out from inside concat. Step Out must follow the
        // stack back to the goal after concat — it must not run to the end.
        //   2: main :-
        //   3:     writeln(hello),
        //   4:     catch(body, _E, recover),
        //   5:     done.
        //   6: body :-
        //   7:     concat(a, b, _R),
        //   8:     after.
        //   9: concat(_, _, r) :-
        //  10:     join.
        //  11: join.
        //  12: after.
        //  13: recover.
        //  14: done.
        var engine = DebugEngine("""
            main :-
                writeln(hello),
                catch(body, _E, recover),
                done.
            body :-
                concat(a, b, _R),
                after.
            concat(_, _, r) :-
                join.
            join.
            after.
            recover.
            done.
            """);
        engine.AddBreakpoint("<string>", 3);   // writeln(hello)

        var stops = Walk(engine, "main.",
            StepMode.Over,   // [0]->[1] to catch (catch/3, line 4)
            StepMode.Into,   // [1]->[2] into catch -> body (line 4)
            StepMode.Into,   // [2]->[3] into body -> concat (line 7)
            StepMode.Into,   // [3]->[4] into concat -> join (line 10, INSIDE concat)
            StepMode.Out);   // [4]->[5] STEP OUT from inside concat

        _log.WriteLine("PORTS: " + string.Join("\n       ",
            stops.Select((s, i) => $"[{i}] {s.Reason} {s.Goal}@{s.Line}")));

        // The keystrokes visit catch/3, body, concat, join; Step Out then lands on after/1 —
        // the goal after concat, back in body — not run-to-end.
        Assert.Equal("catch/3", stops[1].Goal);
        Assert.True(stops.Count >= 6, "Step Out from inside concat must stop, not run to the end");
        Assert.Equal("after/0", stops[5].Goal);
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
        var engine = DebugEngine("""
            greet -->
                pre,
                [world],
                post.
            pre --> [hello].
            post --> [end].
            """);
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
