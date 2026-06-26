using Shumway.Repl;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// REPL line-editor wrapping. Chunk 253 originally kept a long line
/// on one terminal row via a horizontal-scroll window
/// (<c>ComputeVisibleWindow</c>); Phase 31 replaced that with real
/// multi-row wrapping driven by <see cref="LineEditor.CellRowCol"/>.
/// These tests pin the pure layout helper — the interactive painting
/// itself is exercised by manual smoke testing.
/// </summary>
public class Chunk253Tests
{
    [Fact]
    public void FirstRow_IndexMapsToColumnDirectly()
    {
        var (row, col) = LineEditor.CellRowCol(15, 80);
        Assert.Equal(0, row);
        Assert.Equal(15, col);
    }

    [Fact]
    public void ExactWidth_WrapsToStartOfNextRow()
    {
        // Cell at linear index == width is the first cell of row 1.
        var (row, col) = LineEditor.CellRowCol(80, 80);
        Assert.Equal(1, row);
        Assert.Equal(0, col);
    }

    [Fact]
    public void PastSeveralRows_ComputesRowAndColumn()
    {
        // 200 cells in an 80-col terminal → row 2, col 40.
        var (row, col) = LineEditor.CellRowCol(200, 80);
        Assert.Equal(2, row);
        Assert.Equal(40, col);
    }

    [Fact]
    public void LastColumnOfARow_StaysOnThatRow()
    {
        var (row, col) = LineEditor.CellRowCol(79, 80);
        Assert.Equal(0, row);
        Assert.Equal(79, col);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]   // negative index is clamped, never throws / negative
    [InlineData(80)]
    public void WidthAndIndexGuards_NeverThrowAndStayInRange(int width)
    {
        // width < 1 is coerced to 1; negative index coerced to 0. The
        // returned column must always be a valid 0..width-1 coordinate.
        int w = width < 1 ? 1 : width;
        for (int idx = -3; idx <= 250; idx += 13)
        {
            var (row, col) = LineEditor.CellRowCol(idx, width);
            Assert.True(row >= 0, $"row<0 for idx={idx}, width={width}");
            Assert.True(col >= 0 && col < w,
                $"col {col} out of [0,{w}) for idx={idx}, width={width}");
        }
    }

    [Fact]
    public void RowMajorOrder_ConsecutiveIndicesAdvanceColumnThenRow()
    {
        // Walking linear indices 0..2W-1 must visit row 0 cols 0..W-1
        // then row 1 cols 0..W-1 — the invariant the renderer relies on.
        const int w = 10;
        for (int i = 0; i < 2 * w; i++)
        {
            var (row, col) = LineEditor.CellRowCol(i, w);
            Assert.Equal(i / w, row);
            Assert.Equal(i % w, col);
        }
    }
}
