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

    /// <summary>Load the bundle debuggable, stop at line 3, and return the materialised source
    /// path the debugger's own frame names — the exact file it would open.</summary>
    private static string MaterialisedFileAtBreak()
    {
        var engine = new PrologEngine();
        engine.Flags.EmitDebugInfo = true;
        engine.Flags.DebugCodegen = true;
        engine.LoadBundle(new Bundle(new[] { new BundleEntry(ModuleName, Source) }));
        engine.QueryAll("set_prolog_flag(debug_lco, off).").ToList();

        string file = "";
        var svc = new DebugService(engine, (s, e) =>
        {
            if (file == "" && e.Frames.Count > 0) file = e.Frames[0].File;
            s.Resume(StepMode.Continue);
        });
        engine.AttachDebugSession(svc);
        Assert.True(engine.AddBreakpoint(ModuleName + ".pl", 3) > 0,
            "a breakpoint in the bundle's own source should bind");
        Assert.Single(engine.QueryAll("run(1).").ToList());   // and the code still runs
        engine.AttachDebugSession(null);
        return file;
    }

    [Fact]
    public void ASourceCarryingBundleLoadedUnderDebug_IsDebuggable_FromItsEmbeddedSource()
    {
        // The .shum case: a bundle that still carries its module source. Loading it debuggable
        // materialises the source to a file the debugger can open — the exact text that was
        // compiled, not a possibly-drifted .pl someone has on disk.
        string materialised = MaterialisedFileAtBreak();
        Assert.NotEqual("", materialised);
        Assert.True(File.Exists(materialised), "the embedded source should be materialised");

        // Line endings are normalised to one consistent style (CRLF) so the editor never
        // reports a mixed-EOL file, but every line boundary — and so every breakpoint line — is
        // preserved.
        string expectedText = Source.Replace("\r\n", "\n").Replace("\n", "\r\n");
        string text = File.ReadAllText(materialised);
        Assert.Equal(expectedText, text);
        Assert.DoesNotContain('\n', text.Replace("\r\n", ""));   // no lone LF: consistent CRLF

        // No hot relinking, so the materialised source is READ-ONLY — an edit there could not
        // reach the running code.
        Assert.True(File.GetAttributes(materialised).HasFlag(FileAttributes.ReadOnly),
            "the materialised source should be read-only");
    }

    [Fact]
    public void ReMaterialisingTheSameSource_ReusesTheSamePath_AndStaysReadOnly()
    {
        // Re-running the same binary (here: a second load in the same process, same executable)
        // must land on the SAME materialised path so the debugger reuses its window and keeps its
        // breakpoints — instead of a second identical window that orphans them. And
        // re-materialising over the now-read-only file must not throw.
        string first = MaterialisedFileAtBreak();
        Assert.NotEqual("", first);
        Assert.True(File.GetAttributes(first).HasFlag(FileAttributes.ReadOnly));

        string second = MaterialisedFileAtBreak();   // the "re-run" — no throw over the read-only file
        Assert.Equal(first, second);                 // SAME path — the debugger reuses its window
        Assert.True(File.GetAttributes(second).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void FromBundleWithDebug_ProducesADebuggableEngine()
    {
        // What the `shumway-link --dll` factory's CreateEngine(debug: true) calls: the
        // create-and-load path enables debugging on the fresh engine BEFORE it consults the
        // bundle, so the modules load debuggable — a host that only has the generated factory
        // can still debug.
        PrologEngine engine = PrologEngine.FromBundle(
            new Bundle(new[] { new BundleEntry(ModuleName, Source) }),
            new DebugOptions());
        try
        {
            Assert.True(engine.Flags.DebugCodegen);
            Assert.True(engine.AddBreakpoint(ModuleName + ".pl", 3) > 0);
            Assert.Single(engine.QueryAll("run(1).").ToList());
        }
        finally
        {
            // FromBundle discards the session (rooted in the process-wide static); the
            // instance path returns it to dispose, but here the test must release it so the
            // one-debugger-per-process slot is free for the next test.
            ShumwayDebugHelper.Session?.Dispose();
        }
    }

    [Fact]
    public void FromBundleWithoutDebug_IsUnchanged()
    {
        // The default overload stays release: no session, no debug codegen.
        PrologEngine engine = PrologEngine.FromBundle(
            new Bundle(new[] { new BundleEntry(ModuleName, Source) }));
        Assert.False(engine.Flags.DebugCodegen);
        Assert.Null(ShumwayDebugHelper.Session);
        Assert.Single(engine.QueryAll("run(1).").ToList());
    }

    [Fact]
    public void ExeDebug_OnASourceStrippedBundle_FailsThePrecondition()
    {
        // ADR-035 --exe --debug: a debuggable exe materialises its modules' embedded source
        // at startup for the debugger to open. A source-stripped bundle has none, so the
        // link-time precondition must fail loudly — BEFORE any dotnet-publish — rather than
        // ship an undebuggable "debug" exe. (Cheap: Emit returns at the precondition.)
        var full = new Bundle(new[] { new BundleEntry("m", ":- public go/0.\ngo.\n") });
        byte[] withBc = BundleWriter.ToBytes(full, includeCompiledBytecode: true);
        Bundle readBack = BundleReader.FromBytes(withBc);

        // Rebuild each entry keeping the compiled bytecode but dropping the source — exactly
        // what a --strip link (or --release .shmo inputs) produces.
        var stripped = readBack.Entries
            .Select(e => new BundleEntry(e.ModuleName, "",
                compiledBytecode: e.CompiledBytecode, defined: e.Defined))
            .ToArray();
        byte[] strippedBytes = BundleWriter.ToBytes(new Bundle(stripped));

        string exePath = Path.Combine(Path.GetTempPath(), "shumway-dbg-precond-" +
            Guid.NewGuid().ToString("N") + ".exe");
        ExecutableEmitResult res = ExecutableEmitter.Emit(
            strippedBytes, goal: "go", outputPath: exePath, debug: true);

        Assert.False(res.Success);
        Assert.Contains(res.Diagnostics, d => d.Code == "debug_no_source");
        Assert.False(File.Exists(exePath), "no exe should be produced when the precondition fails");
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
