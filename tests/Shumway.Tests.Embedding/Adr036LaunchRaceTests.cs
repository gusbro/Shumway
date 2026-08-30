using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Shumway.Embedding.Debugging.Dap;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>Repro of the user's --dap-wait corruption: breakpoints armed BEFORE the
/// consult (the launch flow), consult after, then run — expect a working program.</summary>
[Collection("debugger")]
public class Adr036LaunchRaceTests
{
    private readonly ITestOutputHelper _log;
    public Adr036LaunchRaceTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public void BreakpointsArmedBeforeConsult_ProgramStillRuns()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-dapwait-repro");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "app.pl");
        // A program with some meat, so compile/link does real work.
        var src = new StringBuilder();
        src.AppendLine(":- dynamic(log/1).");
        src.AppendLine("test :- gen(200), count.");
        src.AppendLine("gen(0) :- !.");
        src.AppendLine("gen(N) :- item(N, V), assertz(log(V)), N1 is N - 1, gen(N1).");
        src.AppendLine("count :- findall(X, log(X), L), length(L, N), write(n(N)), nl.");
        for (int i = 1; i <= 200; i++)
            src.AppendLine($"item({i}, v{i}).");
        File.WriteAllText(file, src.ToString());

        for (int round = 0; round < 5; round++)
        {
            var engine = new PrologEngine();
            var session = engine.EnableDebugging(new DebugOptions
            {
                SourceFiles = new[] { file },
                DapPort = 0,
            });
            try
            {
                using var client = new DapTestClient(session.DapPort!.Value);
                client.Request("initialize");
                client.WaitEvent("initialized");
                // The user's flow: breakpoints for the file BEFORE it is consulted.
                client.Request("setBreakpoints",
                    "{\"source\":{\"path\":" + JsonSerializer.Serialize(file) + "},"
                    + "\"breakpoints\":[{\"line\":2}]}");   // test/0's body: hit once
                client.Request("configurationDone");
                Assert.True(session.WaitForDapConfigured(TimeSpan.FromSeconds(10)),
                    "configure gate");

                // The REPL's next step: consult. (Main thread, like the REPL.)
                engine.ConsultFile(file);

                // Now run — the engine will stop at the breakpoint; drive it on.
                var query = new QueryRun(engine, "test.");
                int seen = 0;
                try
                {
                    for (; seen < 500; seen++)
                    {
                        // 30 s, not 10: under the parallel suite on a saturated runner a
                        // DELAYED stop (starved engine thread, socket latency) arrives
                        // late and is fine; a LOST stop never arrives and fails at any
                        // timeout - so the generous wait discriminates the two instead
                        // of conflating them.
                        JsonElement ev = client.WaitEvent("stopped", 30000);
                        _log.WriteLine("stop " + seen + ": "
                            + ev.GetProperty("body").GetProperty("reason").GetString());
                        client.Request("continue", "{\"threadId\":1}");
                        if (query.Join(50)) break;
                    }
                }
                catch (TimeoutException tex)
                {
                    _log.WriteLine("no more stops after " + seen + ": " + tex.Message);
                }
                bool done = query.Join(30_000);
                if (!done)
                {
                    JsonElement st = client.Request("stackTrace", "{\"threadId\":1}");
                    _log.WriteLine("STUCK; stackTrace: " + st.GetRawText());
                }
                Assert.True(done, "round " + round + ": query must complete");
                Assert.Single(query.Solutions!);

                // The user's second symptom: listing walked the program and then hit
                // "reserved_invalid opcode ... bytecode corruption".
                var listing = new QueryRun(engine, "listing.");
                Assert.True(listing.Join(20_000), "round " + round + ": listing done");

                // And the code must still RUN after everything.
                var again = new QueryRun(engine, "test.");
                for (int stops = 0; stops < 5 && !again.Join(50); stops++)
                {
                    client.WaitEvent("stopped", 30000);   // same 30 s discrimination as above
                    client.Request("continue", "{\"threadId\":1}");
                }
                Assert.True(again.Join(20_000), "round " + round + ": rerun done");
            }
            finally
            {
                session.Dispose();
            }
            _log.WriteLine("round " + round + " ok");
        }
    }
}
