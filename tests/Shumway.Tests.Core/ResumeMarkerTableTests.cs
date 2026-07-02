using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// Phase 33 B1 — resume markers are dense side-table ids, not the old
/// arithmetic encoding (Base + fid*4096 + cursor) whose functor-id cap
/// (~262 143) was a proven live ceiling for large programs / long-running
/// processes.
/// </summary>
public class ResumeMarkerTableTests
{
    [Fact]
    public void RoundTrips_BeyondTheOldArithmeticCaps()
    {
        // A functor id far beyond the old ~262143 cap.
        int m1 = Engine.EncodeResumeMarker(5_000_000, 7);
        // A cursor beyond the old 4096 stride.
        int m2 = Engine.EncodeResumeMarker(5_000_000, 8_000);
        int m3 = Engine.EncodeResumeMarker(12, 3);

        Assert.True(Engine.IsResumeMarker(m1));
        Assert.True(Engine.IsResumeMarker(m2));
        Assert.True(Engine.IsResumeMarker(m3));
        Assert.Equal((5_000_000, 7), Engine.DecodeResumeMarker(m1));
        Assert.Equal((5_000_000, 8_000), Engine.DecodeResumeMarker(m2));
        Assert.Equal((12, 3), Engine.DecodeResumeMarker(m3));
    }

    [Fact]
    public void Interned_SamePairSameMarker_DistinctPairsDistinctMarkers()
    {
        int a1 = Engine.EncodeResumeMarker(777_777, 1);
        int a2 = Engine.EncodeResumeMarker(777_777, 1);
        int b = Engine.EncodeResumeMarker(777_777, 2);
        int c = Engine.EncodeResumeMarker(777_778, 1);
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.NotEqual(a1, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void ManyMarkers_GrowTheTable_AndStayStable()
    {
        // Force several table growths (initial capacity 4096) and verify every
        // marker still decodes to its pair afterwards.
        var markers = new int[10_000];
        for (int i = 0; i < markers.Length; i++)
            markers[i] = Engine.EncodeResumeMarker(9_000_000 + i, i % 50);
        for (int i = 0; i < markers.Length; i++)
            Assert.Equal((9_000_000 + i, i % 50), Engine.DecodeResumeMarker(markers[i]));
    }

    [Fact]
    public void MarkersNeverCollideWithCodeAddresses()
    {
        int m = Engine.EncodeResumeMarker(1, 0);
        Assert.True(m >= Engine.ResumeMarkerBase);
        Assert.False(Engine.IsResumeMarker(Engine.ResumeMarkerBase - 1));
        Assert.False(Engine.IsResumeMarker(0));
    }
}
