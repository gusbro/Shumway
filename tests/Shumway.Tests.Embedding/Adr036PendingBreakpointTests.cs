using System;
using System.IO;
using System.Linq;
using System.Text;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>Bisecting the --dap-wait corruption: NO DAP anywhere — two engines in one
/// process, each arming a PENDING breakpoint (file not consulted yet) and then
/// consulting the same file. If the second engine fails, the bug is in the ADR-035
/// core (static DebugSiteTable vs per-engine state), not in the DAP frontend.</summary>
[Collection("debugger")]
public class Adr036PendingBreakpointTests
{
    private readonly ITestOutputHelper _log;
    public Adr036PendingBreakpointTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public void PendingBreakpointBeforeConsult_TwoEnginesSameFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-dapwait-repro2");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "app2.pl");
        var src = new StringBuilder();
        src.AppendLine(":- dynamic(log/1).");
        src.AppendLine("test :- gen(50), count.");
        src.AppendLine("gen(0) :- !.");
        src.AppendLine("gen(N) :- item(N, V), assertz(log(V)), N1 is N - 1, gen(N1).");
        src.AppendLine("count :- findall(X, log(X), L), length(L, N), N >= 50.");
        for (int i = 1; i <= 50; i++)
            src.AppendLine($"item({i}, v{i}).");
        File.WriteAllText(file, src.ToString());

        for (int round = 0; round < 3; round++)
        {
            var engine = new PrologEngine();
            ChannelDebugSession? sessionRef = null;
            var session = new ChannelDebugSession(engine, _ =>
                sessionRef!.Channel.WriteCommands(
                    new DebugCommand(DebugCommandKind.Continue)));
            sessionRef = session;
            try
            {
                engine.QueryAll("set_prolog_flag(compile_mode, debug).").ToList();

                // The launch shape: the breakpoint is drawn before the file exists in
                // this engine — pending, binds on consult.
                int bound = engine.AddBreakpoint(file, 2);
                _log.WriteLine($"round {round}: pre-consult bind count = {bound}");

                engine.ConsultFile(file);

                var sols = engine.QueryAll("test.").ToList();
                Assert.Single(sols);
                _log.WriteLine($"round {round}: ok");
            }
            finally
            {
                session.Dispose();
            }
        }
    }
}
