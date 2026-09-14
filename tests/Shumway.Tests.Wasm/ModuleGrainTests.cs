using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>How much of a module is its own code, and how much is the fixed
/// furniture every module carries: the dispatcher, the fail/proceed resolver
/// and the general unifier.
///
/// <para>G2 measured 818 separate modules at 1.53x the bytes of one group, and
/// the browser compiles all of it at load. That ratio is entirely this
/// furniture, repeated. The number below decides the grain of the split: if
/// the fixed part dominates a one-predicate module, the answer is a few
/// predicates per module rather than one each.</para></summary>
public sealed class ModuleGrainTests(ITestOutputHelper o)
{
    [Fact]
    public void TheFixedCostOfAModuleIsMeasured()
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
            catch (WasmCompileException) { continue; }
            members.Add(m);
        }
        Assert.True(members.Count > 500);

        // Sizes of single-predicate modules, and their WAM instruction counts.
        var singles = new List<(int Bytes, int Code)>(members.Count);
        foreach (var m in members)
        {
            var one = WasmPredicateCompiler.CompileGroup(new[] { m }, env);
            singles.Add((one.Module.Length, m.Predicate.Bytecode.Length));
        }
        singles.Sort((x, y) => x.Code.CompareTo(y.Code));

        // The smallest predicates are almost all furniture: their own code
        // contributes little, so their module size approximates the fixed cost.
        int floor = singles[0].Bytes;
        int median = singles[singles.Count / 2].Bytes;
        int largest = singles[^1].Bytes;

        // Grouping N at a time pays the furniture once per group.
        long TotalAt(int perModule)
        {
            long total = 0;
            for (int i = 0; i < members.Count; i += perModule)
            {
                var slice = members.GetRange(i, Math.Min(perModule, members.Count - i));
                total += WasmPredicateCompiler.CompileGroup(slice, env).Module.Length;
            }
            return total;
        }
        long all = WasmPredicateCompiler.CompileGroup(members, env).Module.Length;
        long at1 = 0;
        foreach (var s in singles) at1 += s.Bytes;

        o.WriteLine($"{members.Count} predicates");
        o.WriteLine($"  smallest module : {floor:N0} bytes (its predicate is "
            + $"{singles[0].Code} bytes of WAM)");
        o.WriteLine($"  median module   : {median:N0} bytes");
        o.WriteLine($"  largest module  : {largest:N0} bytes");
        o.WriteLine($"  one group       : {all:N0} bytes");
        foreach (int per in new[] { 1, 4, 16, 64 })
        {
            long t = per == 1 ? at1 : TotalAt(per);
            o.WriteLine($"  {per,3} per module   : {t:N0} bytes  ({(double)t / all:F2}x)");
        }

        // ANTI-VACUITY: a floor of zero would mean the measurement found
        // nothing to measure.
        Assert.True(floor > 0);
        Assert.True(all > 0);
    }
}
