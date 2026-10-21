using System.Reflection;
using System.Reflection.Emit;
using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The JIT's limits as <see cref="IlJitLimits"/> records them, each
/// probed against the JIT of the running runtime: a method a little under
/// the limit is optimized, one a little past it is compiled without
/// optimization. A runtime whose JIT moved a limit fails here, and the
/// constant is reviewed (<c>docs/design/region-jit-limits.md</c>).
/// Exclusive: the JIT's events reach the listener late under the load of
/// other classes.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class JitLimitCanaryTests
{
#if !NETFRAMEWORK
    // Past a limit the JIT says MinOptJitted; under it, Optimized. The
    // margin covers what a dynamic method brings of its own.
    private const int Margin = 16;

    // A built method with AggressiveOptimization, as a region method is: a
    // dynamic method without the flag is compiled without optimization
    // whatever its size.
    private static readonly ModuleBuilder Probes = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("ShumwayJitProbes"), AssemblyBuilderAccess.RunAndCollect)
        .DefineDynamicModule("probes");

    private static bool MinOpts(string name, int locals, Action<ILGenerator> body)
    {
        using var jit = new JitTiers();
        var type = Probes.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var mb = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        mb.SetImplementationFlags(MethodImplAttributes.AggressiveOptimization);
        var il = mb.GetILGenerator();
        for (int i = 0; i < locals; i++) il.DeclareLocal(typeof(int));
        body(il);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        var built = type.CreateType()!;
        var f = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), built.GetMethod(name)!);
        Assert.Equal(0, f());
        var tiers = jit.WaitFor(name);
        Assert.True(tiers.TryGetValue(name, out var tier), $"the JIT reported no {name} ({jit.Events} events [{jit.Seen}])");
        return tier!.Contains("MinOpt", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-Margin, false)]
    [InlineData(+Margin, true)]
    public void BasicBlocks(int delta, bool minOpts)
    {
        int blocks = IlJitLimits.BasicBlocks + delta;
        // The entry block, then two per unit: the one the branch skips and
        // the one it lands on. A branch to the next instruction is no
        // boundary to the JIT, so the skipped instruction stays.
        Assert.Equal(minOpts, MinOpts($"ShumwayJitProbe_blocks_{blocks}", 0, il =>
        {
            for (int i = 0; i < (blocks - 1) / 2; i++)
            {
                var l = il.DefineLabel();
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Brfalse_S, l);
                il.Emit(OpCodes.Nop);
                il.MarkLabel(l);
            }
        }));
    }

    [Theory]
    [InlineData(-Margin, false)]
    [InlineData(+Margin, true)]
    public void IlBytes(int delta, bool minOpts)
    {
        int bytes = IlJitLimits.IlBytes + delta;
        // Ten bytes in two instructions (a long constant and its pop), so
        // the instruction limit stays far; the return at the end takes two.
        Assert.Equal(minOpts, MinOpts($"ShumwayJitProbe_bytes_{bytes}", 0, il =>
        {
            int body = bytes - 2;
            for (int i = 0; i < body / 10; i++)
            {
                il.Emit(OpCodes.Ldc_I8, 0L);
                il.Emit(OpCodes.Pop);
            }
            for (int i = 0; i < body % 10; i++) il.Emit(OpCodes.Nop);
        }));
    }

    [Theory]
    [InlineData(-Margin, false)]
    [InlineData(+Margin, true)]
    public void Instructions(int delta, bool minOpts)
    {
        int instructions = IlJitLimits.Instructions + delta;
        Assert.Equal(minOpts, MinOpts($"ShumwayJitProbe_instrs_{instructions}", 0, il =>
        {
            for (int i = 0; i < instructions - 2; i++) il.Emit(OpCodes.Nop);
        }));
    }

    [Theory]
    [InlineData(-Margin, false)]
    [InlineData(+Margin, true)]
    public void Locals(int delta, bool minOpts)
    {
        int locals = IlJitLimits.Locals + delta;
        Assert.Equal(minOpts, MinOpts($"ShumwayJitProbe_locals_{locals}", locals, _ => { }));
    }

    [Theory]
    [InlineData(-Margin, false)]
    [InlineData(+Margin, true)]
    public void LocalRefs(int delta, bool minOpts)
    {
        int refs = IlJitLimits.LocalRefs + delta;
        // A load and a store of one local, two references a pair.
        Assert.Equal(minOpts, MinOpts($"ShumwayJitProbe_refs_{refs}", 1, il =>
        {
            for (int i = 0; i < refs / 2; i++)
            {
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stloc_0);
            }
        }));
    }
#endif
}
