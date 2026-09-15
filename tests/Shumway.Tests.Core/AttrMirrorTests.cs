using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>The linear-memory image of the attribute store has to say exactly
/// what the store says, after every kind of mutation there is: a put, an
/// overwrite, a del, the backtracking that undoes them, and the heap collector
/// moving every index at once.
///
/// <para>The image exists so a compiled wasm module can answer get_attr/3
/// without leaving wasm. Its failure mode is therefore NOT a wrong answer
/// anyone would notice here: a stale row makes the module return a value the
/// engine no longer holds, which is unsound and invisible to every test that
/// only compares answers. So these assert the image directly, in both
/// directions, at each boundary.</para>
///
/// <para>Red counter-proof, run by hand when this was written and reproducible
/// the same way: delete the AttrMirrorDelete call from AttrRemove in
/// Activation.Attrs.cs and <see cref="Del_RemovesTheRow"/> plus
/// <see cref="Backtracking_RestoresTheImage"/> both fail with "stale row";
/// delete it from AttrDropRecord and
/// <see cref="DroppingAVariable_RemovesEveryRowItHad"/> fails; drop the
/// rebuild from AttrRekeyAll and <see cref="HeapGc_RekeysTheImage"/> fails.
/// Without that the checks below would pass over an image nobody maintains.
/// </para></summary>
public class AttrMirrorTests
{
    private const int ModA = 1, ModB = 2;

    private static int Value(Activation e, int atom)
    {
        int v = e.AllocateHeap(1);
        e.SetHeap(v, Cell.Atom(atom));
        return v;
    }

    private static void Agrees(Activation e, string site)
    {
        string? bad = e.AttrMirrorDisagreement();
        if (bad is not null) Assert.Fail($"{site}: {bad}");
    }

    [Fact]
    public void Put_IsReadableFromTheImage()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        int x = e.AllocateHeapUnbound();
        int v = Value(e, 50);
        e.PutAttr(x, ModA, v);

        // Not merely "they agree" -- the image has to hold the VALUE, or a
        // check that only counted rows would pass on an empty one.
        Assert.Equal(v, e.AttrMirrorLookup(x, ModA));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "put");
    }

    [Fact]
    public void Overwrite_ReplacesTheRowRatherThanAddingOne()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        int x = e.AllocateHeapUnbound();
        int v0 = Value(e, 50);
        int v1 = Value(e, 51);
        e.PutAttr(x, ModA, v0);
        e.PutAttr(x, ModA, v1);

        Assert.Equal(v1, e.AttrMirrorLookup(x, ModA));
        Agrees(e, "overwrite");
    }

    [Fact]
    public void Del_RemovesTheRow()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));
        e.PutAttr(x, ModB, Value(e, 51));
        int keptB = e.AttrMirrorLookup(x, ModB);

        e.DelAttr(x, ModA);

        Assert.Equal(-1, e.AttrMirrorLookup(x, ModA));
        // The sibling must survive: a tombstone that stopped the probe would
        // hide it, and nothing else in this suite would notice.
        Assert.Equal(keptB, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "del");
    }

    [Fact]
    public void DroppingAVariable_RemovesEveryRowItHad()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));
        e.PutAttr(x, ModB, Value(e, 51));

        // The last attribute going away demotes the cell and drops the record.
        e.DelAttr(x, ModA);
        e.DelAttr(x, ModB);

        Assert.Equal(-1, e.AttrMirrorLookup(x, ModA));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "drop");
    }

    [Fact]
    public void Backtracking_RestoresTheImage()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        int x = e.AllocateHeapUnbound();
        int v0 = Value(e, 50);
        e.PutAttr(x, ModA, v0);

        e.SetHbForTesting(e.HeapTop);
        e.PushChoicePoint(0, 999);
        int binding = e.BindingTrailTop;
        int extra = e.ExtraTrailTop;

        int v1 = Value(e, 51);
        e.PutAttr(x, ModA, v1);
        e.PutAttr(x, ModB, Value(e, 52));
        Assert.Equal(v1, e.AttrMirrorLookup(x, ModA));
        Agrees(e, "above the CP");

        e.UnwindTrails(binding, extra);

        // The undo runs through the same funnel, so the image has to come back
        // with the store -- including ModB, which did not exist below the CP.
        Assert.Equal(v0, e.AttrMirrorLookup(x, ModA));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "after backtracking");
    }

    [Fact]
    public void ACutReclaimingABoundAttvar_DropsItsRowsToo()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        // Old enough that the cut's compaction keeps the CP but discards
        // anything younger.
        for (int i = 0; i < 8; i++) Value(e, 900 + i);
        e.SetHbForTesting(e.HeapTop);
        e.PushChoicePoint(0, 999);
        int outerB = e.B;

        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));
        e.PutAttr(x, ModB, Value(e, 51));
        Agrees(e, "posted");

        // Bind it, so the cell stops being an ATTVAR, then cut. The cut
        // discards the entry that would restore the cell, which is exactly
        // when the record may be reclaimed -- and it is reclaimed while it
        // still HOLDS attributes. Every other path empties the record first,
        // so this is the only one that exercises AttrDropRecord's own rows,
        // and it is the memory-hygiene path a long cut-only run depends on.
        e.SetHeap(x, Cell.Atom(60));

        e.PushChoicePoint(0, 998);
        e.Cut(outerB);

        Assert.False(e.AttrHasRecordForTesting(x));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModA));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "after the cut reclaimed it");
    }

    [Fact]
    public void BacktrackingPastThePromotion_DropsTheWholeRecordFromTheImage()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        e.SetHbForTesting(e.HeapTop);
        e.PushChoicePoint(0, 999);
        int binding = e.BindingTrailTop;
        int extra = e.ExtraTrailTop;

        // The variable becomes attributed ABOVE the choice point, so undoing
        // the promotion drops a record that still HOLDS its attributes --
        // AttrDropRecord's own path, which the del tests never reach because
        // there the record is already empty by the time it is dropped. This is
        // what a solver posting on a fresh variable and then failing does, so
        // it is the common case, not a corner.
        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));
        e.PutAttr(x, ModB, Value(e, 51));
        Agrees(e, "posted");

        e.UnwindTrails(binding, extra);

        Assert.Equal(-1, e.AttrMirrorLookup(x, ModA));
        Assert.Equal(-1, e.AttrMirrorLookup(x, ModB));
        Agrees(e, "after undoing the promotion");
    }

    [Fact]
    public void HeapGc_RekeysTheImage()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        // Garbage below the attributed variable, so the collector has to move
        // both its home and its attribute value rather than leaving them put.
        for (int i = 0; i < 64; i++) Value(e, 900 + i);
        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));

        e.CollectHeap();

        // The home moved, so the OLD key must no longer answer and the store's
        // new one must. Agreement alone would hold even on an empty image.
        Agrees(e, "after gc");
        Assert.Single(e.AttrTableKeysSnapshot());
        int moved = e.AttrTableKeysSnapshot()[0];
        Assert.NotEqual(-1, e.AttrMirrorLookup(moved, ModA));
    }

    [Fact]
    public void ManyVariables_GrowTheImageWithoutLosingARow()
    {
        var e = new Activation();
        e.AttrMirrorEnable();

        // Past the initial 64 slots several times over, so the image rebuilds
        // and every row has to survive the move.
        const int n = 500;
        var homes = new int[n];
        var values = new int[n];
        for (int i = 0; i < n; i++)
        {
            homes[i] = e.AllocateHeapUnbound();
            values[i] = Value(e, 100 + i);
            e.PutAttr(homes[i], ModA, values[i]);
        }

        for (int i = 0; i < n; i++)
            Assert.Equal(values[i], e.AttrMirrorLookup(homes[i], ModA));
        Agrees(e, "grown");

        // Deleting every other one leaves a tombstone between the survivors:
        // the probe has to walk past them, not stop.
        for (int i = 0; i < n; i += 2) e.DelAttr(homes[i], ModA);
        for (int i = 1; i < n; i += 2)
            Assert.Equal(values[i], e.AttrMirrorLookup(homes[i], ModA));
        Agrees(e, "tombstoned");
    }

    [Fact]
    public void EnablingLate_BuildsTheImageFromWhatIsAlreadyThere()
    {
        var e = new Activation();

        int x = e.AllocateHeapUnbound();
        int v = Value(e, 50);
        e.PutAttr(x, ModA, v);
        Assert.False(e.AttrMirrorEnabled);

        // A world attaches to an engine mid-run; the image starts from the
        // store rather than from the mutations it missed.
        e.AttrMirrorEnable();

        Assert.Equal(v, e.AttrMirrorLookup(x, ModA));
        Agrees(e, "enabled late");
    }

    [Fact]
    public void Disabled_CostsNothingAndClaimsNothing()
    {
        var e = new Activation();
        int x = e.AllocateHeapUnbound();
        e.PutAttr(x, ModA, Value(e, 50));

        Assert.False(e.AttrMirrorEnabled);
        Assert.Empty(e.AttrMirrorRows);
        // The checker must stay silent rather than report an empty image as a
        // disagreement, or every engine without a wasm world would look broken.
        Assert.Null(e.AttrMirrorDisagreement());
    }
}
