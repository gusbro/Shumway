using System.Linq;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 — a bundle compiled with <c>shumway-compile --debug</c> bakes the debuggable WAM
/// AND its debug side tables (stop sites, per-clause frames/variables/head-args) straight into
/// the <c>.shmo</c>, so a debug bundle is debuggable at load with no re-consult from source.
/// These tests cover the two halves that enable it: the compiler emits the debug info under
/// Debug build mode, and <see cref="CompiledModuleCodec"/> round-trips it (re-interning stop
/// sites into the loading process's <see cref="DebugSiteTable"/>).
/// </summary>
[Collection("debugger")]
public class Adr035DebugBundleTests
{
    private const string Source =
        ":- public run/1.\n" +          // 1
        "run(X) :-\n" +                 // 2
        "    helper(X, Y),\n" +         // 3
        "    emit(Y).\n" +              // 4
        "helper(a, b).\n" +             // 5
        "emit(_).\n";                   // 6

    [Fact]
    public void DebugBuildMode_EmitsDebugSideTables_IntoTheCompiledModule()
    {
        // Task 1: ShmoBuildMode.Debuggable turns on DebugCodegen, so the compiled predicates
        // carry stop sites and per-clause frames. Release (and plain Debug) do not.
        var release = ShmoCompiler.CompileSource(Source, "demo", ShmoBuildMode.Release);
        var relMod = CompiledModuleCodec.Decode(release.Bytecode);
        Assert.All(relMod.Predicates, p => Assert.Empty(p.DebugStops));

        var debug = ShmoCompiler.CompileSource(Source, "demo", ShmoBuildMode.Debuggable);
        var dbgMod = CompiledModuleCodec.Decode(debug.Bytecode);

        var run = dbgMod.Predicates.Single(
            p => FunctorName(p) == "run" && p.Arity == 1);
        Assert.NotEmpty(run.DebugStops);
        Assert.NotEmpty(run.DebugFrames);
    }

    [Fact]
    public void Codec_RoundTrips_StopSites_ReResolvingFileAndLineInThisProcess()
    {
        // Task 2: a stop's SiteId is a process-local DebugSiteTable id; the codec serializes
        // the resolved (file, line, column) and re-interns here, so the decoded SiteId points
        // at the same source location and the line is registered for breakpoint binding.
        var debug = ShmoCompiler.CompileSource(Source, "demo", ShmoBuildMode.Debuggable);
        var mod = CompiledModuleCodec.Decode(debug.Bytecode);

        var run = mod.Predicates.Single(p => FunctorName(p) == "run" && p.Arity == 1);

        // Every stop resolves to demo.pl at a real line of run/1's body (3 or 4).
        int fileId = DebugSiteTable.InternFile("demo.pl");
        foreach (var stop in run.DebugStops)
        {
            var site = DebugSiteTable.Get(stop.SiteId);
            Assert.Equal(fileId, site.FileId);
            Assert.InRange(site.Line, 2, 4);
        }

        // The body goals' lines are registered, so a line breakpoint there binds.
        Assert.NotEmpty(DebugSiteTable.SitesOnLine(fileId, 3));
    }

    [Fact]
    public void Codec_RoundTrips_ClauseFrames_WithVariableNamesAndHeadArgs()
    {
        var debug = ShmoCompiler.CompileSource(Source, "demo", ShmoBuildMode.Debuggable);
        var mod = CompiledModuleCodec.Decode(debug.Bytecode);

        var run = mod.Predicates.Single(p => FunctorName(p) == "run" && p.Arity == 1);
        var frame = run.DebugFrames.First(f => f.HasFrame);

        // run(X) :- helper(X, Y), emit(Y). — the source variables X and Y are in the frame,
        // each named, and the head skeleton survived (one arg: X).
        var names = frame.Variables.Select(v => v.Name).ToList();
        Assert.Contains("X", names);
        Assert.Contains("Y", names);
        Assert.NotNull(frame.HeadArgs);
        Assert.Single(frame.HeadArgs!);
    }

    private static string FunctorName(CompiledPredicate p)
    {
        var (atomId, _) = FunctorTable.Lookup(p.FunctorId);
        return AtomTable.GetById(atomId)!.Name;
    }
}
