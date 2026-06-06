using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// Chunk 337 (Phase 28): a cut's trail compaction must not drop an
/// <c>AttrModify</c> entry that belongs to an OLD attributed variable just
/// because the entry's <c>HeapIdx</c> is large.
///
/// <para>For a <c>ValueChange</c> entry, <c>ExtraTrailEntry.HeapIdx</c> is the
/// modified heap cell, so the "drop young cells" compaction rule
/// (<c>HeapIdx &lt; parentHeapTop</c>) is correct. But for an <c>AttrModify</c>
/// entry, <c>HeapIdx</c> is an index into <c>_attrTrailLog</c> — a monotonic
/// counter of attribute mutations, NOT a heap address. Once a long-running
/// computation has mutated more attributes than the parent CP's heap top, that
/// counter exceeds <c>parentHeapTop</c> and the old (buggy) rule wrongly
/// dropped the entry — even for an OLD attvar whose record restore is still
/// required. The fix tests the attvar's HOME instead.</para>
///
/// <para>This is the engine root cause of the clpfd "donald"
/// <c>type_error(evaluable, fd(_,_))</c>: clpfd's many small if-then-else cuts
/// compact the trail constantly, and deep labeling pushes the attribute-log
/// counter past the cut's heap top, so an old FD variable's domain stopped
/// being restored on backtracking — leaving a stale / reclaimed attribute term
/// that flowed into bound arithmetic.</para>
/// </summary>
public class Chunk337Tests
{
    [Fact]
    public void Cut_DoesNotDropAttrModify_ForOldAttvar_WhenLogIndexExceedsParentHeapTop()
    {
        var engine = new Engine();
        const int mod = 1;

        // An OLD attributed variable X (low heap home) with attribute value v0.
        int x = engine.AllocateHeapUnbound();
        int v0 = engine.AllocateHeap(1);
        engine.SetHeap(v0, Cell.Atom(50));
        engine.PutAttr(x, mod, v0);

        // Inflate the attribute-mutation log WITHOUT growing the heap: re-set a
        // throwaway attvar to the same value cell many times. This drives the
        // _attrTrailLog counter (stored in each AttrModify entry's HeapIdx) far
        // past the tiny heap top — exactly donald's deep-search regime.
        int y = engine.AllocateHeapUnbound();
        int vy = engine.AllocateHeap(1);
        engine.SetHeap(vy, Cell.Atom(1));
        for (int i = 0; i < 200; i++) engine.PutAttr(y, mod, vy);

        // Outer choice point — the labeling value choice we later backtrack to.
        engine.SetHbForTesting(engine.HeapTop);
        engine.PushChoicePoint(0, 999);
        int outerB = engine.B;
        int outerBinding = engine.BindingTrailTop;
        int outerExtra = engine.ExtraTrailTop;

        // Modify the OLD attvar X above the outer CP. Its AttrModify entry now
        // carries a HeapIdx (log index ~200) far above the parent heap top
        // (~4), while its attvar HOME (x ~0) sits below it — the misclassified
        // case the old rule got wrong.
        int v1 = engine.AllocateHeap(1);
        engine.SetHeap(v1, Cell.Atom(77));
        engine.PutAttr(x, mod, v1);
        Assert.Equal(v1, engine.GetAttr(x, mod));

        // An inner choice point, then a cut committing to the outer CP. This
        // runs CompactTrails relative to the outer CP's small heap top. The
        // buggy rule drops X's entry (HeapIdx 200 >= 4); the fixed rule keeps
        // it (HOME 0 < 4).
        engine.PushChoicePoint(0, 998);
        engine.Cut(outerB);

        // Backtrack to the outer CP: X's attribute MUST be restored to v0.
        // With the bug it stays at v1 (the dropped entry never ran).
        engine.UnwindTrails(outerBinding, outerExtra);
        Assert.Equal(v0, engine.GetAttr(x, mod));
    }

    // A control: when the heap top is large (parent CP pushed after a big heap),
    // the old rule happened to keep AttrModify entries — confirm the fix keeps
    // them too (no regression for the previously-working case).
    [Fact]
    public void Cut_PreservesAttrModify_ForOldAttvar_SmallLogIndex()
    {
        var engine = new Engine();
        const int mod = 1;

        int x = engine.AllocateHeapUnbound();
        int v0 = engine.AllocateHeap(1);
        engine.SetHeap(v0, Cell.Atom(50));
        engine.PutAttr(x, mod, v0);

        // Grow the heap so the parent CP's heap top is comfortably above any
        // attribute-log index reached in this short test.
        engine.AllocateHeap(500);

        engine.SetHbForTesting(engine.HeapTop);
        engine.PushChoicePoint(0, 999);
        int outerB = engine.B;
        int outerBinding = engine.BindingTrailTop;
        int outerExtra = engine.ExtraTrailTop;

        int v1 = engine.AllocateHeap(1);
        engine.SetHeap(v1, Cell.Atom(77));
        engine.PutAttr(x, mod, v1);

        engine.PushChoicePoint(0, 998);
        engine.Cut(outerB);

        engine.UnwindTrails(outerBinding, outerExtra);
        Assert.Equal(v0, engine.GetAttr(x, mod));
    }
}
