using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class PstrTests
{
    // ---------- Cell.Pstr factory & accessors ----------

    [Fact]
    public void Pstr_FactoryEncodesAllThreeFields()
    {
        var cell = Cell.Pstr(length: 7, bufferIdx: 42, offset: 2);
        Assert.Equal(Tag.Pstr, cell.Tag);
        Assert.Equal(7, cell.AsPstrLength);
        Assert.Equal(42, cell.AsPstrBufferIndex);
        Assert.Equal(2, cell.AsPstrOffset);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(123, 456, 1)]
    [InlineData(Cell.MaxPstrLength, Cell.MaxPstrBufferIndex, 2)]
    public void Pstr_RoundTrip(int length, int bufferIdx, int offset)
    {
        var cell = Cell.Pstr(length, bufferIdx, offset);
        Assert.Equal(length, cell.AsPstrLength);
        Assert.Equal(bufferIdx, cell.AsPstrBufferIndex);
        Assert.Equal(offset, cell.AsPstrOffset);
    }

    [Fact]
    public void Pstr_OutOfRangeLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Pstr(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Pstr(Cell.MaxPstrLength + 1, 0, 0));
    }

    [Fact]
    public void Pstr_OutOfRangeBufferIdx_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Pstr(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Pstr(0, Cell.MaxPstrBufferIndex + 1, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Pstr_OutOfRangeOffset_Throws(int offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cell.Pstr(0, 0, offset));
    }

    // ---------- Cell.PstrBuffer ----------

    [Fact]
    public void PstrBuffer_PacksThreeCodeUnits()
    {
        var cell = Cell.PstrBuffer('a', 'b', 'c');
        Assert.Equal(Tag.PstrBuffer, cell.Tag);
        Assert.Equal('a', cell.AsPstrCodeUnit(0));
        Assert.Equal('b', cell.AsPstrCodeUnit(1));
        Assert.Equal('c', cell.AsPstrCodeUnit(2));
    }

    [Fact]
    public void PstrBuffer_HandlesFullSixteenBitRange()
    {
        var cell = Cell.PstrBuffer(0x0000, 0x7FFF, 0xFFFF);
        Assert.Equal(0x0000, cell.AsPstrCodeUnit(0));
        Assert.Equal(0x7FFF, cell.AsPstrCodeUnit(1));
        Assert.Equal(0xFFFF, cell.AsPstrCodeUnit(2));
    }

    // ---------- Engine.MakePstr / AsPstrString ----------

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("hello world")]
    [InlineData("Hola, ñoño")]                       // BMP unicode
    [InlineData("a very long string with many characters more than 30")]
    public void MakePstr_AsPstrString_RoundTrips(string value)
    {
        var engine = new Engine();
        int idx = engine.MakePstr(value);
        Assert.Equal(value, engine.AsPstrString(idx));
    }

    [Fact]
    public void MakePstr_LayoutIsHeaderBuffersTail()
    {
        var engine = new Engine();
        int idx = engine.MakePstr("abcdef");           // 6 code units → 2 buffer cells

        Assert.Equal(Tag.Pstr, engine.GetHeap(idx).Tag);
        Assert.Equal(Tag.PstrBuffer, engine.GetHeap(idx + 1).Tag);
        Assert.Equal(Tag.PstrBuffer, engine.GetHeap(idx + 2).Tag);

        int tailIdx = engine.GetPstrTailIndex(idx);
        Assert.Equal(idx + 3, tailIdx);
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetHeap(tailIdx));
    }

    [Fact]
    public void MakePstr_EmptyString_OmitsBufferCells()
    {
        var engine = new Engine();
        int idx = engine.MakePstr("");
        Assert.Equal(0, engine.GetHeap(idx).AsPstrLength);
        Assert.Equal(idx + 1, engine.GetPstrTailIndex(idx));
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetHeap(idx + 1));
    }

    [Fact]
    public void MakePstr_AdvancesHeapByExpectedCount()
    {
        var engine = new Engine();
        int before = engine.HeapTop;
        engine.MakePstr("abcdefghi");                  // 9 code units → 3 buffer + 1 header + 1 tail
        Assert.Equal(before + 5, engine.HeapTop);
    }

    [Fact]
    public void MakePstr_NullArgument_Throws()
    {
        var engine = new Engine();
        Assert.Throws<ArgumentNullException>(() => engine.MakePstr(null!));
    }

    [Fact]
    public void AsPstrString_OnNonPstr_Throws()
    {
        var engine = new Engine();
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(0));
        Assert.Throws<InvalidOperationException>(() => engine.AsPstrString(slot));
    }

    // ---------- Unify: PSTR ↔ var ----------

    [Fact]
    public void Unify_VarWithPstr_BindsVarAsRefToHeader()
    {
        // PSTR is "compound-like" — BindVarToValue writes a REF, not a cell copy.
        var engine = new Engine();
        int pstr = engine.MakePstr("abc");
        int v = engine.AllocateHeapUnbound();

        Assert.True(engine.Unify(v, pstr));
        Cell vCell = engine.GetHeap(v);
        Assert.Equal(Tag.Ref, vCell.Tag);
        Assert.Equal(pstr, vCell.AsHeapIndex);
        Assert.Equal(pstr, engine.Deref(v));
    }

    // ---------- Unify: PSTR ↔ PSTR ----------

    [Fact]
    public void Unify_PstrsSameContent_Succeeds()
    {
        var engine = new Engine();
        int a = engine.MakePstr("hello");
        int b = engine.MakePstr("hello");
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_PstrsDifferentContentSameLength_Fails()
    {
        var engine = new Engine();
        int a = engine.MakePstr("abc");
        int b = engine.MakePstr("abd");
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_TwoEmptyPstrs_Succeeds()
    {
        var engine = new Engine();
        int a = engine.MakePstr("");
        int b = engine.MakePstr("");
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_DifferentLengthPstrsWithStoredTails_Fails()
    {
        // Both PSTRs have tail = []. A is "abc", B is "ab". Common prefix matches; then
        // A's tail ("c" slice) vs B's tail ([]). Different shapes — fail.
        var engine = new Engine();
        int a = engine.MakePstr("abc");
        int b = engine.MakePstr("ab");
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_DifferentLengthPstrsWithCommonPrefix_BindsTailVar()
    {
        // Build a "ab|X" partial PSTR by overriding the tail with an unbound var.
        var engine = new Engine();
        int xPos = engine.AllocateHeapUnbound();
        int partial = engine.MakePstr("ab");           // header at partial, tail at partial+2
        engine.SetHeap(engine.GetPstrTailIndex(partial), Cell.Ref(xPos));

        int full = engine.MakePstr("abcdef");

        Assert.True(engine.Unify(partial, full));

        // X should now reference a slice of `full` representing "cdef".
        int xTarget = engine.Deref(xPos);
        Cell xCell = engine.GetHeap(xTarget);
        Assert.Equal(Tag.Pstr, xCell.Tag);
        Assert.Equal(4, xCell.AsPstrLength);
    }

    // ---------- Unify: PSTR ↔ [] ----------

    [Fact]
    public void Unify_EmptyPstrWithEmptyListAtom_Succeeds()
    {
        var engine = new Engine();
        int p = engine.MakePstr("");                   // length 0, tail = []
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(AtomTable.EmptyListId));
        Assert.True(engine.Unify(p, slot));
    }

    [Fact]
    public void Unify_NonEmptyPstrWithEmptyListAtom_Fails()
    {
        var engine = new Engine();
        int p = engine.MakePstr("a");
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(AtomTable.EmptyListId));
        Assert.False(engine.Unify(p, slot));
    }

    [Fact]
    public void Unify_NonEmptyPstrWithUnrelatedAtom_Fails()
    {
        var engine = new Engine();
        int p = engine.MakePstr("a");
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Atom(AtomTable.TrueId));
        Assert.False(engine.Unify(p, slot));
    }

    // ---------- Unify: PSTR ↔ LIS ----------

    [Fact]
    public void Unify_PstrWithExplicitCodeList_Succeeds()
    {
        // PSTR "abc" should unify with the cons list [97, 98, 99 | []].
        var engine = new Engine();
        int pstr = engine.MakePstr("abc");

        int lis = engine.AllocateHeap(2 * 3 + 1);
        engine.SetHeap(lis, Cell.Lis(lis + 1));
        engine.SetHeap(lis + 1, Cell.Int('a'));
        engine.SetHeap(lis + 2, Cell.Lis(lis + 3));
        engine.SetHeap(lis + 3, Cell.Int('b'));
        engine.SetHeap(lis + 4, Cell.Lis(lis + 5));
        engine.SetHeap(lis + 5, Cell.Int('c'));
        engine.SetHeap(lis + 6, Cell.Atom(AtomTable.EmptyListId));

        Assert.True(engine.Unify(pstr, lis));
    }

    [Fact]
    public void Unify_PstrWithListOfWrongCodes_Fails()
    {
        var engine = new Engine();
        int pstr = engine.MakePstr("abc");

        int lis = engine.AllocateHeap(2 * 3 + 1);
        engine.SetHeap(lis, Cell.Lis(lis + 1));
        engine.SetHeap(lis + 1, Cell.Int('a'));
        engine.SetHeap(lis + 2, Cell.Lis(lis + 3));
        engine.SetHeap(lis + 3, Cell.Int('X'));        // wrong code here
        engine.SetHeap(lis + 4, Cell.Lis(lis + 5));
        engine.SetHeap(lis + 5, Cell.Int('c'));
        engine.SetHeap(lis + 6, Cell.Atom(AtomTable.EmptyListId));

        Assert.False(engine.Unify(pstr, lis));
    }

    [Fact]
    public void Unify_PstrWithListBindsVarHead()
    {
        // [X, 98 | []] vs "ab" — X should be bound to 97.
        var engine = new Engine();
        int pstr = engine.MakePstr("ab");

        int lis = engine.AllocateHeap(2 * 2 + 1);
        engine.SetHeap(lis, Cell.Lis(lis + 1));
        engine.SetHeap(lis + 1, Cell.UnboundVar(lis + 1));     // X
        engine.SetHeap(lis + 2, Cell.Lis(lis + 3));
        engine.SetHeap(lis + 3, Cell.Int('b'));
        engine.SetHeap(lis + 4, Cell.Atom(AtomTable.EmptyListId));
        int xPos = lis + 1;

        Assert.True(engine.Unify(pstr, lis));
        Assert.Equal((long)'a', engine.GetHeap(engine.Deref(xPos)).AsInt);
    }

    [Fact]
    public void Unify_PstrWithListBindsTailVar()
    {
        // [97 | T] vs "abcdef" — T binds to a slice representing "bcdef".
        var engine = new Engine();
        int pstr = engine.MakePstr("abcdef");

        int lis = engine.AllocateHeap(3);
        engine.SetHeap(lis, Cell.Lis(lis + 1));
        engine.SetHeap(lis + 1, Cell.Int('a'));
        engine.SetHeap(lis + 2, Cell.UnboundVar(lis + 2));      // T
        int tPos = lis + 2;

        Assert.True(engine.Unify(pstr, lis));
        Cell tCell = engine.GetHeap(engine.Deref(tPos));
        Assert.Equal(Tag.Pstr, tCell.Tag);
        Assert.Equal(5, tCell.AsPstrLength);                    // "bcdef"
    }

    [Fact]
    public void Unify_PstrWithShorterListAndMatchingPrefix_FailsOnTail()
    {
        // "abc" vs [97, 98 | []]: prefix matches, but PSTR's remaining "c" doesn't
        // unify with []. Should fail.
        var engine = new Engine();
        int pstr = engine.MakePstr("abc");

        int lis = engine.AllocateHeap(2 * 2 + 1);
        engine.SetHeap(lis, Cell.Lis(lis + 1));
        engine.SetHeap(lis + 1, Cell.Int('a'));
        engine.SetHeap(lis + 2, Cell.Lis(lis + 3));
        engine.SetHeap(lis + 3, Cell.Int('b'));
        engine.SetHeap(lis + 4, Cell.Atom(AtomTable.EmptyListId));

        Assert.False(engine.Unify(pstr, lis));
    }

    // ---------- Unify with mixed tag failures ----------

    [Fact]
    public void Unify_PstrWithInt_Fails()
    {
        var engine = new Engine();
        int p = engine.MakePstr("a");
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Int(0));
        Assert.False(engine.Unify(p, slot));
    }

    [Fact]
    public void Unify_PstrWithCompound_Fails()
    {
        var engine = new Engine();
        int functorId = FunctorTable.Intern(1, 1);
        int s = engine.AllocateHeap(3);
        engine.SetHeap(s, Cell.Str(s + 1));
        engine.SetHeap(s + 1, Cell.Functor(functorId));
        engine.SetHeap(s + 2, Cell.Atom(0));

        int p = engine.MakePstr("a");
        Assert.False(engine.Unify(p, s));
    }

    // ---------- Edge cases for slicing ----------

    [Fact]
    public void GetPstrTailIndex_RespectsOffsetWhenComputingBufferCount()
    {
        // A manually constructed PSTR with offset=1, length=4: positions 1..4 of the buffer
        // span 4 cells worth (buffer cells at idx+0 and idx+1). Tail should be at idx+2.
        var engine = new Engine();
        int bufStart = engine.AllocateHeap(2);
        engine.SetHeap(bufStart, Cell.PstrBuffer(0, 'a', 'b'));
        engine.SetHeap(bufStart + 1, Cell.PstrBuffer('c', 'd', 0));
        int tailSlot = engine.AllocateHeap(1);
        engine.SetHeap(tailSlot, Cell.Atom(AtomTable.EmptyListId));

        int hdrSlot = engine.AllocateHeap(1);
        engine.SetHeap(hdrSlot, Cell.Pstr(length: 4, bufferIdx: bufStart, offset: 1));

        Assert.Equal(tailSlot, engine.GetPstrTailIndex(hdrSlot));
        Assert.Equal("abcd", engine.AsPstrString(hdrSlot));
    }
}
