using System;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — the EXACT user repro: ShumBlintDebug -console c:\temp\Blint.pl, attach,
/// breakpoint at line 1005 (head of chk_directives1/2's first clause), F5, and at the stop
/// DISABLE the breakpoint and F5 again → "break opcode at PC=0x… with no breakpoint recorded".
/// Needs C:\temp\Blint.pl; no-ops (passes) where it is absent so the suite stays portable.</summary>
public class Adr035BlintDisableBp
{
    private readonly ITestOutputHelper _log;
    public Adr035BlintDisableBp(ITestOutputHelper log) => _log = log;

    private const string BlintPath = @"C:\temp\Blint.pl";

    [Fact]
    public void DisableBreakpointMidLint_ThenContinue_DoesNotDesyncTheCodeSpace()
    {
        if (!File.Exists(BlintPath)) { _log.WriteLine("Blint.pl absent — skipped"); return; }

        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).\n");
        engine.ConsultFile(BlintPath);
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        // Lint Blint.pl itself, the way the launcher does (dummy prog name + -console + file;
        // main drops the leading argv element for non-SICStus).
        engine.Flags.Argv = new[] { "blint", "-console", BlintPath.Replace("\\", "/") };

        Assert.True(engine.AddBreakpoint(BlintPath, 1005) > 0, "bp at chk_directives1/2 head should bind");

        int stops = 0;
        Exception? failed = null;
        var svc = new DebugService(engine, (s, e) =>
        {
            stops++;
            _log.WriteLine($"stop #{stops}: {e.Reason} {e.Goal} at {e.File}:{e.Line}");
            if (stops == 1) engine.RemoveBreakpoint(BlintPath, 1005);   // the "disable"
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
        Assert.Null(failed);        // no "out of step"
        Assert.True(stops >= 1, "the breakpoint must have been hit at least once");
    }
}
