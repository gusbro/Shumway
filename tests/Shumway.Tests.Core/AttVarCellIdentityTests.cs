using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>An ATTVAR cell is the one cell that names its own slot: the
/// payload is its home address, and that address is the key into the
/// attribute table. It may therefore be REFERENCED from anywhere but never
/// COPIED to another address — a copy is a second cell claiming a home that
/// is not its own slot, and the first lookup through it finds no record.
///
/// <para><see cref="Activation.TryUnconsListLike"/> is the chokepoint every
/// list walker goes through, and it handed out the raw head and tail cells.
/// A caller that stores what it peels (append/3 rebuilds a spine cell by
/// cell) then planted such a copy; the report was a KeyNotFoundException
/// escaping the engine out of dif/2, which was only the first thing to look
/// the copy up.</para></summary>
public class AttVarCellIdentityTests
{
    /// <summary>A one-element list whose head slot IS the attributed
    /// variable's home — the shape the engine's own list building produces,
    /// and the only one where peeling can copy an ATTVAR cell.</summary>
    private static (Activation Engine, int Pair) ListHoldingAnAttvarInline()
    {
        var engine = new Activation();
        int pair = engine.AllocateHeap(2);
        engine.SetHeap(pair, Cell.UnboundVar(pair));      // head: home == its own slot
        engine.SetHeap(pair + 1, Cell.Atom(AtomTable.EmptyListId));
        int value = engine.AllocateHeap(1);
        engine.SetHeap(value, Cell.Atom(50));
        engine.PutAttr(pair, 1, value);                   // promote the head slot
        Assert.Equal(Tag.AttVar, engine.GetHeap(pair).Tag);
        return (engine, pair);
    }

    [Fact]
    public void PeelingAListHandsOutAReferenceToAnAttvar_NotACopyOfIt()
    {
        var (engine, pair) = ListHoldingAnAttvarInline();
        Assert.True(engine.TryUnconsListLike(Cell.Lis(pair), out Cell head, out _));
        // A copy would be Tag.AttVar with the payload naming a slot that is
        // not where the cell now lives.
        Assert.Equal(Tag.Ref, head.Tag);
        Assert.Equal(pair, head.AsHeapIndex);
        // ...and it still denotes the attributed variable.
        Assert.Equal(Tag.AttVar, engine.GetHeap(engine.Deref(head.AsHeapIndex)).Tag);
    }

    [Fact]
    public void TheSameHoldsForAnAttvarInTheTailSlot()
    {
        var engine = new Activation();
        int pair = engine.AllocateHeap(2);
        engine.SetHeap(pair, Cell.Atom(42));
        engine.SetHeap(pair + 1, Cell.UnboundVar(pair + 1));   // open tail, its own home
        int value = engine.AllocateHeap(1);
        engine.SetHeap(value, Cell.Atom(50));
        engine.PutAttr(pair + 1, 1, value);

        Assert.True(engine.TryUnconsListLike(Cell.Lis(pair), out _, out Cell tail));
        Assert.Equal(Tag.Ref, tail.Tag);
        Assert.Equal(pair + 1, tail.AsHeapIndex);
    }

    [Fact]
    public void APeeledAttvarStillFindsItsAttribute_AfterBeingStoredElsewhere()
    {
        // What the crash actually was: peel, store the cell somewhere else,
        // then look the attribute up through the stored copy.
        var (engine, pair) = ListHoldingAnAttvarInline();
        Assert.True(engine.TryUnconsListLike(Cell.Lis(pair), out Cell head, out _));

        int elsewhere = engine.AllocateHeap(1);
        engine.SetHeap(elsewhere, head);                  // the append/3 move

        int home = engine.Deref(elsewhere);
        Assert.Equal(Tag.AttVar, engine.GetHeap(home).Tag);
        Assert.Single(engine.AttrModules(home));           // the record is reachable
    }

    [Fact]
    public void APlainUnboundHeadIsHandedOutUnchanged()
    {
        // Only the attributed cell needs re-pointing: a plain unbound REF is
        // a self-pointer and copies harmlessly, so the fix must not disturb
        // it (nor anything else the walkers rely on).
        var engine = new Activation();
        int pair = engine.AllocateHeap(2);
        engine.SetHeap(pair, Cell.UnboundVar(pair));
        engine.SetHeap(pair + 1, Cell.Atom(AtomTable.EmptyListId));

        Assert.True(engine.TryUnconsListLike(Cell.Lis(pair), out Cell head, out Cell tail));
        Assert.Equal(Tag.Ref, head.Tag);
        Assert.Equal(pair, head.AsHeapIndex);
        Assert.Equal(Tag.Atom, tail.Tag);
    }
}
