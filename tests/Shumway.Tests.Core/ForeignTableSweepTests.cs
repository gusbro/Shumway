using Shumway.Core;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Core;

/// <summary>ADR-053: a foreign table entry lives as long as a FOREIGN cell
/// naming it is reachable, and no longer. Mechanism level: build the heap
/// directly, set the roots, collect, and read the table.
///
/// <para>Before this, nothing ever removed an entry -- not backtracking, not
/// the collector, not a cut -- so a query looping over a native reftype
/// output grew the table at the speed of the loop. Its two sibling side
/// tables both reclaim (TrailType.BigIntAlloc / RationalAlloc), which is
/// what made this an unapplied mechanism rather than a missing one.</para>
/// </summary>
public class ForeignTableSweepTests(ITestOutputHelper o)
{
    private static void ClearRegisters(Activation e)
    {
        for (int i = 0; i < e.RegisterCount; i++)
            e.SetRegister(i, Cell.Atom(0));
    }

    /// <summary>A foreign object nothing can reach is released. The object
    /// is what costs (a TermSlot holds a whole term tree); the slot is 8
    /// bytes.</summary>
    [Fact]
    public void AnUnreachableForeignObjectIsReleased()
    {
        var e = new Activation();
        ClearRegisters(e);

        // Two objects, only the first reachable: its FOREIGN cell is on the
        // heap under a root, the second's cell is dropped on the floor.
        var kept = new object();
        var dropped = new object();
        int h = e.AllocateHeap(1);
        e.SetHeap(h, e.MakeForeign(kept));
        e.MakeForeign(dropped);                    // cell discarded
        e.SetRegister(0, Cell.Ref(h));
        Assert.Equal(2, e.ForeignTableCount);

        e.CollectHeap();

        // The kept one still resolves through its (relocated) cell...
        Cell cell = e.GetHeap(e.Deref(e.GetRegister(0).AsHeapIndex));
        Assert.Equal(Tag.Foreign, cell.Tag);
        Assert.Same(kept, e.AsForeign(cell));
        // ...and the dropped one is gone: it was the tail, so the slot went
        // with the object.
        o.WriteLine($"foreign table after collection: 2 -> {e.ForeignTableCount}");
        Assert.Equal(1, e.ForeignTableCount);
        Assert.NotSame(dropped, e.ForeignById(1));
    }

    /// <summary>THE COUNTER-PROOF. A foreign object reachable only from a
    /// REGISTER must survive: the sweep may only free what the trace
    /// disproved. Without the Tag.Foreign case that records live ids, the
    /// sweep frees everything and this fails RED.</summary>
    [Fact]
    public void AForeignObjectReachableFromARegisterSurvives()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                   // garbage, so the collector moves

        var live = new object();
        e.SetRegister(0, e.MakeForeign(live));     // the cell IS the root

        e.CollectHeap();

        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Foreign, r0.Tag);
        Assert.Same(live, e.AsForeign(r0));
    }

    /// <summary>And one reachable only through a compound on the heap: the
    /// id has to be recorded from the TRACE, not just from the roots.
    /// </summary>
    [Fact]
    public void AForeignObjectNestedInAStructureSurvives()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                   // leading garbage

        var live = new object();
        int f = e.AllocateHeap(2);
        e.SetHeap(f, Cell.Functor(
            FunctorTable.Intern(AtomTable.Intern("wrap", permanent: true).Id, 1)));
        e.SetHeap(f + 1, e.MakeForeign(live));
        e.SetRegister(0, Cell.Str(f));

        e.CollectHeap();

        int nf = e.GetRegister(0).AsHeapIndex;
        Assert.Same(live, e.AsForeign(e.GetHeap(nf + 1)));
    }

    /// <summary>The tail shrinks while its last entry is dead, so the common
    /// append-then-die shape returns its slots too. Liveness-driven: a live
    /// id is never released, which is what makes the surviving ids stay
    /// positional and keeps them meaning what they meant.</summary>
    [Fact]
    public void TheTailShrinksButALiveIdIsNeverReleased()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                   // the collector returns
                                                   // early on an empty heap

        // id 0 live (rooted), ids 1..99 dead.
        var live = new object();
        e.SetRegister(0, e.MakeForeign(live));
        for (int i = 0; i < 99; i++) e.MakeForeign(new object());
        Assert.Equal(100, e.ForeignTableCount);

        e.CollectHeap();

        o.WriteLine($"table {100} -> {e.ForeignTableCount}");
        Assert.Equal(1, e.ForeignTableCount);       // the tail went
        Cell r0 = e.GetRegister(0);
        Assert.Equal(0, r0.AsForeignId);            // the live id did NOT move
        Assert.Same(live, e.AsForeign(r0));
    }

    /// <summary>A dead entry UNDER a live one cannot be removed without
    /// moving the live id, so it is nulled instead: the object goes, the
    /// slot stays, and every surviving id still means what it meant.
    /// </summary>
    [Fact]
    public void ADeadEntryUnderALiveOneIsNulledNotRemoved()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                   // ditto: something to collect

        var dead = new object();
        var live = new object();
        e.MakeForeign(dead);                        // id 0, unreachable
        e.SetRegister(0, e.MakeForeign(live));      // id 1, rooted
        Assert.Equal(2, e.ForeignTableCount);

        e.CollectHeap();

        Assert.Equal(2, e.ForeignTableCount);       // id 1 must keep meaning 1
        Cell r0 = e.GetRegister(0);
        Assert.Equal(1, r0.AsForeignId);
        Assert.Same(live, e.AsForeign(r0));
        Assert.Null(e.ForeignById(0));              // the object was released
    }

    /// <summary>A null a program stored ON PURPOSE is indistinguishable from
    /// a swept one by value, so the sweep must judge by LIVENESS. Shrinking
    /// on null-ness would drop a live id off the end and turn
    /// AsForeign into an index-out-of-range out of the engine.</summary>
    [Fact]
    public void AnIntentionalNullEntryIsStillALiveId()
    {
        var e = new Activation();
        ClearRegisters(e);
        e.AllocateHeapUnbound();                    // ditto
        e.SetRegister(0, e.MakeForeign(null));      // id 0, rooted, and null

        e.CollectHeap();

        Assert.Equal(1, e.ForeignTableCount);
        Cell r0 = e.GetRegister(0);
        Assert.Equal(Tag.Foreign, r0.Tag);
        Assert.Equal(0, r0.AsForeignId);
        Assert.Null(e.AsForeign(r0));               // resolves, does not throw
    }
}
