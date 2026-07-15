using System;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — debugging an EMBEDDED engine. The point is a large .NET application that uses
/// Shumway for one part of its work, in the application's own process: a debugger attached
/// to that process should be able to set breakpoints in the engine's <c>.pl</c> code, the
/// same as the standalone REPL's <c>--debug</c>. <see cref="PrologEngine.EnableDebugging"/>
/// is that switch, and a bundle that still carries its module source is shown FROM that
/// source.
/// </summary>
public class Adr035EmbeddingTests
{
    //   1: :- public run/1.
    //   2: run(X) :-
    //   3:     step(X).
    //   4: step(_).
    private const string ModuleName = "demo035";
    private const string Source = ":- public run/1.\nrun(X) :-\n    step(X).\nstep(_).\n";

    [Fact]
    public void EnableDebugging_TurnsOnDebugCodegen_AndIsDisposable()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Flags.DebugCodegen);

        using (ChannelDebugSession session = engine.EnableDebugging())
        {
            Assert.True(engine.Flags.EmitDebugInfo);
            Assert.True(engine.Flags.DebugCodegen);
            Assert.False(engine.Flags.DebugLco);        // off by default: a reclaimed frame is unshowable
            Assert.NotNull(ShumwayDebugHelper.Session);
        }

        // Disposed: the process is free to be debugged again.
        Assert.Null(ShumwayDebugHelper.Session);
    }

    [Fact]
    public void EnableDebugging_Twice_Throws_OneDebuggerPerProcess()
    {
        var engine = new PrologEngine();
        using ChannelDebugSession session = engine.EnableDebugging();

        var other = new PrologEngine();
        Assert.Throws<InvalidOperationException>(() => other.EnableDebugging());
    }

    [Fact]
    public void EnableDebugging_LastCallOptimisationOption_IsHonoured()
    {
        // Unless the SHUMWAY_DEBUG_LCO pin is set, the caller's choice wins. The suite does
        // not set the pin.
        Assert.Null(Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_LCO"));
        var engine = new PrologEngine();
        using ChannelDebugSession session =
            engine.EnableDebugging(new DebugOptions { LastCallOptimisation = true });
        Assert.True(engine.Flags.DebugLco);
    }

    [Fact]
    public void ASourceCarryingBundleLoadedUnderDebug_IsDebuggable_FromItsEmbeddedSource()
    {
        // The .shum case: a bundle that still carries its module source. Enabling debug
        // BEFORE loading it makes the load re-compile the module debuggable, and the source
        // is materialised to a file the debugger can open — the exact text that was compiled.
        var engine = new PrologEngine();
        using ChannelDebugSession session = engine.EnableDebugging();

        engine.LoadBundle(new Bundle(new[] { new BundleEntry(ModuleName, Source) }));

        // The body goal on line 3 is a real stop site: a breakpoint there binds. Identity is
        // by base name, so the module's file name is enough.
        int bound = engine.AddBreakpoint(ModuleName + ".pl", 3);
        Assert.True(bound > 0, "a breakpoint in the bundle's own source should bind");

        // And the source really was written out, byte for byte — that is what the debugger
        // opens, not a possibly-drifted .pl someone has on disk.
        string materialised = Path.Combine(
            Path.GetTempPath(), "shumway-debug",
            "src-" + Environment.ProcessId, ModuleName + ".pl");
        Assert.True(File.Exists(materialised), "the embedded source should be materialised");
        Assert.Equal(Source, File.ReadAllText(materialised));

        // The code runs, too — enabling debug did not break it.
        Assert.Single(engine.QueryAll("run(1).").ToList());
    }

    [Fact]
    public void WithoutEnableDebugging_TheSameBundleIsNotDebuggable()
    {
        // The control: no debug session, so the module compiles release — no stop sites, and
        // a breakpoint binds nothing. This is what EnableDebugging changes.
        var engine = new PrologEngine();
        engine.LoadBundle(new Bundle(new[] { new BundleEntry(ModuleName, Source) }));

        int bound = engine.AddBreakpoint(ModuleName + ".pl", 3);
        Assert.Equal(0, bound);
        Assert.Single(engine.QueryAll("run(1).").ToList());
    }
}
