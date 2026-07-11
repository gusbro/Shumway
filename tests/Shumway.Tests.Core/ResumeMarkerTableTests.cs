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
        int m1 = Activation.EncodeResumeMarker(5_000_000, 7);
        // A cursor beyond the old 4096 stride.
        int m2 = Activation.EncodeResumeMarker(5_000_000, 8_000);
        int m3 = Activation.EncodeResumeMarker(12, 3);

        Assert.True(Activation.IsResumeMarker(m1));
        Assert.True(Activation.IsResumeMarker(m2));
        Assert.True(Activation.IsResumeMarker(m3));
        Assert.Equal((5_000_000, 7), Activation.DecodeResumeMarker(m1));
        Assert.Equal((5_000_000, 8_000), Activation.DecodeResumeMarker(m2));
        Assert.Equal((12, 3), Activation.DecodeResumeMarker(m3));
    }

    [Fact]
    public void Interned_SamePairSameMarker_DistinctPairsDistinctMarkers()
    {
        int a1 = Activation.EncodeResumeMarker(777_777, 1);
        int a2 = Activation.EncodeResumeMarker(777_777, 1);
        int b = Activation.EncodeResumeMarker(777_777, 2);
        int c = Activation.EncodeResumeMarker(777_778, 1);
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
            markers[i] = Activation.EncodeResumeMarker(9_000_000 + i, i % 50);
        for (int i = 0; i < markers.Length; i++)
            Assert.Equal((9_000_000 + i, i % 50), Activation.DecodeResumeMarker(markers[i]));
    }

    [Fact]
    public void MarkersNeverCollideWithCodeAddresses()
    {
        int m = Activation.EncodeResumeMarker(1, 0);
        Assert.True(m >= Activation.ResumeMarkerBase);
        Assert.False(Activation.IsResumeMarker(Activation.ResumeMarkerBase - 1));
        Assert.False(Activation.IsResumeMarker(0));
    }
}
