using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>G2 of the many-modules arc: what compiling n predicates as n
/// modules costs against compiling them as one group.
///
/// <para>This is the gate that says a real JIT is possible rather than merely
/// fast. Today promotion rebuilds the whole group, so promoting n predicates
/// one at a time costs n(n+1)/2 predicate compiles -- measured on boards.pl as
/// 136 group builds against 3 in batch. Separate modules make that n. But each
/// module carries its own fixed cost, and if that dominates, the honest answer
/// is a handful of modules rather than one per predicate.</para>
///
/// <para>The gate counts bytes, not milliseconds: the fixed cost of a module
/// is code the emitter writes once per module (dispatcher, resolver, cursor
/// table, imports), so it shows in the size of n modules against one, and
/// that number is the same on every run. Wall-clock is printed for the eye
/// only. Instantiation and per-thread registration are the browser's, and
/// the browser probe measures those.</para></summary>
public sealed class IncrementalCompileCostTests(ITestOutputHelper o)
{
    [Fact]
    public void CompilingOneAtATimeIsNotMuchWorseThanOneBatch()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.Query("true.");
        var env = new EngineWasmCompileEnv();

        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var m = new WasmGroupMember(pred, addr, null);
            try { WasmPredicateCompiler.CompileGroup(new[] { m }, env); }
            catch (WasmCompileException) { continue; }   // the backend refuses it
            members.Add(m);
        }
        Assert.True(members.Count > 500, $"only {members.Count} compilable");

        // Warm: first touch pays for JIT and caches on both paths alike.
        _ = WasmPredicateCompiler.CompileGroup(members, env);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batch = WasmPredicateCompiler.CompileGroup(members, env);
        sw.Stop();
        double batchMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        long separateBytes = 0;
        var each = new List<double>(members.Count);
        foreach (var m in members)
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            var entry = WasmPredicateCompiler.CompileGroup(new[] { m }, env);
            one.Stop();
            each.Add(one.Elapsed.TotalMilliseconds);
            separateBytes += entry.Module.Length;
        }
        sw.Stop();
        double separateMs = sw.Elapsed.TotalMilliseconds;

        each.Sort();
        double median = each[each.Count / 2];
        o.WriteLine($"{members.Count} predicates");
        o.WriteLine($"  one batch      : {batchMs:F0} ms, {batch.Module.Length:N0} bytes");
        o.WriteLine($"  one at a time  : {separateMs:F0} ms, {separateBytes:N0} bytes total");
        o.WriteLine($"  ratio          : {separateMs / batchMs:F2}x time, "
            + $"{(double)separateBytes / batch.Module.Length:F2}x bytes");
        o.WriteLine($"  per predicate  : median {median:F2} ms, max {each[^1]:F2} ms");

        // The gate. What would sink the arc is a large FIXED cost per module,
        // and that shows up as a byte ratio in the tens. Measured 1.67x
        // (about 3.6 KB per module over the shared code of the batch).
        double bytesRatio = (double)separateBytes / batch.Module.Length;
        double fixedBytesPerModule = (separateBytes - batch.Module.Length) / (double)members.Count;
        Assert.True(bytesRatio <= 2.0,
            $"compiling one at a time emitted {bytesRatio:F2}x the batch's bytes "
            + $"({separateBytes:N0} against {batch.Module.Length:N0}). Above 2x the arc's "
            + "answer may be a handful of modules rather than one per predicate.");
        Assert.True(fixedBytesPerModule <= 8 * 1024,
            $"{fixedBytesPerModule:F0} bytes of fixed cost per module: a promotion has "
            + "to be cheap enough to happen mid-session without being felt.");
    }
}
