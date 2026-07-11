using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class UnifyTests
{
    // ---------- REF ↔ REF ----------

    [Fact]
    public void Unify_TwoUnboundVars_BindsYoungerToOlder()
    {
        var engine = new Activation();
        int older = engine.AllocateHeapUnbound();
        int younger = engine.AllocateHeapUnbound();

        Assert.True(engine.Unify(older, younger));

        Cell olderCell = engine.GetHeap(older);
        Cell youngerCell = engine.GetHeap(younger);

        Assert.Equal(Tag.Ref, olderCell.Tag);
        Assert.Equal(older, olderCell.AsHeapIndex);   // older still unbound
        Assert.Equal(Tag.Ref, youngerCell.Tag);
        Assert.Equal(older, youngerCell.AsHeapIndex); // younger -> older
    }

    [Fact]
    public void Unify_TwoUnboundVars_OrderIndependent()
    {
        var engine = new Activation();
        int older = engine.AllocateHeapUnbound();
        int younger = engine.AllocateHeapUnbound();

        // Argument order to Unify must not matter — young-to-old is determined by heap index.
        Assert.True(engine.Unify(younger, older));
        Assert.Equal(older, engine.GetHeap(younger).AsHeapIndex);
        Assert.Equal(older, engine.GetHeap(older).AsHeapIndex);
    }

    [Fact]
    public void Unify_SameVarIndex_TrueWithoutWork()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);
        Assert.True(engine.Unify(v, v));
        Assert.Equal(0, engine.BindingTrailTop);
        Assert.Equal(Cell.UnboundVar(v), engine.GetHeap(v));
    }

    // ---------- REF ↔ atomic value ----------

    [Fact]
    public void Unify_VarWithAtom_CopiesAtomIntoVar()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        int aSlot = engine.AllocateHeap(1);
        engine.SetHeap(aSlot, Cell.Atom(42));

        Assert.True(engine.Unify(v, aSlot));

        Assert.Equal(Cell.Atom(42), engine.GetHeap(v));
        Assert.Equal(Cell.Atom(42), engine.GetHeap(aSlot));
    }

    [Fact]
    public void Unify_AtomWithVar_BindsVarSameWay()
    {
        var engine = new Activation();
        int aSlot = engine.AllocateHeap(1);
        engine.SetHeap(aSlot, Cell.Atom(13));
        int v = engine.AllocateHeapUnbound();

        Assert.True(engine.Unify(aSlot, v));      // bound argument first
        Assert.Equal(Cell.Atom(13), engine.GetHeap(v));
    }

    [Fact]
    public void Unify_VarWithInt_CopiesInt()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();
        int iSlot = engine.AllocateHeap(1);
        engine.SetHeap(iSlot, Cell.Int(-1234));

        Assert.True(engine.Unify(v, iSlot));
        Assert.Equal(Cell.Int(-1234), engine.GetHeap(v));
        Assert.Equal(-1234, engine.GetHeap(v).AsInt);
    }

    [Fact]
    public void Unify_VarWithCompound_BindsAsRefToCompound()
    {
        // For STR / LIS / PSTR values the binding policy writes a REF, not a copy of
        // the STR cell, so the var becomes a pointer to the compound's heap position.
        var engine = new Activation();
        int s = engine.AllocateHeap(2);
        engine.SetHeap(s, Cell.Str(s + 1));
        engine.SetHeap(s + 1, Cell.Functor(99));
        int v = engine.AllocateHeapUnbound();

        Assert.True(engine.Unify(v, s));
        Cell vCell = engine.GetHeap(v);
        Assert.Equal(Tag.Ref, vCell.Tag);
        Assert.Equal(s, vCell.AsHeapIndex);
        Assert.Equal(s, engine.Deref(v));   // deref lands on the STR cell
    }

    // ---------- ATOM ↔ ATOM ----------

    [Fact]
    public void Unify_TwoIdenticalAtoms_Succeeds()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1);
        engine.SetHeap(a, Cell.Atom(7));
        int b = engine.AllocateHeap(1);
        engine.SetHeap(b, Cell.Atom(7));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_DifferentAtoms_Fails()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1);
        engine.SetHeap(a, Cell.Atom(7));
        int b = engine.AllocateHeap(1);
        engine.SetHeap(b, Cell.Atom(8));
        Assert.False(engine.Unify(a, b));
    }

    // ---------- INT ↔ INT ----------

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-42)]
    [InlineData(Cell.MaxInt60)]
    [InlineData(Cell.MinInt60)]
    public void Unify_TwoEqualInts_Succeeds(long value)
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Int(value));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, Cell.Int(value));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_DifferentInts_Fails()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Int(42));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, Cell.Int(43));
        Assert.False(engine.Unify(a, b));
    }

    // ---------- Mismatched tags ----------

    [Fact]
    public void Unify_AtomVsInt_Fails()
    {
        var engine = new Activation();
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Atom(0));
        int b = engine.AllocateHeap(1); engine.SetHeap(b, Cell.Int(0));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_AtomVsCompound_Fails()
    {
        var engine = new Activation();
        int s = engine.AllocateHeap(2);
        engine.SetHeap(s, Cell.Str(s + 1));
        engine.SetHeap(s + 1, Cell.Functor(0));
        int a = engine.AllocateHeap(1); engine.SetHeap(a, Cell.Atom(0));
        Assert.False(engine.Unify(a, s));
    }

    // ---------- Deref chains ----------

    [Fact]
    public void Unify_FollowsRefChainsOnBothSides()
    {
        var engine = new Activation();
        int chainEnd = engine.AllocateHeap(1);
        engine.SetHeap(chainEnd, Cell.Atom(7));
        int mid = engine.AllocateHeap(1);
        engine.SetHeap(mid, Cell.Ref(chainEnd));
        int start = engine.AllocateHeap(1);
        engine.SetHeap(start, Cell.Ref(mid));
        // start -> mid -> chainEnd (Atom 7)

        int rhsMatch = engine.AllocateHeap(1);
        engine.SetHeap(rhsMatch, Cell.Atom(7));
        Assert.True(engine.Unify(start, rhsMatch));

        int rhsMiss = engine.AllocateHeap(1);
        engine.SetHeap(rhsMiss, Cell.Atom(8));
        Assert.False(engine.Unify(start, rhsMiss));
    }

    // ---------- Trail interaction ----------

    [Fact]
    public void Unify_DoesNotTrailWhenVarIsYoungerThanHb()
    {
        var engine = new Activation();
        // Hb left at 0 — every var is young.
        int v = engine.AllocateHeapUnbound();
        int aSlot = engine.AllocateHeap(1);
        engine.SetHeap(aSlot, Cell.Atom(1));
        Assert.True(engine.Unify(v, aSlot));
        Assert.Equal(0, engine.BindingTrailTop);
    }

    [Fact]
    public void Unify_TrailsWhenVarIsOlderThanHb()
    {
        var engine = new Activation();
        int v = engine.AllocateHeapUnbound();           // idx 0
        int aSlot = engine.AllocateHeap(1);             // idx 1
        engine.SetHeap(aSlot, Cell.Atom(99));
        engine.SetHbForTesting(engine.HeapTop);         // HB = 2; v (0) is "old"

        Assert.True(engine.Unify(v, aSlot));
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(v, engine.BindingTrailSpan[0]);
    }

    [Fact]
    public void Unify_TwoOldVars_OnlyTheBoundOneTrails()
    {
        var engine = new Activation();
        int older = engine.AllocateHeapUnbound();
        int younger = engine.AllocateHeapUnbound();
        engine.SetHbForTesting(engine.HeapTop);

        Assert.True(engine.Unify(older, younger));
        // Younger was bound to older; trail must record the younger's heap idx.
        Assert.Equal(1, engine.BindingTrailTop);
        Assert.Equal(younger, engine.BindingTrailSpan[0]);
    }

    [Fact]
    public void Unify_AfterFailure_TrailUnwindRestoresOriginalState()
    {
        // A composite scenario: a successful unify writes to the heap; if a later
        // unify fails, the caller can roll back via UnwindBindingTrail.
        var engine = new Activation();
        int v1 = engine.AllocateHeapUnbound();
        int v2 = engine.AllocateHeapUnbound();
        int a7 = engine.AllocateHeap(1); engine.SetHeap(a7, Cell.Atom(7));
        int a8 = engine.AllocateHeap(1); engine.SetHeap(a8, Cell.Atom(8));
        engine.SetHbForTesting(engine.HeapTop);

        int mark = engine.BindingTrailTop;
        Assert.True(engine.Unify(v1, a7));
        Assert.True(engine.Unify(v2, a8));
        Assert.False(engine.Unify(v1, a8));     // v1 is now Atom(7), can't unify with Atom(8)

        engine.UnwindBindingTrail(mark);
        Assert.Equal(Cell.UnboundVar(v1), engine.GetHeap(v1));
        Assert.Equal(Cell.UnboundVar(v2), engine.GetHeap(v2));
    }

    // STR and LIS unification are exercised in CompoundUnifyTests.
}
