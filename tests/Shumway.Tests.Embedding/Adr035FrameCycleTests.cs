using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — cycle-aware frame elision. A stack too deep to show whole is almost
/// always a recursion; the middle is elided on CYCLE boundaries so whole cycles survive at the
/// innermost end (where the machine is) and the outermost end (where the recursion began,
/// with the non-recursive origin frames), instead of a blind head/tail cut.</summary>
[Collection("debugger")]
public class Adr035FrameCycleTests
{
    private readonly ITestOutputHelper _log;
    public Adr035FrameCycleTests(ITestOutputHelper log) => _log = log;

    private sealed class Stop : Exception { }

    /// <summary>Consult a source with debug codegen + LCO off, break at the first breakpoint,
    /// and return the captured frames of that first stop.</summary>
    private IReadOnlyList<PrologEngine.DebugFrame> FramesAtBreak(
        string source, int breakLine, string goal)
    {
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.ConsultString(source);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        engine.AddBreakpoint("<string>", breakLine);

        IReadOnlyList<PrologEngine.DebugFrame>? frames = null;
        var svc = new DebugService(engine, (s, e) =>
        {
            frames = e.Frames;
            throw new Stop();
        });
        engine.AttachDebugSession(svc);
        try { engine.QueryAll(goal).ToList(); }
        catch (Stop) { }
        engine.AttachDebugSession(null);

        Assert.NotNull(frames);
        return frames!;
    }

    private static int OmittedIndex(IReadOnlyList<PrologEngine.DebugFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++)
            if (frames[i].Name.Contains("omitted", StringComparison.Ordinal)) return i;
        return -1;
    }

    private static int OmittedCount(PrologEngine.DebugFrame f)
    {
        // "... 1 frame omitted ..." / "... 1,234 frames omitted ..."
        var digits = new string(f.Name.Where(c => char.IsDigit(c)).ToArray());
        return digits.Length == 0 ? 0 : int.Parse(digits);
    }

    [Fact]
    public void PlainRecursion_ElidesOnCycleBoundaries_KeepingBothEndsAndTheOrigin()
    {
        // Lines are 1-based; keep marker's body on a known line.
        var src = string.Join("\n", new[]
        {
            "run :- origin.",                                  // 1
            "origin :- deep(400).",                            // 2  the non-recursive origin
            "deep(0) :- !, marker.",                           // 3
            "deep(N) :- N > 0, N1 is N - 1, deep(N1).",        // 4  the recursive cycle
            "marker :- true.",                                 // 5  <-- break here (line 5)
        });
        var frames = FramesAtBreak(src, breakLine: 5, goal: "run.");
        foreach (var f in frames) _log.WriteLine($"  {f.Name}/{f.Arity} :{f.Line}");

        int oi = OmittedIndex(frames);
        Assert.True(oi >= 0, "expected an omitted-frames sentence for a 400-deep recursion");
        Assert.True(OmittedCount(frames[oi]) > 100, "most of the recursion should be elided");

        // The budget is kept: at least ~100 frames still show (the middle is cut, not the ends).
        Assert.True(frames.Count >= 100, $"should keep the ~100-frame budget, got {frames.Count}");

        // The stack, innermost first: marker, deep(0), deep(1)... [omitted] ...deep, origin, query.
        // Innermost side (before the sentence): the current frame + kept cycles are all deep/marker.
        var innerNames = frames.Take(oi).Select(f => f.Name).ToList();
        Assert.Contains("marker", innerNames);
        Assert.Contains("deep", innerNames);
        Assert.True(innerNames.Count(n => n == "deep") >= 2, "keep >= 2 innermost cycles");

        // Outermost side (after the sentence): kept outermost cycles THEN the origin, so the
        // user can see where the chain started and Run-to-cursor onto the goal after it.
        var outerNames = frames.Skip(oi + 1).Select(f => f.Name).ToList();
        Assert.True(outerNames.Count(n => n == "deep") >= 2, "keep >= 2 outermost cycles");
        Assert.Contains("origin", outerNames);       // the non-recursive frame that started it
        int lastDeep = outerNames.FindLastIndex(n => n == "deep");
        int originAt = outerNames.IndexOf("origin");
        Assert.True(lastDeep < originAt, "origin sits BELOW the outermost kept cycle");
    }

    [Fact]
    public void MutualRecursion_DetectsPeriodTwo_KeepingWholePingPongCyclesAtEachEnd()
    {
        var src = string.Join("\n", new[]
        {
            "run :- origin.",                                  // 1
            "origin :- ping(400).",                            // 2
            "ping(0) :- !, marker.",                           // 3
            "ping(N) :- N > 0, N1 is N - 1, pong(N1).",        // 4
            "pong(0) :- !, marker.",                           // 5
            "pong(N) :- N > 0, N1 is N - 1, ping(N1).",        // 6
            "marker :- true.",                                 // 7  <-- break here
        });
        var frames = FramesAtBreak(src, breakLine: 7, goal: "run.");
        foreach (var f in frames) _log.WriteLine($"  {f.Name}/{f.Arity} :{f.Line}");

        int oi = OmittedIndex(frames);
        Assert.True(oi >= 0, "expected an omitted-frames sentence for a 400-deep mutual recursion");

        // The cut is on CYCLE boundaries: a whole number of period-2 cycles is elided, so the
        // omitted count is even — the tell-tale that neither end was sliced through a ping/pong.
        Assert.Equal(0, OmittedCount(frames[oi]) % 2);

        // Budget kept: at least ~100 frames still show.
        Assert.True(frames.Count >= 100, $"should keep the ~100-frame budget, got {frames.Count}");

        // Both ends must show BOTH members of the cycle — ping and pong — so a whole cycle is
        // visible, not half of one.
        var inner = frames.Take(oi).Select(f => f.Name).ToList();
        var outer = frames.Skip(oi + 1).Select(f => f.Name).ToList();
        Assert.Contains("ping", inner);
        Assert.Contains("pong", inner);
        Assert.Contains("ping", outer);
        Assert.Contains("pong", outer);
        Assert.Contains("origin", outer);
    }

    [Fact]
    public void ShallowStack_ShowsEveryFrame_NoElision()
    {
        var src = string.Join("\n", new[]
        {
            "run :- origin.",                                  // 1
            "origin :- deep(5).",                              // 2
            "deep(0) :- !, marker.",                           // 3
            "deep(N) :- N > 0, N1 is N - 1, deep(N1).",        // 4
            "marker :- true.",                                 // 5
        });
        var frames = FramesAtBreak(src, breakLine: 5, goal: "run.");
        Assert.Equal(-1, OmittedIndex(frames));   // nothing elided under the budget
        Assert.Contains(frames, f => f.Name == "marker");
        Assert.Contains(frames, f => f.Name == "origin");
    }
}
