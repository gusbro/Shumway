using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class CompoundUnifyTests
{
    // FunctorTable is global, but interning the same (atomId, arity) always returns the
    // same id, so test ordering doesn't affect correctness. Each test interns the
    // functors it needs locally.

    // ---------- STR ↔ STR ----------

    [Fact]
    public void Unify_CompoundsSameFunctorMatchingAtomArgs_Succeeds()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        int a = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        int b = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_CompoundsSameFunctorMismatchedArg_Fails()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        int a = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        int b = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(999));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_CompoundsDifferentFunctorName_Fails()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);
        int bar2 = FunctorTable.Intern(atomId: 200, arity: 2);

        int a = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        int b = BuildCompound(engine, bar2, Cell.Atom(1), Cell.Atom(2));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_CompoundsSameNameDifferentArity_Fails()
    {
        var engine = new Activation();
        // foo/2 and foo/3 are distinct functors — FunctorTable.Intern keys on the pair.
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);
        int foo3 = FunctorTable.Intern(atomId: 100, arity: 3);
        Assert.NotEqual(foo2, foo3);

        int a = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        int b = BuildCompound(engine, foo3, Cell.Atom(1), Cell.Atom(2), Cell.Atom(3));
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_CompoundWithVarArg_BindsVar()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        // foo(X, atom(2)) where X is inline-unbound at heap[xPos]
        int a = engine.AllocateHeap(4);
        engine.SetHeap(a, Cell.Str(a + 1));
        engine.SetHeap(a + 1, Cell.Functor(foo2));
        engine.SetHeap(a + 2, Cell.UnboundVar(a + 2));      // X inline
        engine.SetHeap(a + 3, Cell.Atom(2));
        int xPos = a + 2;

        int b = BuildCompound(engine, foo2, Cell.Atom(42), Cell.Atom(2));
        Assert.True(engine.Unify(a, b));
        Assert.Equal(Cell.Atom(42), engine.GetHeap(xPos));
    }

    [Fact]
    public void Unify_CompoundsWithMatchingVarArgs_BindsBothToSameTarget()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        // foo(X, X) — both arg slots reference the same var via REF.
        int xPos = engine.AllocateHeap(1);
        engine.SetHeap(xPos, Cell.UnboundVar(xPos));

        int a = engine.AllocateHeap(4);
        engine.SetHeap(a, Cell.Str(a + 1));
        engine.SetHeap(a + 1, Cell.Functor(foo2));
        engine.SetHeap(a + 2, Cell.Ref(xPos));
        engine.SetHeap(a + 3, Cell.Ref(xPos));

        // foo(atom(7), atom(7))
        int b = BuildCompound(engine, foo2, Cell.Atom(7), Cell.Atom(7));

        Assert.True(engine.Unify(a, b));
        Assert.Equal(Cell.Atom(7), engine.GetHeap(xPos));
    }

    [Fact]
    public void Unify_CompoundsWithVarSharedFails_WhenTargetsDisagree()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        // foo(X, X)
        int xPos = engine.AllocateHeap(1);
        engine.SetHeap(xPos, Cell.UnboundVar(xPos));
        int a = engine.AllocateHeap(4);
        engine.SetHeap(a, Cell.Str(a + 1));
        engine.SetHeap(a + 1, Cell.Functor(foo2));
        engine.SetHeap(a + 2, Cell.Ref(xPos));
        engine.SetHeap(a + 3, Cell.Ref(xPos));

        // foo(atom(7), atom(8)) — X can't be both 7 and 8.
        int b = BuildCompound(engine, foo2, Cell.Atom(7), Cell.Atom(8));

        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_NestedCompoundsStructurallyEqual_Succeeds()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);
        int bar1 = FunctorTable.Intern(atomId: 200, arity: 1);

        // foo(bar(1), 99)
        int innerA = BuildCompound(engine, bar1, Cell.Int(1));
        int a = BuildCompound(engine, foo2, Cell.Ref(innerA), Cell.Int(99));

        int innerB = BuildCompound(engine, bar1, Cell.Int(1));
        int b = BuildCompound(engine, foo2, Cell.Ref(innerB), Cell.Int(99));

        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_NestedCompoundsInnerMismatch_Fails()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);
        int bar1 = FunctorTable.Intern(atomId: 200, arity: 1);

        int innerA = BuildCompound(engine, bar1, Cell.Int(1));
        int a = BuildCompound(engine, foo2, Cell.Ref(innerA), Cell.Int(99));

        int innerB = BuildCompound(engine, bar1, Cell.Int(2));   // different inner value
        int b = BuildCompound(engine, foo2, Cell.Ref(innerB), Cell.Int(99));

        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_CompoundsAfterPartialMatchThenFail_LeavesBindingsForUnwind()
    {
        // Demonstrates the WAM convention: Unify makes bindings as it goes; on overall
        // failure, the caller is responsible for trail unwind back to the pre-unify mark.
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);

        // foo(X, atom(100))
        int xPos = engine.AllocateHeap(1);
        engine.SetHeap(xPos, Cell.UnboundVar(xPos));
        int a = engine.AllocateHeap(4);
        engine.SetHeap(a, Cell.Str(a + 1));
        engine.SetHeap(a + 1, Cell.Functor(foo2));
        engine.SetHeap(a + 2, Cell.Ref(xPos));
        engine.SetHeap(a + 3, Cell.Atom(100));

        // foo(atom(7), atom(101)) — arg 1 will unify, arg 2 won't.
        int b = BuildCompound(engine, foo2, Cell.Atom(7), Cell.Atom(101));

        engine.SetHbForTesting(engine.HeapTop);   // make X "old" so the binding is trailed
        int mark = engine.BindingTrailTop;

        Assert.False(engine.Unify(a, b));

        // X is still visibly bound to atom(7) until the caller unwinds.
        Assert.Equal(Cell.Atom(7), engine.GetHeap(xPos));
        Assert.Equal(mark + 1, engine.BindingTrailTop);

        engine.UnwindBindingTrail(mark);
        Assert.Equal(Cell.UnboundVar(xPos), engine.GetHeap(xPos));
    }

    [Fact]
    public void Unify_TwoStrsDerefToSameAddress_TrueViaShortCircuit()
    {
        var engine = new Activation();
        int foo2 = FunctorTable.Intern(atomId: 100, arity: 2);
        int s = BuildCompound(engine, foo2, Cell.Atom(1), Cell.Atom(2));
        // A REF cell pointing at the STR — Deref normalises both arguments to `s`.
        int alias = engine.AllocateHeap(1);
        engine.SetHeap(alias, Cell.Ref(s));

        Assert.True(engine.Unify(s, alias));
    }

    // ---------- LIS ↔ LIS ----------

    [Fact]
    public void Unify_TwoIdenticalLists_Succeeds()
    {
        var engine = new Activation();
        int a = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2), Cell.Atom(3) });
        int b = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2), Cell.Atom(3) });
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ListsDifferAtFirstElement_Fails()
    {
        var engine = new Activation();
        int a = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2) });
        int b = BuildList(engine, new[] { Cell.Atom(99), Cell.Atom(2) });
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ListsDifferAtLaterElement_Fails()
    {
        var engine = new Activation();
        int a = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2), Cell.Atom(3) });
        int b = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2), Cell.Atom(99) });
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ListsOfDifferentLengths_Fails()
    {
        var engine = new Activation();
        // [1, 2] tail is a LIS cell; [1] tail is the [] atom — different tags, no match.
        int a = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2) });
        int b = BuildList(engine, new[] { Cell.Atom(1) });
        Assert.False(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_ListWithVarHead_BindsVar()
    {
        var engine = new Activation();

        // Build [X, atom(2)] with the var inlined as the head.
        int a = engine.AllocateHeap(5);
        engine.SetHeap(a, Cell.Lis(a + 1));
        engine.SetHeap(a + 1, Cell.UnboundVar(a + 1));
        engine.SetHeap(a + 2, Cell.Lis(a + 3));
        engine.SetHeap(a + 3, Cell.Atom(2));
        engine.SetHeap(a + 4, Cell.Atom(AtomTable.EmptyListId));
        int xPos = a + 1;

        int b = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2) });

        Assert.True(engine.Unify(a, b));
        Assert.Equal(Cell.Atom(1), engine.GetHeap(xPos));
    }

    [Fact]
    public void Unify_ListWithVarTail_BindsTail()
    {
        var engine = new Activation();
        // [1 | T] where T is an unbound var inlined as the tail.
        int a = engine.AllocateHeap(3);
        engine.SetHeap(a, Cell.Lis(a + 1));
        engine.SetHeap(a + 1, Cell.Atom(1));
        engine.SetHeap(a + 2, Cell.UnboundVar(a + 2));
        int tPos = a + 2;

        int b = BuildList(engine, new[] { Cell.Atom(1), Cell.Atom(2) });

        Assert.True(engine.Unify(a, b));
        // T should now point at the rest of B, i.e., a LIS containing [2].
        int tDeref = engine.Deref(tPos);
        Assert.Equal(Tag.Lis, engine.GetHeap(tDeref).Tag);
    }

    [Fact]
    public void Unify_EmptyListAtomVsEmptyListAtom_Succeeds()
    {
        var engine = new Activation();
        // [] is just an atom, so unifying [] with [] is plain atom equality.
        int a = engine.AllocateHeap(1);
        engine.SetHeap(a, Cell.Atom(AtomTable.EmptyListId));
        int b = engine.AllocateHeap(1);
        engine.SetHeap(b, Cell.Atom(AtomTable.EmptyListId));
        Assert.True(engine.Unify(a, b));
    }

    [Fact]
    public void Unify_LisVsAtom_Fails()
    {
        var engine = new Activation();
        int l = BuildList(engine, new[] { Cell.Atom(1) });
        int atomSlot = engine.AllocateHeap(1);
        engine.SetHeap(atomSlot, Cell.Atom(99));
        Assert.False(engine.Unify(l, atomSlot));
    }

    // ---------- Helpers ----------

    /// <summary>Lays out a compound term as STR + FUNCTOR + args contiguously. Returns
    /// the heap index of the STR cell.</summary>
    private static int BuildCompound(Activation engine, int functorId, params Cell[] args)
    {
        int s = engine.AllocateHeap(2 + args.Length);
        engine.SetHeap(s, Cell.Str(s + 1));
        engine.SetHeap(s + 1, Cell.Functor(functorId));
        for (int i = 0; i < args.Length; i++)
            engine.SetHeap(s + 2 + i, args[i]);
        return s;
    }

    /// <summary>Lays out a proper list <c>[e0, e1, ...]</c> with the supplied elements as
    /// head cells, terminated by the empty-list atom. Returns the heap index of the
    /// outermost LIS cell.</summary>
    private static int BuildList(Activation engine, Cell[] elements)
    {
        if (elements.Length == 0)
        {
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Atom(AtomTable.EmptyListId));
            return slot;
        }

        int start = engine.AllocateHeap(2 * elements.Length + 1);
        for (int i = 0; i < elements.Length; i++)
        {
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));   // tail is at lisIdx + 2
            engine.SetHeap(lisIdx + 1, elements[i]);
        }
        engine.SetHeap(start + 2 * elements.Length, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }
}
