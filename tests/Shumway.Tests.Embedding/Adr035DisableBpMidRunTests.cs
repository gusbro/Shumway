using System;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — the disable-mid-run desync. The original report came
/// from a real-program session: breakpoint on the head of a predicate the
/// run calls hundreds of times, F5, and at the stop DISABLE the breakpoint
/// and F5 again → "break opcode at PC=0x… with no breakpoint recorded". The
/// synthetic program reproduces the shape self-contained: a debug-compiled
/// predicate driven in a loop, the breakpoint removed INSIDE its first stop,
/// and the loop then re-entering the (formerly) patched code many times.</summary>
[Collection("debugger")]
public class Adr035DisableBpMidRunTests
{
    private readonly ITestOutputHelper _log;
    public Adr035DisableBpMidRunTests(ITestOutputHelper log) => _log = log;

    // Line numbers are load-bearing:
    //  1: main :-
    //  2:     between(1, 200, I),
    //  3:     check(I),
    //  4:     fail.
    //  5: main.
    //  6: check(I) :-
    //  7:     0 is I mod 2,
    //  8:     !.
    //  9: check(_).
    private const string Source = """
        main :-
            between(1, 200, I),
            check(I),
            fail.
        main.
        check(I) :-
            0 is I mod 2,
            !.
        check(_).
        """;

    [Fact]
    public void DisableBreakpointMidRun_ThenContinue_DoesNotDesyncTheCodeSpace()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_disablebp_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, Source + "\n");
        try
        {
            var engine = new PrologEngine();
            engine.Flags.EmitDebugInfo = true;
            engine.Flags.DebugCodegen = true;
            engine.ConsultString(":- set_prolog_flag(compile_mode, debug).");
            engine.ConsultFile(path);
            engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

            Assert.True(engine.AddBreakpoint(path, 6) > 0,
                "bp at check/1's head should bind");

            int stops = 0;
            Exception? failed = null;
            var svc = new DebugService(engine, (s, e) =>
            {
                stops++;
                _log.WriteLine($"stop #{stops}: {e.Reason} {e.Goal} at {e.File}:{e.Line}");
                if (stops == 1) engine.RemoveBreakpoint(path, 6);   // the "disable"
                s.Resume(StepMode.Continue);
            });
            engine.AttachDebugSession(svc);
            try
            {
                engine.QueryAll("main.").ToList();
            }
            catch (Exception ex)
            {
                failed = ex;
                _log.WriteLine("THREW: " + ex.Message);
            }
            finally
            {
                engine.AttachDebugSession(null);
            }

            _log.WriteLine($"total stops = {stops}");
            Assert.Null(failed);        // no "out of step" / stray break opcode
            Assert.True(stops >= 1, "the breakpoint must have been hit at least once");
            // The 199 calls AFTER the removal ran the formerly-patched code:
            // exactly one stop means the removal really unpatched it.
            Assert.Equal(1, stops);
        }
        finally { File.Delete(path); }
    }
}
