using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The resume table's own behaviour, before anything reads it from
/// wasm. Small enough to state exhaustively, and worth stating: everything
/// later in the arc trusts these rows.</summary>
public sealed class ResumeTableTests
{
    [Fact]
    public void AMarkerRoundTrips()
    {
        var t = new WasmResumeTable(16);
        int m = Activation.EncodeResumeMarker(4242, 0x1000);
        t.Set(m, moduleId: 3, cursor: 77);
        Assert.True(t.TryGet(m, out int mod, out int cur));
        Assert.Equal(3, mod);
        Assert.Equal(77, cur);
    }

    /// <summary>Cursor 0 is a real cursor — the first leader of a module — so
    /// "absent" cannot be encoded as a zero cursor.</summary>
    [Fact]
    public void CursorZeroIsNotMistakenForAbsent()
    {
        var t = new WasmResumeTable(16);
        int m = Activation.EncodeResumeMarker(4243, 0x2000);
        t.Set(m, moduleId: 0, cursor: 0);
        Assert.True(t.TryGet(m, out int mod, out int cur));
        Assert.Equal(0, mod);
        Assert.Equal(0, cur);
    }

    [Fact]
    public void AnUnsetMarkerDoesNotResolve()
    {
        var t = new WasmResumeTable(16);
        Assert.False(t.TryGet(Activation.EncodeResumeMarker(4244, 0x3000), out _, out _));
    }

    /// <summary>A marker past the table is newer than it: the safe answer is
    /// "not here", never an out-of-range read.</summary>
    [Fact]
    public void AMarkerPastTheTableDoesNotResolve()
    {
        var t = new WasmResumeTable(4);
        Assert.False(t.TryGet(Activation.ResumeMarkerBase + 1_000_000, out _, out _));
    }

    [Fact]
    public void SettingPastTheEndGrowsTheTable()
    {
        var t = new WasmResumeTable(4);
        int before = t.Length;
        // Reach for a marker well past the initial rows.
        int m = Activation.ResumeMarkerBase + 500;
        t.Set(m, moduleId: 1, cursor: 5);
        Assert.True(t.Length > before);
        Assert.True(t.TryGet(m, out int mod, out int cur));
        Assert.Equal(1, mod);
        Assert.Equal(5, cur);
    }

    /// <summary>Eviction forgets one module's rows and only that module's.
    /// </summary>
    [Fact]
    public void ClearingOneModuleLeavesTheOthers()
    {
        var t = new WasmResumeTable(64);
        int mine = Activation.EncodeResumeMarker(4245, 0x4000);
        int theirs = Activation.EncodeResumeMarker(4246, 0x5000);
        t.Set(mine, moduleId: 2, cursor: 11);
        t.Set(theirs, moduleId: 7, cursor: 22);

        t.ClearModule(2);
        Assert.False(t.TryGet(mine, out _, out _));
        Assert.True(t.TryGet(theirs, out int mod, out int cur));
        Assert.Equal(7, mod);
        Assert.Equal(22, cur);
    }

    /// <summary>Module 0 is a real module, and its rows must not read as
    /// absent — the reason a row stores moduleId + 1.</summary>
    [Fact]
    public void ModuleZeroIsARealModule()
    {
        var t = new WasmResumeTable(16);
        int m = Activation.EncodeResumeMarker(4247, 0x6000);
        t.Set(m, moduleId: 0, cursor: 9);
        Assert.True(t.TryGet(m, out int mod, out int cur));
        Assert.Equal(0, mod);
        Assert.Equal(9, cur);
        t.ClearModule(0);
        Assert.False(t.TryGet(m, out _, out _));
    }
}
