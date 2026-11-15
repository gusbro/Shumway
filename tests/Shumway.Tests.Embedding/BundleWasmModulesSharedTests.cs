using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Engines that each read the same bundle hold one copy of its wasm
/// module: a page with several sessions reads megabytes of them per
/// engine.</summary>
public class BundleWasmModulesSharedTests
{
    private static byte[] BundleWith(byte[] module)
        => BundleWriter.ToBytes(new Bundle(
            new[] { new BundleEntry("shared_wasm_m", "shared_wasm_p(1).\n") },
            foreignAssemblies: null, snapshot: null, wasmModules: new[] { module }));

    private static byte[] Module(byte seed)
    {
        var module = new byte[4096];
        for (int i = 0; i < module.Length; i++) module[i] = (byte)(seed + i * 7);
        return module;
    }

    [Fact]
    public void TwoReadsOfABundle_HoldOneCopyOfItsWasmModule()
    {
        byte[] bytes = BundleWith(Module(1));
        Bundle a = BundleReader.FromBytes(bytes);
        Bundle b = BundleReader.FromBytes(bytes);

        Assert.NotSame(a, b);
        Assert.Single(a.WasmModules);
        Assert.Equal(Module(1), a.WasmModules[0]);
        Assert.Same(a.WasmModules[0], b.WasmModules[0]);
    }

    [Fact]
    public void BundlesWithDifferentModules_KeepTheirOwn()
    {
        Bundle a = BundleReader.FromBytes(BundleWith(Module(1)));
        Bundle b = BundleReader.FromBytes(BundleWith(Module(2)));

        Assert.NotSame(a.WasmModules[0], b.WasmModules[0]);
        Assert.Equal(Module(1), a.WasmModules[0]);
        Assert.Equal(Module(2), b.WasmModules[0]);
    }
}
