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
}
