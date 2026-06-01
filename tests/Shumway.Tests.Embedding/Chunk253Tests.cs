using Shumway.Repl;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 253: REPL line-editor horizontal scroll. Tests the
/// pure window-computation helper that drives Redraw — the
/// interactive painting itself is exercised by manual smoke
/// testing.
/// </summary>
public class Chunk253Tests
{
    [Fact]
    public void BufferFits_WindowCoversAll()
    {
        // Buffer fits in the visible columns → no scrolling, whole
        // buffer is the window.
        var (start, end) = LineEditor.ComputeVisibleWindow(
            bufferLength: 30, cursor: 15, visibleCols: 80);
        Assert.Equal(0, start);
        Assert.Equal(30, end);
    }

    [Fact]
    public void EmptyBuffer_WindowIsEmpty()
    {
        var (start, end) = LineEditor.ComputeVisibleWindow(0, 0, 80);
        Assert.Equal(0, start);
        Assert.Equal(0, end);
    }

    [Fact]
    public void LongBuffer_CursorNearStart_AnchorsAtZero()
    {
        // Cursor at column 10 of a 200-char buffer in an 80-col
        // window → window stays anchored at 0.
        var (start, end) = LineEditor.ComputeVisibleWindow(
            bufferLength: 200, cursor: 10, visibleCols: 80);
        Assert.Equal(0, start);
        Assert.Equal(80, end);
    }

    [Fact]
    public void LongBuffer_CursorAtRightEdgeOfWindow_StaysAnchored()
    {
        // Cursor exactly at visibleCols - 1 → still anchored at 0.
        var (start, end) = LineEditor.ComputeVisibleWindow(
            bufferLength: 200, cursor: 79, visibleCols: 80);
        Assert.Equal(0, start);
        Assert.Equal(80, end);
    }

    [Fact]
    public void LongBuffer_CursorPastWindow_SlidesRight()
    {
        // Cursor at column 100 of 200-char buffer → window slides
        // so the cursor is the last column visible.
        var (start, end) = LineEditor.ComputeVisibleWindow(
            bufferLength: 200, cursor: 100, visibleCols: 80);
        Assert.Equal(21, start);   // 101 - 80
        Assert.Equal(101, end);    // cursor + 1
        Assert.Equal(80, end - start);
    }

    [Fact]
    public void LongBuffer_CursorAtEnd_WindowEndsAtBuffer()
    {
        var (start, end) = LineEditor.ComputeVisibleWindow(
            bufferLength: 200, cursor: 200, visibleCols: 80);
        Assert.Equal(120, start);
        Assert.Equal(200, end);
    }

    [Fact]
    public void CursorAlwaysVisible_PropertyTest()
    {
        // For every combination of buffer length and cursor
        // position, the cursor must be inside (or at the right
        // edge of) the returned window — otherwise the user
        // can't see what they're typing.
        for (int buf = 0; buf <= 300; buf += 17)
            for (int cur = 0; cur <= buf; cur += 7)
                foreach (int cols in new[] { 1, 5, 40, 80, 200 })
                {
                    var (s, e) = LineEditor.ComputeVisibleWindow(buf, cur, cols);
                    Assert.True(s >= 0,
                        $"start < 0 for (buf={buf}, cur={cur}, cols={cols})");
                    Assert.True(e <= buf,
                        $"end > buf for (buf={buf}, cur={cur}, cols={cols})");
                    Assert.True(cur >= s && cur <= e,
                        $"cursor {cur} not in window [{s},{e}] for "
                        + $"(buf={buf}, cols={cols})");
                    Assert.True(e - s <= cols,
                        $"window width {e - s} > visibleCols {cols} for "
                        + $"(buf={buf}, cur={cur})");
                }
    }
}
