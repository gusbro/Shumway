using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 phase D1 — per-frame variables: what the user called them, and what they are
/// bound to right now.
///
/// <para>This is the one thing release codegen actively destroys. The WAM keeps a
/// variable in an X register whenever it can, and the next call overwrites it; a
/// variable that survives a call is kept in a Y slot, but only until the last call that
/// needs it, after which trimming drops it. Both are exactly right for running the
/// program and useless for debugging it. So debug codegen makes every named variable
/// permanent and never trims — which is the reason debug and release are not the same
/// code, stated in one sentence.</para>
/// </summary>
public class Adr035VariablesTests
{
    private readonly ITestOutputHelper _log;

    public Adr035VariablesTests(ITestOutputHelper log) => _log = log;

    private static PrologEngine DebugEngine(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n" + program);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        return engine;
    }

    private List<DebugStopEvent> Walk(
        PrologEngine engine, string goal, params StepMode[] steps)
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
            _log.WriteLine($"{s.Reason,-10} {s.Goal,-10} {s.File}:{s.Line}  "
                + string.Join(", ", s.Variables.Select(v => $"{v.Name} = {v.Value}")));
        return stops;
    }

    private static string Value(DebugStopEvent stop, string name) =>
        stop.Variables.First(v => v.Name == name).Value;

    [Fact]
    public void TheHeadArgumentsAreBoundByTheTimeWeStopInTheClause()
    {
        //   2: p(X, Y) :-
        //   3:     q(X, Y).
        //   4: q(_, _).
        var engine = DebugEngine("p(X, Y) :-\n    q(X, Y).\nq(_, _).\n");
        engine.AddBreakpoint("<string>", 2);

        var stop = Walk(engine, "p(hello, 42).")[0];

        // The clause's entry stop sits AFTER head unification, so the head arguments are
        // matched: a debugger stopped at a clause shows what it was called with.
        Assert.Equal("hello", Value(stop, "X"));
        Assert.Equal("42", Value(stop, "Y"));
    }

    [Fact]
    public void ABodyVariableIsUnboundBeforeItsGoalRunsAndBoundAfter()
    {
        //   2: p(Out) :-
        //   3:     mk(Mid),
        //   4:     Out = got(Mid).
        //   5: mk(seven).
        var engine = DebugEngine("p(Out) :-\n    mk(Mid),\n    Out = got(Mid).\nmk(seven).\n");
        engine.AddBreakpoint("<string>", 3);   // before mk(Mid) runs
        engine.AddBreakpoint("<string>", 4);   // after it

        var stops = Walk(engine, "p(R).");

        Assert.StartsWith("_", Value(stops[0], "Mid"));   // not yet bound to anything
        Assert.Equal("seven", Value(stops[1], "Mid"));    // mk/1 bound it
    }

    [Fact]
    public void AVariableIsStillThereAfterTheCallThatUsedIt()
    {
        // The trimming case. Release codegen drops a Y slot at the last call that needs
        // it, so `First` would be gone by line 4 — and it is precisely the variable a
        // user stopped on line 4 wants to look at. Debug codegen does not trim.
        //
        //   2: p :-
        //   3:     use(First),
        //   4:     use(Second),
        //   5:     done(First, Second).
        //   6: use(x).
        //   7: done(_, _).
        var engine = DebugEngine(
            "p :-\n    use(First),\n    use(Second),\n    done(First, Second).\n"
            + "use(x).\ndone(_, _).\n");
        engine.AddBreakpoint("<string>", 5);

        var stop = Walk(engine, "p.")[0];

        Assert.Equal("x", Value(stop, "First"));
        Assert.Equal("x", Value(stop, "Second"));
    }

    [Fact]
    public void EveryFrameOnTheStackHasItsOwnVariables()
    {
        //   2: top(A) :-
        //   3:     mid(A, B),
        //   4:     use(B).
        //   5: mid(In, Out) :-
        //   6:     leaf(In, Out).
        //   7: leaf(I, out(I)).
        //   8: use(_).
        var engine = DebugEngine(
            "top(A) :-\n    mid(A, B),\n    use(B).\n"
            + "mid(In, Out) :-\n    leaf(In, Out).\nleaf(I, out(I)).\nuse(_).\n");
        engine.AddBreakpoint("<string>", 7);   // inside leaf/2

        var frames = Walk(engine, "top(one).")[0].Frames;
        foreach (var f in frames)
            _log.WriteLine($"{f.Name}/{f.Arity}: "
                + string.Join(", ", f.Variables.Select(v => $"{v.Name} = {v.Value}")));

        // Each frame names its variables the way ITS clause did — the same value carries
        // three different names down the stack, which is exactly what the user needs to
        // see and what a raw heap dump could never tell them.
        Assert.Equal("one", frames.Single(f => f.Name == "leaf").Variables
            .First(v => v.Name == "I").Value);
        Assert.Equal("one", frames.Single(f => f.Name == "mid").Variables
            .First(v => v.Name == "In").Value);
        Assert.Equal("one", frames.Single(f => f.Name == "top").Variables
            .First(v => v.Name == "A").Value);
    }

    [Fact]
    public void CompoundsAndListsAreRenderedAsTheUserWroteThem()
    {
        //   2: p(T) :-
        //   3:     q(T).
        //   4: q(_).
        var engine = DebugEngine("p(T) :-\n    q(T).\nq(_).\n");
        engine.AddBreakpoint("<string>", 2);

        Assert.Equal("[1, 2, 3]", Value(Walk(engine, "p([1,2,3]).")[0], "T"));
        Assert.Equal("point(1, 2)", Value(Walk(engine, "p(point(1,2)).")[0], "T"));
        Assert.Equal("a+b", Value(Walk(engine, "p(a+b).")[0], "T"));
    }

    [Fact]
    public void AnonymousVariablesAreNotReported()
    {
        // `_` and `_Ignored` are the two ways of saying "I do not care about this one".
        // A debugger that shows them anyway is just noise.
        //
        //   2: p(Keep, _Drop, _) :-
        //   3:     q(Keep).
        //   4: q(_).
        var engine = DebugEngine("p(Keep, _Drop, _) :-\n    q(Keep).\nq(_).\n");
        engine.AddBreakpoint("<string>", 2);

        var stop = Walk(engine, "p(a, b, c).")[0];

        Assert.Equal(new[] { "Keep" }, stop.Variables.Select(v => v.Name));
    }

    [Fact]
    public void TheVariablesFollowBacktrackingBackToWhatTheyWere()
    {
        //   2: p(1).
        //   3: p(2).
        //   4: t :-
        //   5:     p(X),
        //   6:     X > 1.
        var engine = DebugEngine("p(1).\np(2).\nt :-\n    p(X),\n    X > 1.\n");
        engine.AddBreakpoint("<string>", 6);   // the guard, after p(X) has bound X

        var stops = Walk(engine, "t.");

        // Two stops: X = 1, the guard fails, p/1 is redone, X = 2. The binding was
        // undone by backtracking, and the frame shows it — a debugger reading the live
        // machine cannot be out of date with it.
        Assert.Equal(new[] { "1", "2" }, stops.Select(s => Value(s, "X")));
    }

    [Fact]
    public void ReleaseCodeHasNoVariablesToShow_AndSaysSoRatherThanGuessing()
    {
        // The same program without compile_mode=debug. Nothing is instrumented, so
        // nothing binds — and the frames that a stop would have shown do not exist.
        var engine = new PrologEngine();
        engine.ConsultString("p(X) :-\n    q(X).\nq(_).\n");

        Assert.Equal(0, engine.AddBreakpoint("<string>", 2));
        Assert.Empty(engine.Breakpoints);
    }
}
