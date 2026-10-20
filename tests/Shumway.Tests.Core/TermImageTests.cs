using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>ADR-063: an image copied to the heap is the term with every index
/// relocated by the base: a structure's and a list's references, a packed
/// string header's buffer index and a float header's pair index; atoms,
/// integers, buffer cells and float pairs copy as they are.</summary>
public class TermImageTests
{
    // f([a], "xy", 1.5, 7): the functor and four argument cells, then the
    // list's cell pair, the packed string (header, buffer, tail) and the
    // float (header, pair), every index relative to the image.
    private static TermImage Image()
    {
        var cells = new Cell[12];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 4));
        cells[1] = Cell.Lis(5);
        cells[2] = Cell.Ref(7);
        cells[3] = Cell.Ref(10);
        cells[4] = Cell.Int(7);
        cells[5] = Cell.Atom(AtomTable.Intern("a", permanent: true).Id);
        cells[6] = Cell.Atom(AtomTable.EmptyListId);
        cells[7] = Cell.Pstr(2, 8, 0, TextKind.Chars, astral: false);
        cells[8] = Cell.PstrBuffer('x', 'y', 0);
        cells[9] = Cell.Atom(AtomTable.EmptyListId);
        var (header, paired) = Cell.MakeFloat(1.5, 11);
        cells[10] = header;
        cells[11] = paired;
        return new TermImage(cells, Tag.Str);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(4096)]
    public void TheCopyRelocatesEveryIndexByTheBase(int filler)
    {
        var engine = new Activation();
        if (filler > 0) engine.AllocateHeap(filler);
        var image = Image();
        Cell root = engine.BuildImage(image);
        int b = filler;
        Assert.Equal(Cell.Str(b), root);
        Assert.Equal(image.Cells[0], engine.GetHeap(b));                   // the functor
        Assert.Equal(Cell.Lis(b + 5), engine.GetHeap(b + 1));
        Assert.Equal(Cell.Ref(b + 7), engine.GetHeap(b + 2));
        Assert.Equal(Cell.Ref(b + 10), engine.GetHeap(b + 3));
        Assert.Equal(Cell.Int(7), engine.GetHeap(b + 4));
        Assert.Equal(image.Cells[5], engine.GetHeap(b + 5));               // the atom
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetHeap(b + 6));
        Cell pstr = engine.GetHeap(b + 7);
        Assert.Equal(Tag.Pstr, pstr.Tag);
        Assert.Equal(b + 8, pstr.AsPstrBufferIndex);
        Assert.Equal(2, pstr.AsPstrLength);
        Assert.Equal(image.Cells[8], engine.GetHeap(b + 8));               // the buffer cell
        Assert.Equal("xy", engine.AsPstrString(b + 7));
        Cell header = engine.GetHeap(b + 10);
        Assert.Equal(Tag.Float, header.Tag);
        Assert.Equal(b + 11, header.AsHeapIndex);
        Assert.Equal(1.5, Cell.DecodeFloat(header, engine.GetHeap(b + 11)));
        Assert.Equal(b + 12, engine.HeapTop);
    }

    [Fact]
    public void TwoCopiesOfOneImageAreTwoTerms()
    {
        var engine = new Activation();
        var image = Image();
        Cell first = engine.BuildImage(image);
        Cell second = engine.BuildImage(image);
        Assert.Equal(Cell.Str(0), first);
        Assert.Equal(Cell.Str(12), second);
        Assert.Equal(Cell.Lis(5), engine.GetHeap(1));
        Assert.Equal(Cell.Lis(17), engine.GetHeap(13));   // the second copy's list is inside the second copy
    }

    [Fact]
    public void ImagesAreEqualByContent()
    {
        Assert.Equal(Image(), Image());
        Assert.Equal(Image().GetHashCode(), Image().GetHashCode());
        var other = Image().Cells.ToArray();
        other[4] = Cell.Int(8);
        Assert.NotEqual(Image(), new TermImage(other, Tag.Str));
        Assert.NotEqual(new TermImage(new[] { Cell.Atom(1), Cell.Atom(2) }, Tag.Lis),
                        new TermImage(new[] { Cell.Atom(1), Cell.Atom(2) }, Tag.Str));
    }
}
