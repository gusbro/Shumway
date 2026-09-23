using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>The extra trail is shared with compiled wasm modules, which bake
/// its entry layout as constants. A silent change to the struct would have a
/// module read the wrong field rather than fail to build, so the constants
/// are asserted against the struct here.
///
/// <para>If this test fails, the fix is to update <see cref="WasmAbi"/> and
/// re-emit every module, not to relax the test.</para></summary>
public sealed class ExtraTrailImageLayoutTests
{
    [Fact]
    public void TheBakedLayoutIsTheStructsLayout()
    {
        Assert.Equal(WasmAbi.ExtraTrailEntryBytes,
                     Unsafe.SizeOf<ExtraTrailEntry>());
        Assert.Equal(WasmAbi.ExtraTrailTypeOffset,
            (int)Marshal.OffsetOf<ExtraTrailEntry>(nameof(ExtraTrailEntry.Type)));
        Assert.Equal(WasmAbi.ExtraTrailHeapIdxOffset,
            (int)Marshal.OffsetOf<ExtraTrailEntry>(nameof(ExtraTrailEntry.HeapIdx)));
        Assert.Equal(WasmAbi.ExtraTrailOldValueOffset,
            (int)Marshal.OffsetOf<ExtraTrailEntry>(nameof(ExtraTrailEntry.OldValue)));
        Assert.Equal(WasmAbi.ExtraTrailMarkerOffset,
            (int)Marshal.OffsetOf<ExtraTrailEntry>(nameof(ExtraTrailEntry.BindingTrailMarker)));
    }

    /// <summary>The entry is blittable, which is what lets a world PIN the
    /// array instead of marshalling it. A managed reference anywhere inside
    /// would make the pin illegal and the image a copy of something else.
    /// </summary>
    [Fact]
    public void TheEntryIsBlittable()
    {
        var one = new ExtraTrailEntry[1];
        var pin = GCHandle.Alloc(one, GCHandleType.Pinned);
        pin.Free();
    }

    /// <summary>The type codes a module compares against are the enum's.
    /// Every one the compaction names is listed, so adding a kind without
    /// deciding what a module-side compaction does with it fails here.
    /// </summary>
    [Fact]
    public void TheTypeCodesAreWhatTheModuleBakes()
    {
        Assert.Equal(1, (int)TrailType.ValueChange);
        Assert.Equal(2, (int)TrailType.BigIntAlloc);
        Assert.Equal(3, (int)TrailType.RationalAlloc);
        Assert.Equal(16, (int)TrailType.AttrAdd);
        Assert.Equal(17, (int)TrailType.AttrModify);
        Assert.Equal(18, (int)TrailType.AttrRemove);
        Assert.Equal(32, (int)TrailType.MutableSet);
        Assert.Equal(64, (int)TrailType.CatchFrame);
        // The non-generic form: net48 has no GetValues<T>().
        Assert.Equal(8, System.Enum.GetValues(typeof(TrailType)).Length);
    }
}
