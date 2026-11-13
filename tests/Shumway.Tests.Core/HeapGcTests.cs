using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-016 mark-compact heap collector — mechanism-level tests that build
/// heap state directly, set the roots, collect, and verify reachable
/// structure survives (relocated) while garbage is reclaimed.
/// </summary>
public class HeapGcTests
{
    // Neutralise the default registers (each starts as Ref(0), which would
    // conservatively pin heap cell 0) so a test controls its roots exactly.
    private static void ClearRegisters(Activation e)
    {
        for (int i = 0; i < e.RegisterCount; i++)
            e.SetRegister(i, Cell.Atom(0));
    }

    private static int FooFunctor()
        => FunctorTable.Intern(AtomTable.Intern("foo", permanent: true).Id, 2);

    [Fact]
    public void Collect_SlidesLiveStructureOverLeadingGarbage()
    {
        var e = new Activation();
        ClearRegisters(e);

        // [0,1] garbage unbound vars; [2]=foo/2 functor, [3]=1, [4]=2;
        // [5,6] trailing garbage. Only the structure at 2 is rooted (reg0).
        e.AllocateHeapUnbound();                  // 0
        e.AllocateHeapUnbound();                  // 1
        int f = e.AllocateHeap(3);                // 2,3,4
        e.SetHeap(f, Cell.Functor(FooFunctor()));
        e.SetHeap(f + 1, Cell.Int(1));
        e.SetHeap(f + 2, Cell.Int(2));
        e.AllocateHeapUnbound();                  // 5
        e.AllocateHeapUnbound();                  // 6
        e.SetRegister(0, Cell.Str(f));

        Assert.Equal(7, e.HeapTop);
        int reclaimed = e.CollectHeap();

        Assert.Equal(4, reclaimed);               // 0,1,5,6
        Assert.Equal(3, e.HeapTop);

        // reg0 now points at the slid structure (0,1,2).
        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Str, r0.Tag);
        int nf = r0.AsHeapIndex;
        Assert.Equal(0, nf);
        Assert.Equal(Tag.Functor, e.GetHeap(nf).Tag);
        Assert.Equal(FooFunctor(), e.GetHeap(nf).AsFunctorId);
        Assert.Equal(1, e.GetHeap(nf + 1).AsInt);
        Assert.Equal(2, e.GetHeap(nf + 2).AsInt);
    }

    /// <summary>The forwarding map is a count per 64-cell word plus a
    /// popcount inside the word: live cells sitting exactly on and around the
    /// word boundaries, with garbage between them, land in order at the
    /// addresses their rank gives, and their references follow.</summary>
    [Fact]
    public void Collect_RelocatesLiveCellsAcrossWordBoundariesByRank()
    {
        var e = new Activation();
        ClearRegisters(e);

        // Pairs at 62..63, 64..65, 126..127, 191..192, 255..256, 319..320 and
        // 1000..1001: each a two-cell list (head Int, tail []) rooted by a
        // register, straddling or touching a word boundary, garbage between.
        int[] starts = { 62, 64, 126, 191, 255, 319, 1000 };
        int reg = 0;
        foreach (int start in starts)
        {
            while (e.HeapTop < start) e.AllocateHeapUnbound();   // garbage up to the pair
            int h = e.AllocateHeap(2);
            Assert.Equal(start, h);
            e.SetHeap(h, Cell.Int(start));
            e.SetHeap(h + 1, Cell.Atom(AtomTable.EmptyListId));
            e.SetRegister(reg++, Cell.Lis(h));
        }
        while (e.HeapTop < 1100) e.AllocateHeapUnbound();        // trailing garbage
        int oldTop = e.HeapTop;

        int reclaimed = e.CollectHeap();

        Assert.Equal(oldTop - 2 * starts.Length, reclaimed);
        Assert.Equal(2 * starts.Length, e.HeapTop);
        for (int i = 0; i < starts.Length; i++)
        {
            Cell r = e.GetRegister(i);
            Assert.Equal(Tag.Lis, r.Tag);
            Assert.Equal(2 * i, r.AsHeapIndex);                  // its rank among the live pairs
            Assert.Equal(starts[i], e.GetHeap(2 * i).AsInt);
            Assert.Equal(AtomTable.EmptyListId, e.GetHeap(2 * i + 1).AsAtomId);
        }
    }

    [Fact]
    public void Collect_PreservesUnboundVariableSelfRef()
    {
        var e = new Activation();
        ClearRegisters(e);

        e.AllocateHeapUnbound();                  // 0 garbage
        int v = e.AllocateHeapUnbound();          // 1 live (rooted)
        e.SetRegister(0, Cell.Ref(v));

        e.CollectHeap();

        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Ref, r0.Tag);
        int nv = r0.AsHeapIndex;
        Assert.Equal(Cell.UnboundVar(nv), e.GetHeap(nv));   // still self-referencing
    }

    [Fact]
    public void Collect_FollowsListSpine()
    {
        var e = new Activation();
        ClearRegisters(e);

        // Build [1, 2] : a LIS pair (head=1, tail=LIS pair (head=2, tail=[])).
        // Lay garbage before it so everything must slide.
        e.AllocateHeapUnbound();                          // 0 garbage
        int nilAtom = AtomTable.Intern("[]", permanent: true).Id;

        int p2 = e.AllocateHeap(2);                       // 1,2  inner pair
        e.SetHeap(p2, Cell.Int(2));
        e.SetHeap(p2 + 1, Cell.Atom(nilAtom));
        int p1 = e.AllocateHeap(2);                       // 3,4  outer pair
        e.SetHeap(p1, Cell.Int(1));
        e.SetHeap(p1 + 1, Cell.Lis(p2));
        e.SetRegister(0, Cell.Lis(p1));

        e.AllocateHeapUnbound();                          // 5 trailing garbage
        e.CollectHeap();

        // Walk the surviving list: 1, then 2, then [].
        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Lis, r0.Tag);
        int a = r0.AsHeapIndex;
        Assert.Equal(1, e.GetHeap(a).AsInt);
        Cell tail = e.GetHeap(a + 1);
        Assert.Equal(Tag.Lis, tail.Tag);
        int b = tail.AsHeapIndex;
        Assert.Equal(2, e.GetHeap(b).AsInt);
        Assert.Equal(Tag.Atom, e.GetHeap(b + 1).Tag);
    }

    [Fact]
    public void Collect_NoGarbage_IsNoOp()
    {
        var e = new Activation();
        ClearRegisters(e);
        int f = e.AllocateHeap(3);
        e.SetHeap(f, Cell.Functor(FooFunctor()));
        e.SetHeap(f + 1, Cell.Int(1));
        e.SetHeap(f + 2, Cell.Int(2));
        e.SetRegister(0, Cell.Str(f));

        Assert.Equal(0, e.CollectHeap());
        Assert.Equal(3, e.HeapTop);
    }

    [Fact]
    public void Collect_PreservesThePstrKindOfEverySurvivor()
    {
        // ADR-047: Relocate rebuilds a PSTR header around the buffer's new
        // index. Dropping the presentation bit there would turn a list of
        // chars into a list of codes mid-collection — non-deterministic, and
        // only reproducible under memory pressure. Both kinds are live here,
        // with garbage in front so the survivors actually move.
        var e = new Activation();
        ClearRegisters(e);

        e.AllocateHeapUnbound();                      // garbage, forces a slide
        e.AllocateHeapUnbound();
        int chars = e.MakePstr("hello", TextKind.Chars);
        e.AllocateHeapUnbound();                      // garbage between them
        int codes = e.MakePstr("world", TextKind.Codes);
        e.SetRegister(0, Cell.Ref(chars));
        e.SetRegister(1, Cell.Ref(codes));

        e.CollectHeap();

        int charsNow = e.GetRegister(0).AsHeapIndex;
        int codesNow = e.GetRegister(1).AsHeapIndex;
        Assert.NotEqual(chars, charsNow);             // it really moved
        Assert.Equal(TextKind.Chars, e.GetHeap(charsNow).AsPstrKind);
        Assert.Equal(TextKind.Codes, e.GetHeap(codesNow).AsPstrKind);
        Assert.Equal("hello", e.AsPstrString(charsNow));
        Assert.Equal("world", e.AsPstrString(codesNow));
    }

    [Fact]
    public void Collect_PreservesThePstrAstralBitOfEverySurvivor()
    {
        // The astral flag lives at payload bit 58, rebuilt by Relocate like
        // the kind bit. Losing it would re-split every surrogate pair into
        // two malformed elements — only under memory pressure, never in a
        // plain run.
        var e = new Activation();
        ClearRegisters(e);

        e.AllocateHeapUnbound();                      // garbage, forces a slide
        int astral = e.MakePstr("a😀b", TextKind.Chars);
        e.AllocateHeapUnbound();
        int bmp = e.MakePstr("plain", TextKind.Chars);
        e.SetRegister(0, Cell.Ref(astral));
        e.SetRegister(1, Cell.Ref(bmp));

        e.CollectHeap();

        int astralNow = e.GetRegister(0).AsHeapIndex;
        int bmpNow = e.GetRegister(1).AsHeapIndex;
        Assert.True(e.GetHeap(astralNow).AsPstrIsAstral);
        Assert.False(e.GetHeap(bmpNow).AsPstrIsAstral);
        Assert.Equal("a😀b", e.AsPstrString(astralNow));
    }

    /// <summary>The control stack is scanned conservatively: each reference
    /// tag roots its cells and is relocated with them, the leaves stay bit for
    /// bit, a control word whose value would read as a heap index roots
    /// nothing, and an index past the heap is left alone.</summary>
    [Fact]
    public void Collect_ScansAndRelocatesEveryTagOfTheControlStack()
    {
        var e = new Activation();
        ClearRegisters(e);
        int nil = AtomTable.EmptyListId;

        e.AllocateHeapUnbound();                          // 0 garbage
        int v = e.AllocateHeapUnbound();                  // 1 unbound variable
        e.AllocateHeapUnbound();                          // 2 garbage
        int pair = e.AllocateHeap(2);                     // 3,4 [7]
        e.SetHeap(pair, Cell.Int(7));
        e.SetHeap(pair + 1, Cell.Atom(nil));
        e.AllocateHeapUnbound();                          // 5 garbage
        int f = e.AllocateHeap(3);                        // 6..8 foo(1, 2)
        e.SetHeap(f, Cell.Functor(FooFunctor()));
        e.SetHeap(f + 1, Cell.Int(1));
        e.SetHeap(f + 2, Cell.Int(2));
        int named = e.AllocateHeapUnbound();              // 9 garbage a control word names
        int fl = e.MakeFloat(2.5);                        // 10,11 header (garbage) and paired cell
        e.AllocateHeapUnbound();                          // 12 garbage
        int ps = e.MakePstr("stack", TextKind.Chars);     // 13 header (garbage), 14,15 buffers, 16 tail
        e.AllocateHeapUnbound();                          // 17 garbage
        int top = e.HeapTop;
        long paired = e.GetHeap(fl + 1).Data;
        long buffer0 = e.GetHeap(ps + 1).Data, buffer1 = e.GetHeap(ps + 2).Data;

        Cell[] slots =
        {
            Cell.Ref(v), Cell.RawInt(named), Cell.Lis(pair), Cell.Int(named), Cell.Str(f),
            Cell.Atom(nil), e.GetHeap(fl), e.GetHeap(ps), Cell.Ref(top + 100),
        };
        for (int i = 0; i < slots.Length; i++) e._stack[i] = slots[i];
        e._stackTop = slots.Length;

        e.CollectHeap();

        // The variable, the pair, foo/2, the float's paired cell, the string's
        // two buffers and its tail.
        Assert.Equal(10, e.HeapTop);
        Cell[] s = e._stack;

        Assert.Equal(Tag.Ref, s[0].Tag);
        Assert.Equal(s[0].Data, e.GetHeap(s[0].AsHeapIndex).Data);
        Assert.Equal(slots[1].Data, s[1].Data);
        Assert.Equal(Tag.Lis, s[2].Tag);
        Assert.Equal(7, e.GetHeap(s[2].AsHeapIndex).AsInt);
        Assert.Equal(Cell.Atom(nil).Data, e.GetHeap(s[2].AsHeapIndex + 1).Data);
        Assert.Equal(slots[3].Data, s[3].Data);
        Assert.Equal(Tag.Str, s[4].Tag);
        Assert.Equal(FooFunctor(), e.GetHeap(s[4].AsHeapIndex).AsFunctorId);
        Assert.Equal(1, e.GetHeap(s[4].AsHeapIndex + 1).AsInt);
        Assert.Equal(2, e.GetHeap(s[4].AsHeapIndex + 2).AsInt);
        Assert.Equal(slots[5].Data, s[5].Data);
        Assert.Equal(Tag.Float, s[6].Tag);
        Assert.Equal(slots[6].Data >> 32, s[6].Data >> 32);
        Assert.Equal(paired, e.GetHeap(s[6].FloatPairedIndex).Data);
        Assert.Equal(Tag.Pstr, s[7].Tag);
        Assert.Equal(slots[7].AsPstrLength, s[7].AsPstrLength);
        Assert.Equal(slots[7].AsPstrOffset, s[7].AsPstrOffset);
        Assert.Equal(slots[7].AsPstrKind, s[7].AsPstrKind);
        Assert.Equal(buffer0, e.GetHeap(s[7].AsPstrBufferIndex).Data);
        Assert.Equal(buffer1, e.GetHeap(s[7].AsPstrBufferIndex + 1).Data);
        Assert.Equal(slots[8].Data, s[8].Data);
    }
}
