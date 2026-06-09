using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// Chunk 372 (Phase 29, region compilation — Stage 3 foundation): the
/// <see cref="Engine.RegionReturnCursor"/> Cp-decode used by a region method's
/// <c>ret</c> handler. At a member's proceed it decides intra-region return (Cp is
/// a resume marker into this region → continue at the cursor via an intra-method
/// <c>br</c>) vs cross-region return (anything else → return to the dispatch loop,
/// which runs Cp).
/// </summary>
public class Chunk372Tests
{
    [Fact]
    public void Marker_IntoThisRegion_ReturnsCursor()
    {
        var e = new Engine();
        e.SetCp(Engine.EncodeResumeMarker(7, 3));
        Assert.Equal(3, e.RegionReturnCursor(7));
    }

    [Fact]
    public void Marker_IntoAnotherFunctor_IsCrossRegion()
    {
        var e = new Engine();
        e.SetCp(Engine.EncodeResumeMarker(9, 2));
        Assert.Equal(-1, e.RegionReturnCursor(7));   // different region root
    }

    [Fact]
    public void NonMarkerCp_IsCrossRegion()
    {
        var e = new Engine();
        e.SetCp(42);   // an ordinary bytecode address, not a resume marker
        Assert.Equal(-1, e.RegionReturnCursor(7));
    }

    [Fact]
    public void Cursor0_IntoThisRegion_ReturnsZero()
    {
        var e = new Engine();
        e.SetCp(Engine.EncodeResumeMarker(7, 0));
        Assert.Equal(0, e.RegionReturnCursor(7));    // 0 is a valid return cursor, not "cross"
    }
}
