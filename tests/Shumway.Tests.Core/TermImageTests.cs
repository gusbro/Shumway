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

    // f(X, Y, Z, W): X comes from A1, Y from Y1 of the frame, Z goes to A2,
    // W to Y0; each variable is a cell the image holds unbound.
    private static TermImage Holed()
    {
        var cells = new Cell[5];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 4));
        for (int i = 1; i <= 4; i++) cells[i] = Cell.Ref(i);
        return new TermImage(cells, Tag.Str, new[]
        {
            new ImageHole(1, ImageHole.In, 1),
            new ImageHole(2, ImageHole.In | ImageHole.Y, 1),
            new ImageHole(3, ImageHole.Out, 2),
            new ImageHole(4, ImageHole.Out | ImageHole.Y, 0),
        });
    }

    [Fact]
    public void TheHolesTakeAndGiveRegisters()
    {
        var engine = new Activation();
        engine.Allocate(2);
        engine.AllocateHeap(10);
        int older = engine.AllocateHeapUnbound();
        engine.SetRegister(1, Cell.Ref(older));
        engine.SetY(1, Cell.Int(42));
        int b = engine.HeapTop;
        Cell root = engine.BuildImage(Holed());
        Assert.Equal(Cell.Str(b), root);
        Assert.Equal(Cell.Ref(older), engine.GetHeap(b + 1));      // in, from the register
        Assert.Equal(Cell.Int(42), engine.GetHeap(b + 2));         // in, from the frame
        Assert.Equal(Cell.Ref(b + 3), engine.GetHeap(b + 3));      // unbound, and the register at it
        Assert.Equal(Cell.Ref(b + 3), engine.GetRegister(2));
        Assert.Equal(Cell.Ref(b + 4), engine.GetHeap(b + 4));
        Assert.Equal(Cell.Ref(b + 4), engine.GetY(0));
        Assert.Equal(b + 5, engine.HeapTop);
    }

    [Fact]
    public void GetBlitBindsAnUnboundArgumentAndLeavesABoundOneToTheStream()
    {
        var engine = new Activation();
        engine.Allocate(2);
        engine.AllocateHeap(10);
        int older = engine.AllocateHeapUnbound();
        engine.SetRegister(1, Cell.Ref(older));
        engine.SetY(1, Cell.Int(42));
        int v = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(v));
        int b = engine.HeapTop;
        Assert.True(engine.GetBlit(Holed(), 0));
        Assert.Equal(Cell.Str(b), engine.GetHeap(v));            // the variable, bound to the copy
        Assert.Equal(Cell.Ref(older), engine.GetHeap(b + 1));    // the holes, as in a put
        Assert.Equal(Cell.Ref(b + 3), engine.GetRegister(2));
        Assert.Equal(Cell.Ref(b + 4), engine.GetY(0));
        // Bound now: the stream's turn, and nothing is allocated.
        int top = engine.HeapTop;
        Assert.False(engine.GetBlit(Holed(), 0));
        Assert.Equal(top, engine.HeapTop);
        // Not a variable at all: the stream's turn too (it will fail).
        engine.SetRegister(3, Cell.Int(7));
        Assert.False(engine.GetBlit(Holed(), 3));
        Assert.Equal(top, engine.HeapTop);
    }

    [Fact]
    public void TheCopyMarksTheDeferredTemporaries()
    {
        // g(X) with X's cell marked for the temporary X5 a deferred part would use.
        var engine = new Activation();
        var cells = new Cell[2];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 1));
        cells[1] = Cell.Ref(1);
        var image = new TermImage(cells, Tag.Str, new[] { new ImageHole(0, ImageHole.Sentinel, 5) });
        engine.SetRegister(5, Cell.Int(0));
        int v = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(v));
        Assert.True(engine.GetBlit(image, 0));
        Assert.True(Activation.IsCopiedMark(engine.GetRegister(5)));
        // The stream's turn leaves the temporaries alone.
        engine.SetRegister(5, Cell.Int(0));
        Assert.False(engine.GetBlit(image, 0));
        Assert.False(Activation.IsCopiedMark(engine.GetRegister(5)));
    }

    [Fact]
    public void AnAttributedVariableComesInAsAReferenceToItsHome()
    {
        // As unify_value writes one: the attribute table keys on the cell's
        // own address, and a copy would claim attributes it does not have.
        var engine = new Activation();
        int home = engine.AllocateHeapUnbound();
        engine.SetRegister(1, Cell.AttVar(home));
        var cells = new Cell[2];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 1));
        cells[1] = Cell.Ref(1);
        int b = engine.HeapTop;
        engine.BuildImage(new TermImage(cells, Tag.Str, new[] { new ImageHole(1, ImageHole.In, 1) }));
        Assert.Equal(Cell.Ref(home), engine.GetHeap(b + 1));
    }

    // f(a, [1, 2], X, g(X, Y)) as the builder lays it: g's block (pushed last,
    // popped first) before the list's; X a hole for X2, Y one for X3.
    private static TermImage Nested()
    {
        var cells = new Cell[12];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 4));
        cells[1] = Cell.Atom(AtomTable.Intern("a", permanent: true).Id);
        cells[2] = Cell.Lis(8);
        cells[3] = Cell.Ref(3);
        cells[4] = Cell.Str(5);
        cells[5] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 2));
        cells[6] = Cell.Ref(3);
        cells[7] = Cell.Ref(7);
        cells[8] = Cell.Int(1);
        cells[9] = Cell.Lis(10);
        cells[10] = Cell.Int(2);
        cells[11] = Cell.Atom(AtomTable.EmptyListId);
        return new TermImage(cells, Tag.Str, new[] { new ImageHole(3, ImageHole.Out, 2), new ImageHole(7, ImageHole.Out, 3) });
    }

    // f(a, [1, 2], V, g(V, W)) on the heap, V and W unbound: the functor cell's
    // index, g's, the list's two pairs'.
    private static (int T, int G, int L1, int L2) NestedTerm(Activation engine)
    {
        int t = engine.AllocateHeap(5);
        int g = engine.AllocateHeap(3);
        int l1 = engine.AllocateHeap(2);
        int l2 = engine.AllocateHeap(2);
        engine.SetHeap(t, Cell.Functor(FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 4)));
        engine.SetHeap(t + 1, Cell.Atom(AtomTable.Intern("a", permanent: true).Id));
        engine.SetHeap(t + 2, Cell.Lis(l1));
        engine.SetHeap(t + 3, Cell.Ref(t + 3));
        engine.SetHeap(t + 4, Cell.Str(g));
        engine.SetHeap(g, Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 2)));
        engine.SetHeap(g + 1, Cell.Ref(t + 3));
        engine.SetHeap(g + 2, Cell.Ref(g + 2));
        engine.SetHeap(l1, Cell.Int(1));
        engine.SetHeap(l1 + 1, Cell.Lis(l2));
        engine.SetHeap(l2, Cell.Int(2));
        engine.SetHeap(l2 + 1, Cell.Atom(AtomTable.EmptyListId));
        return (t, g, l1, l2);
    }

    [Fact]
    public void TheBlocksEndWhereTheirSubTermsDo()
    {
        var image = Nested();
        Assert.Equal(12, image.BlockEnd(0));
        Assert.Equal(8, image.BlockEnd(5));     // g(X, Y)
        Assert.Equal(12, image.BlockEnd(8));    // [1, 2]
        Assert.Equal(12, image.BlockEnd(10));   // [2]
        var literals = Image();                 // f([a], "xy", 1.5, 7): the literals are the root's
        Assert.Equal(12, literals.BlockEnd(0));
        Assert.Equal(7, literals.BlockEnd(5));
    }

    [Fact]
    public void UnifyBlitMatchesABoundPartCellByCell()
    {
        var engine = new Activation();
        var (t, g, _, _) = NestedTerm(engine);
        engine.SetRegister(0, Cell.Str(t));
        int top = engine.HeapTop;
        Assert.True(engine.UnifyBlit(Nested(), 0));
        Assert.Equal(top, engine.HeapTop);                      // nothing built
        Assert.Equal(Cell.Ref(t + 3), engine.GetRegister(2));   // X, from V's cell
        Assert.Equal(Cell.Ref(g + 2), engine.GetRegister(3));   // Y, from W's cell
        // A bound cell comes into the register as it is; a different atom fails.
        engine.SetHeap(g + 2, Cell.Int(5));
        Assert.True(engine.UnifyBlit(Nested(), 0));
        Assert.Equal(Cell.Int(5), engine.GetRegister(3));
        engine.SetHeap(t + 1, Cell.Atom(AtomTable.Intern("b", permanent: true).Id));
        Assert.False(engine.UnifyBlit(Nested(), 0));
    }

    [Fact]
    public void UnifyBlitCopiesAnUnboundSubTerm()
    {
        var engine = new Activation();
        var (t, _, _, _) = NestedTerm(engine);
        engine.SetHeap(t + 2, Cell.Ref(t + 2));                 // the list's place, unbound
        engine.SetRegister(0, Cell.Str(t));
        int top = engine.HeapTop;
        Assert.True(engine.UnifyBlit(Nested(), 0));
        Assert.Equal(Cell.Lis(top), engine.GetHeap(t + 2));
        Assert.Equal(Cell.Int(1), engine.GetHeap(top));
        Assert.Equal(Cell.Lis(top + 2), engine.GetHeap(top + 1));
        Assert.Equal(Cell.Int(2), engine.GetHeap(top + 2));
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetHeap(top + 3));
        Assert.Equal(top + 4, engine.HeapTop);
    }

    [Fact]
    public void AMatchMarksTheDeferredTemporariesAsACopyDoes()
    {
        // g(1) with the mark for X5, the temporary of a part nested under it:
        // matched against g(1) on the heap, X5 holds the mark afterwards.
        var engine = new Activation();
        var cells = new Cell[2];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 1));
        cells[1] = Cell.Int(1);
        var image = new TermImage(cells, Tag.Str, new[] { new ImageHole(0, ImageHole.Sentinel, 5) });
        int t = engine.AllocateHeap(2);
        engine.SetHeap(t, cells[0]);
        engine.SetHeap(t + 1, Cell.Int(1));
        engine.SetRegister(0, Cell.Str(t));
        engine.SetRegister(5, Cell.Int(0));
        Assert.True(engine.UnifyBlit(image, 0));
        Assert.True(Activation.IsCopiedMark(engine.GetRegister(5)));
        // A mismatch leaves the temporaries alone.
        engine.SetRegister(5, Cell.Int(0));
        engine.SetHeap(t + 1, Cell.Int(2));
        Assert.False(engine.UnifyBlit(image, 0));
        Assert.False(Activation.IsCopiedMark(engine.GetRegister(5)));
    }

    [Fact]
    public void UnifyBlitTakesTheMarkAsTheCopy()
    {
        var engine = new Activation();
        engine.SetRegister(0, Activation.CopiedMark);
        int top = engine.HeapTop;
        Assert.True(engine.UnifyBlit(Nested(), 0));
        Assert.Equal(top, engine.HeapTop);
    }

    [Fact]
    public void UnifyBlitBindsAnUnboundPartToTheWholeImage()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(v));
        int top = engine.HeapTop;
        Assert.True(engine.UnifyBlit(Nested(), 0));
        Assert.Equal(Cell.Str(top), engine.GetHeap(v));
        Assert.Equal(Cell.Ref(top + 3), engine.GetRegister(2));
        Assert.Equal(Cell.Ref(top + 7), engine.GetRegister(3));
        Assert.Equal(top + 12, engine.HeapTop);
    }

    [Fact]
    public void AVariablesFirstCellIsMetBeforeItsOthers()
    {
        // f(g(X), h(X)): h's block is laid first, so X's first cell is in it
        // and g's cell refers back; the matcher takes the blocks in that order.
        var cells = new Cell[7];
        cells[0] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 2));
        cells[1] = Cell.Str(5);
        cells[2] = Cell.Str(3);
        cells[3] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("h", permanent: true).Id, 1));
        cells[4] = Cell.Ref(4);
        cells[5] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern("g", permanent: true).Id, 1));
        cells[6] = Cell.Ref(4);
        var image = new TermImage(cells, Tag.Str, new[] { new ImageHole(4, ImageHole.Out, 1) });
        var engine = new Activation();
        // f(g(P), h(Q)), P and Q unbound.
        int t = engine.AllocateHeap(3);
        int gp = engine.AllocateHeap(2);
        int hq = engine.AllocateHeap(2);
        engine.SetHeap(t, cells[0]);
        engine.SetHeap(t + 1, Cell.Str(gp));
        engine.SetHeap(t + 2, Cell.Str(hq));
        engine.SetHeap(gp, cells[5]);
        engine.SetHeap(gp + 1, Cell.Ref(gp + 1));
        engine.SetHeap(hq, cells[3]);
        engine.SetHeap(hq + 1, Cell.Ref(hq + 1));
        engine.SetRegister(0, Cell.Str(t));
        Assert.True(engine.UnifyBlit(image, 0));
        Assert.Equal(Cell.Ref(hq + 1), engine.GetRegister(1));
        Assert.Equal(engine.Deref(gp + 1), engine.Deref(hq + 1));   // P and Q, one variable now
        // P bound: Q takes its value; both bound and different: no match.
        engine.SetHeap(gp + 1, Cell.Int(1));
        engine.SetHeap(hq + 1, Cell.Ref(hq + 1));
        Assert.True(engine.UnifyBlit(image, 0));
        Assert.Equal(Cell.Int(1), engine.GetHeap(engine.Deref(hq + 1)));
        engine.SetHeap(hq + 1, Cell.Int(2));
        Assert.False(engine.UnifyBlit(image, 0));
    }

    [Fact]
    public void UnifyBlitMatchesBoxedLiterals()
    {
        var engine = new Activation();
        int b = engine.HeapTop;
        engine.SetRegister(0, engine.BuildImage(Image()));      // f([a], "xy", 1.5, 7), a copy
        Assert.True(engine.UnifyBlit(Image(), 0));
        engine.SetHeap(b + 4, Cell.Int(8));
        Assert.False(engine.UnifyBlit(Image(), 0));
        engine.SetHeap(b + 4, Cell.Int(7));
        engine.SetHeap(b + 8, Cell.PstrBuffer('x', 'z', 0));
        Assert.False(engine.UnifyBlit(Image(), 0));
    }

    [Fact]
    public void TheCodecCarriesTheHoles()
    {
        var image = Holed();
        using var ms = new MemoryStream();
        TermImageCodec.Write(new BinaryWriter(ms), image);
        ms.Position = 0;
        var back = TermImageCodec.Read(new BinaryReader(ms));
        Assert.Equal(image, back);
        Assert.Equal(4, back.Holes.Length);
        Assert.Equal(new ImageHole(4, ImageHole.Out | ImageHole.Y, 0), back.Holes[3]);
    }

    [Fact]
    public void ImagesAreEqualByContent()
    {
        var holed = Holed();
        Assert.Equal(holed, Holed());
        Assert.NotEqual(holed, new TermImage(holed.Cells.ToArray(), Tag.Str));   // the same cells, no holes
        Assert.Equal(Image(), Image());
        Assert.Equal(Image().GetHashCode(), Image().GetHashCode());
        var other = Image().Cells.ToArray();
        other[4] = Cell.Int(8);
        Assert.NotEqual(Image(), new TermImage(other, Tag.Str));
        Assert.NotEqual(new TermImage(new[] { Cell.Atom(1), Cell.Atom(2) }, Tag.Lis),
                        new TermImage(new[] { Cell.Atom(1), Cell.Atom(2) }, Tag.Str));
    }
}
