using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class CutTests
{
    // ---------- Basic semantics ----------

    [Fact]
    public void Cut_BarrierEqualsCurrentB_IsNoOp()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int bBefore = engine.B;
        int stackBefore = engine.StackTop;

        engine.Cut(bBefore);

        Assert.Equal(bBefore, engine.B);
        Assert.Equal(stackBefore, engine.StackTop);
    }

    [Fact]
    public void Cut_DiscardsCpsAboveBarrier()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0x100);
        int outer = engine.B;
        engine.PushChoicePoint(0, 0x200);
        engine.PushChoicePoint(0, 0x300);

        engine.Cut(outer);

        Assert.Equal(outer, engine.B);
    }

    [Fact]
    public void Cut_ToNegativeOne_DiscardsAllCps()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        engine.PushChoicePoint(0, 0);
        Assert.NotEqual(-1, engine.B);

        engine.Cut(-1);

        Assert.Equal(-1, engine.B);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void Cut_NegativeBarrierBelowMinusOne_Throws(int badBarrier)
    {
        var engine = new Engine();
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Cut(badBarrier));
    }

    [Fact]
    public void Cut_BarrierAboveCurrentB_IsNoOp()
    {
        // Phase-10 chunk 146: a stale barrier above current B means
        // the CP the cut wanted to commit to has been popped already
        // (typically by a surrounding catch/3 unwinding past the
        // clause-entry snapshot). ISO semantics: cut commits to the
        // most recent *active* CP; if it's gone, the cut is a no-op.
        // Was an ArgumentOutOfRangeException pre-chunk-146.
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int bBefore = engine.B;
        engine.Cut(engine.B + 100);
        Assert.Equal(bBefore, engine.B);  // unchanged
    }

    // ---------- Trail compaction: binding trail ----------

    [Fact]
    public void Cut_KeepsBindingsOfOldVars_DiscardsBindingsOfNewVars()
    {
        // Setup so that the inner-CP region has bindings of vars created both before
        // and after the parent CP.
        var engine = new Engine();
        int oldVar = engine.AllocateHeapUnbound();         // idx 0, "old" (pre-parent)

        engine.PushChoicePoint(0, 0);                       // parent: parentHeapTop = 1
        int parentB = engine.B;

        int midVar = engine.AllocateHeapUnbound();         // idx 1, between parent and inner

        engine.PushChoicePoint(0, 0);                       // inner: _hb = 2 now

        engine.Bind(oldVar, Cell.Atom(10));                 // idx 0, trailed (0<2), kept (0<1)
        engine.Bind(midVar, Cell.Atom(20));                 // idx 1, trailed (1<2), compacted (1<1 false)
        Assert.Equal(2, engine.BindingTrailTop);

        engine.Cut(parentB);

        Assert.Equal(parentB, engine.B);
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(oldVar, engine.BindingTrailSpan[0]);

        // The bindings themselves stay on the heap — cut does not undo, only discards
        // CPs and compacts trail entries that no longer have a reason to exist.
        Assert.Equal(Cell.Atom(10), engine.GetHeap(oldVar));
        Assert.Equal(Cell.Atom(20), engine.GetHeap(midVar));
    }

    [Fact]
    public void Cut_LeavesPreParentBindingsUntouched()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        // Pre-existing trail entry from outside any CP (HB still 0 — won't actually trail).
        // Construct one manually via a CP/binding sequence.
        engine.PushChoicePoint(0, 0);                       // outer CP
        int outer = engine.B;
        engine.Bind(v, Cell.Atom(1));                       // trailed under outer CP

        engine.PushChoicePoint(0, 0);                       // inner CP
        int newVar = engine.AllocateHeapUnbound();
        engine.Bind(newVar, Cell.Atom(9));                  // will be compacted

        engine.Cut(outer);

        // The original trail entry for v (made before the inner CP existed) survives.
        Assert.Contains(v, engine.BindingTrailSpan.ToArray());
    }

    [Fact]
    public void Cut_ToNegativeOne_EmptiesBindingTrail()
    {
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();
        engine.PushChoicePoint(0, 0);
        engine.Bind(v, Cell.Atom(1));
        Assert.Equal(1, engine.BindingTrailTop);

        engine.Cut(-1);

        Assert.Equal(0, engine.BindingTrailTop);
        // The heap value is unchanged — cut never undoes.
        Assert.Equal(Cell.Atom(1), engine.GetHeap(v));
    }

    // ---------- Trail compaction: extra trail ----------

    [Fact]
    public void Cut_KeepsExtraEntriesForOldCells_DiscardsForNewCells()
    {
        var engine = new Engine();
        int oldCell = engine.AllocateHeap(1);
        engine.SetHeap(oldCell, Cell.Atom(10));             // idx 0

        engine.PushChoicePoint(0, 0);                        // parent at heap top = 1
        int parentB = engine.B;

        int newCell = engine.AllocateHeap(1);
        engine.SetHeap(newCell, Cell.Atom(20));             // idx 1

        engine.PushChoicePoint(0, 0);                        // inner

        engine.TrailValueChange(oldCell, Cell.Atom(10));     // 0 < 1, kept
        engine.SetHeap(oldCell, Cell.Atom(100));
        engine.TrailValueChange(newCell, Cell.Atom(20));     // 1 < 1 false, compacted
        engine.SetHeap(newCell, Cell.Atom(200));

        engine.Cut(parentB);

        Assert.Equal(1, engine.ExtraTrailTop);
    }

    [Fact]
    public void Cut_AdjustsKeptExtraEntryMarkerToCompactedBindingPosition()
    {
        // Critical test: a single cell gets a Bind first, then a ValueChange. After cut
        // compaction, the kept extra entry's marker MUST point AFTER the kept binding so
        // that UnwindTrails processes the extra BEFORE the binding — otherwise the binding
        // is rolled back first and the extra writes the intermediate (bound) value over
        // the unbound state, leaving the cell with the wrong final value.
        var engine = new Engine();
        int cell = engine.AllocateHeapUnbound();             // idx 0

        engine.PushChoicePoint(0, 0);                         // parent at heap top = 1
        int parentB = engine.B;

        int midVar = engine.AllocateHeapUnbound();           // idx 1

        engine.PushChoicePoint(0, 0);                         // inner at heap top = 2

        engine.Bind(cell, Cell.Atom(100));                    // binding[0]: cell, kept
        engine.Bind(midVar, Cell.Atom(50));                   // binding[1]: midVar, compacted
        engine.TrailValueChange(cell, Cell.Atom(100));        // extra[0] marker=2
        engine.SetHeap(cell, Cell.Atom(200));

        engine.Cut(parentB);

        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(1, engine.ExtraTrailTop);

        // Unwind to the parent CP's saved state. Correct ordering yields cell=Unbound.
        // A non-adjusted marker (still 2) would process binding first, then extra would
        // re-write Atom(100), leaving cell=Atom(100) — wrong.
        engine.UnwindTrails(0, 0);
        Assert.Equal(Cell.UnboundVar(cell), engine.GetHeap(cell));
    }

    [Fact]
    public void Cut_PreservesOrderingOfMultipleKeptExtras()
    {
        // Two ValueChange entries on the same cell. Reverse-order processing must restore
        // the original value through both.
        var engine = new Engine();
        int cell = engine.AllocateHeap(1);
        engine.SetHeap(cell, Cell.Atom(10));                 // idx 0

        engine.PushChoicePoint(0, 0);                         // parent at heap top = 1
        int parentB = engine.B;
        engine.PushChoicePoint(0, 0);                         // inner

        engine.TrailValueChange(cell, Cell.Atom(10));
        engine.SetHeap(cell, Cell.Atom(11));
        engine.TrailValueChange(cell, Cell.Atom(11));
        engine.SetHeap(cell, Cell.Atom(12));

        engine.Cut(parentB);
        Assert.Equal(2, engine.ExtraTrailTop);

        engine.UnwindTrails(0, 0);
        Assert.Equal(Cell.Atom(10), engine.GetHeap(cell));
    }

    // ---------- GetLevel ----------

    [Fact]
    public void GetLevel_SavesB0IntoY()
    {
        // GetLevel captures _b0 (the procedure-entry barrier), NOT the current
        // B — so the captured value survives sub-goal calls that overwrite
        // the engine's B0 register.
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        engine.SetB0(-1);                                     // simulate "procedure entry saw no CPs"
        engine.Allocate(1);

        engine.GetLevel(0);

        Assert.Equal(-1L, engine.GetY(0).Data);
    }

    [Fact]
    public void GetLevelThenCut_DiscardsCpsCreatedAfterCapture()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0x100);                     // pre-procedure CP
        int outerB = engine.B;
        engine.SetB0(outerB);                                 // "procedure entered with B=outerB"
        engine.Allocate(1);
        engine.GetLevel(0);                                   // Y[0] := _b0 = outerB

        engine.PushChoicePoint(0, 0x200);                     // sub-goal CP
        engine.PushChoicePoint(0, 0x300);                     // sub-sub-goal CP

        int barrier = (int)engine.GetY(0).Data;
        engine.Cut(barrier);

        Assert.Equal(outerB, engine.B);                       // both inner CPs discarded
    }

    [Fact]
    public void GetLevel_NoEnvironment_Throws()
    {
        var engine = new Engine();
        Assert.Throws<InvalidOperationException>(() => engine.GetLevel(0));
    }

    // ---------- Cut + Trust interaction ----------

    [Fact]
    public void Cut_FollowedByTrust_StateRestoredCorrectly()
    {
        // After Cut, the surviving (outer) CP must still be usable: TrustMe should
        // restore from it correctly.
        var engine = new Engine();
        int v = engine.AllocateHeapUnbound();

        engine.PushChoicePoint(0, 0x100);                     // outer
        int outerB = engine.B;
        engine.PushChoicePoint(0, 0x200);                     // inner

        engine.Bind(v, Cell.Atom(1));                         // 0 < _hb, trailed

        engine.Cut(outerB);
        Assert.Equal(outerB, engine.B);
        Assert.Equal(Cell.Atom(1), engine.GetHeap(v));        // cut does NOT undo

        engine.TrustMe();
        Assert.Equal(-1, engine.B);
        Assert.Equal(Cell.UnboundVar(v), engine.GetHeap(v));  // trust undoes
    }
}
