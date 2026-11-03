using System.Runtime.CompilerServices;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-003: an activation's mark claims every transient atom it reaches, in cells
/// and in the places that keep atom ids outside cells, and only those. Each test
/// puts an atom in one place alone; with that place left out of the mark, its
/// claim count stays at zero.
/// </summary>
public class AtomClaimTests
{
    public AtomClaimTests() => AtomTable.ResetForTesting();

    private static Activation NewActivation(Activation? outer = null)
    {
        var e = new Activation();
        for (int i = 0; i < e.RegisterCount; i++) e.SetRegister(i, Cell.Atom(0));
        e.JoinAtomDomain(outer);
        return e;
    }

    private static int Fresh(string name) => AtomTable.Intern(name).Id;

    private sealed class Holder : IAtomHolder
    {
        private readonly int _atomId;
        public Holder(int atomId) => _atomId = atomId;
        public bool Resume(Activation e, int cursor) => false;
        public void MarkAtoms(AtomMarkSet set) => set.Add(_atomId);
    }

    private sealed class Keyed : IExternalTrailTarget
    {
        public void RestoreExternal(int key, Cell oldValue, bool hadOldValue) { }
    }

    [Fact]
    public void ACompoundRootedInARegisterClaimsItsArgumentAndItsName()
    {
        var e = NewActivation();
        int arg = Fresh("arg_held"), name = Fresh("name_held"), loose = Fresh("loose");
        int f = e.AllocateHeap(2);
        e.SetHeap(f, Cell.Functor(FunctorTable.Intern(name, 1)));
        e.SetHeap(f + 1, Cell.Atom(arg));
        int g = e.AllocateHeap(2);
        e.SetHeap(g, Cell.Functor(FunctorTable.Intern(name, 1)));
        e.SetHeap(g + 1, Cell.Atom(loose));
        e.SetRegister(0, Cell.Str(f));

        Assert.True(e.MarkAtomDomain());

        Assert.Equal(1, AtomTable.ClaimsOf(arg));
        Assert.Equal(1, AtomTable.ClaimsOf(name));
        Assert.Equal(0, AtomTable.ClaimsOf(loose));
    }

    [Fact]
    public void ARegisterClaimsItsAtom()
    {
        var e = NewActivation();
        int held = Fresh("in_register");
        e.SetRegister(3, Cell.Atom(held));

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(held));
    }

    [Fact]
    public void AnAttributeClaimsItsModuleWhileItsVariableIsReached()
    {
        var e = NewActivation();
        int module = Fresh("attr_module"), dropped = Fresh("dropped_module");
        int v = e.AllocateHeapUnbound(), w = e.AllocateHeapUnbound();
        int value = e.AllocateHeap(1);
        e.SetHeap(value, Cell.Int(7));
        e.PutAttr(v, module, value);
        e.PutAttr(w, dropped, value);
        e.SetRegister(0, Cell.Ref(v));

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(module));
        Assert.Equal(0, AtomTable.ClaimsOf(dropped));
    }

    [Fact]
    public void TheAttributeTrailLogClaimsItsModule()
    {
        var e = NewActivation();
        int module = Fresh("logged_module");
        int v = e.AllocateHeapUnbound();
        int value = e.AllocateHeap(1);
        e.SetHeap(value, Cell.Int(1));
        e.SetHb(e.HeapTop);
        e.PutAttr(v, module, value);
        e.DelAttr(v, module);

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(module));
    }

    [Fact]
    public void AWakeupClaimsItsModule()
    {
        var e = NewActivation();
        int module = Fresh("woken_module");
        int v = e.AllocateHeapUnbound();
        int value = e.AllocateHeap(1);
        e.SetHeap(value, Cell.Int(1));
        e.PutAttr(v, module, value);
        int other = e.AllocateHeap(1);
        e.SetHeap(other, Cell.Int(2));
        Assert.True(e.Unify(v, other));
        Assert.True(e.PendingWakeupCount > 0);

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(module));
    }

    [Fact]
    public void AnExternalTrailEntryClaimsItsKey()
    {
        var e = NewActivation();
        int key = Fresh("global_key");
        e.TrailExternal(new Keyed(), key, Cell.Int(0), hadOldValue: true);

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(key));
    }

    [Fact]
    public void ASolutionBufferInProgressClaimsItsAtoms()
    {
        var e = NewActivation();
        int found = Fresh("found_solution");
        var buffer = e.NewSolutions();
        buffer.AddToList(e, Cell.Atom(found));
        e.PushSolutions(buffer);

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(found));
    }

    [Fact]
    public void ABuiltinChoicePointCursorClaimsItsAtoms()
    {
        var e = NewActivation();
        int cursorAtom = Fresh("cursor_atom");
        var holder = new Holder(cursorAtom);
        e.PushBuiltinChoicePoint(holder.Resume, arity: 0);

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(cursorAtom));
    }

    [Fact]
    public void AForeignObjectClaimsItsAtoms()
    {
        var e = NewActivation();
        int foreignAtom = Fresh("foreign_atom");
        e.SetRegister(0, e.MakeForeign(new Holder(foreignAtom)));

        e.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(foreignAtom));
    }

    [Fact]
    public void ANestedActivationMarksTheOneItRunsInside()
    {
        var outer = NewActivation();
        int outerAtom = Fresh("outer_atom"), innerAtom = Fresh("inner_atom");
        outer.SetRegister(0, Cell.Atom(outerAtom));
        var inner = NewActivation(outer);
        inner.SetRegister(0, Cell.Atom(innerAtom));

        Assert.Same(outer.AtomDomain, inner.AtomDomain);
        inner.MarkAtomDomain();

        Assert.Equal(1, AtomTable.ClaimsOf(outerAtom));
        Assert.Equal(1, AtomTable.ClaimsOf(innerAtom));
        Assert.Equal(1, AtomTable.ClaimantCount);
    }

    [Fact]
    public void TheLastActivationOutGivesUpTheDomainsClaims()
    {
        var outer = NewActivation();
        int held = Fresh("held_by_domain");
        outer.SetRegister(0, Cell.Atom(held));
        var inner = NewActivation(outer);
        inner.MarkAtomDomain();

        inner.LeaveAtomDomain();
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        outer.LeaveAtomDomain();

        Assert.Equal(0, AtomTable.ClaimsOf(held));
        Assert.Equal(0, AtomTable.ClaimantCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MarkAndAbandon()
    {
        var e = NewActivation();
        int held = Fresh("abandoned_atom");
        e.SetRegister(0, Cell.Atom(held));
        e.MarkAtomDomain();
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        return held;
    }

    [Fact]
    public void AnAbandonedDomainIsReleasedByTheNextCollection()
    {
        int held = MarkAndAbandon();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AtomTable.Collect();

        Assert.Equal(0, AtomTable.ClaimantCount);
        Assert.Null(AtomTable.GetById(held));
    }

    [Fact]
    public void AnAtomTakenSinceAClaimantsLastMarkSurvivesUntilItMarksAgain()
    {
        var taker = NewActivation();
        var other = NewActivation();
        taker.MarkAtomDomain();
        int taken = Fresh("taken_since");
        other.MarkAtomDomain();

        AtomTable.Collect();
        Assert.NotNull(AtomTable.GetById(taken));

        taker.MarkAtomDomain();
        AtomTable.Collect();
        Assert.Null(AtomTable.GetById(taken));
    }

    [Fact]
    public void AnAtomAClaimantStillHoldsSurvivesItsOtherClaimantsMarks()
    {
        var holder = NewActivation();
        var other = NewActivation();
        int held = Fresh("still_held");
        holder.SetRegister(0, Cell.Atom(held));
        holder.MarkAtomDomain();
        other.MarkAtomDomain();
        holder.MarkAtomDomain();

        AtomTable.Collect();

        Assert.NotNull(AtomTable.GetById(held));
        Assert.Equal(1, AtomTable.ClaimsOf(held));
    }
}
