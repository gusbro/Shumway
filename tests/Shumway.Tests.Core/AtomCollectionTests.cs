using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-003: a requested collection waits for every claimant to mark. An idle
/// claimant is marked on the requesting thread, a running one at its next safe
/// point or as it stops, so a claimant that does not run never holds it back.
/// Dead ids are poisoned here: an atom a mark missed fails where it is read.
/// </summary>
public class AtomCollectionTests
{
    public AtomCollectionTests()
    {
        AtomTable.ResetForTesting();
        AtomTable.PoisonDeadIds = true;
    }

    private static Activation NewActivation(Activation? outer = null)
    {
        var e = new Activation();
        for (int i = 0; i < e.RegisterCount; i++) e.SetRegister(i, Cell.Atom(0));
        e.JoinAtomDomain(outer);
        return e;
    }

    private static int Fresh(string name) => AtomTable.Intern(name).Id;

    private static bool Dead(int id) => AtomTable.ProbeById(id) is null;

    private sealed class Blocking : IAtomHolder
    {
        private readonly ManualResetEventSlim _inside, _gate;
        public Blocking(ManualResetEventSlim inside, ManualResetEventSlim gate) { _inside = inside; _gate = gate; }
        public void MarkAtoms(AtomMarkSet set)
        {
            _inside.Set();
            _gate.Wait();
        }
    }

    [Fact]
    public void AnIdleClaimantIsMarkedByTheCollection()
    {
        var idle = NewActivation();
        int held = Fresh("held_by_idle"), loose = Fresh("loose");
        idle.SetRegister(0, Cell.Atom(held));

        AtomTable.RequestCollection();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal("held_by_idle", AtomTable.GetById(held)!.Name);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        Assert.True(Dead(loose));
    }

    [Fact]
    public void AClaimantThatHasNotRunSinceItsLastMarkIsNotScannedAgain()
    {
        var idle = NewActivation();
        int held = Fresh("held");
        idle.SetRegister(0, Cell.Atom(held));
        AtomTable.RequestCollection();
        Assert.Equal(1, idle.AtomDomain!.Claimant.HeldScans);

        AtomTable.RequestCollection();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, idle.AtomDomain.Claimant.HeldScans);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
    }

    [Fact]
    public void AClaimantThatRanSinceItsLastMarkIsScannedAgain()
    {
        var e = NewActivation();
        AtomTable.RequestCollection();
        e.EnterAtomRun();
        int taken = Fresh("taken_in_a_run");
        e.SetRegister(1, Cell.Atom(taken));
        e.LeaveAtomRun(completed: true);

        AtomTable.RequestCollection();

        Assert.Equal(2, e.AtomDomain!.Claimant.HeldScans);
        Assert.Equal(1, AtomTable.ClaimsOf(taken));
    }

    [Fact]
    public void ARunningClaimantMarksAtItsNextSafePoint()
    {
        var running = NewActivation();
        int held = Fresh("held_while_running"), loose = Fresh("loose");
        running.SetRegister(0, Cell.Atom(held));
        running.EnterAtomRun();

        AtomTable.RequestCollection();
        Assert.True(AtomTable.CollectionPending);
        Assert.False(Dead(loose));

        running.MaybeCollectHeap();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, running.AtomTracesAlone);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        Assert.True(Dead(loose));
        running.LeaveAtomRun(completed: true);
    }

    [Fact]
    public void AHeapCollectionDueAtTheSafePointMarksInItsOwnTrace()
    {
        var e = new Activation(new ActivationConfig { GcThreshold = 64 });
        for (int i = 0; i < e.RegisterCount; i++) e.SetRegister(i, Cell.Atom(0));
        e.JoinAtomDomain(null);
        int held = Fresh("held_in_a_collection");
        int f = e.AllocateHeap(2);
        e.SetHeap(f, Cell.Functor(FunctorTable.Intern(held, 1)));
        e.SetHeap(f + 1, Cell.Int(1));
        e.SetRegister(0, Cell.Str(f));
        for (int i = 0; i < 100; i++) e.SetHeap(e.AllocateHeap(1), Cell.Int(i));
        e.EnterAtomRun();
        AtomTable.RequestCollection();

        e.MaybeCollectHeap();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, e.HeapGcRuns);
        Assert.Equal(0, e.AtomTracesAlone);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        e.LeaveAtomRun(completed: true);
    }

    [Fact]
    public void ARunningClaimantThatStopsFirstMarksAsItStops()
    {
        var running = NewActivation();
        int held = Fresh("held_at_stop");
        running.SetRegister(0, Cell.Atom(held));
        running.EnterAtomRun();
        AtomTable.RequestCollection();

        running.LeaveAtomRun(completed: true);

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
    }

    [Fact]
    public void ARunEndedByAnExceptionLeavesItsMarkToTheNextRequest()
    {
        var running = NewActivation();
        int held = Fresh("held_after_throw");
        running.SetRegister(0, Cell.Atom(held));
        running.EnterAtomRun();
        AtomTable.RequestCollection();

        running.LeaveAtomRun(completed: false);
        Assert.True(AtomTable.CollectionPending);

        AtomTable.RequestCollection();
        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
    }

    [Fact]
    public void AFailureDrivenLoopMarksAtTheSafePointOfItsRetries()
    {
        var looping = NewActivation();
        int held = Fresh("held_by_loop");
        looping.SetRegister(0, Cell.Atom(held));
        looping.EnterAtomRun();
        AtomTable.RequestCollection();

        for (int i = 0; i < 4096 && AtomTable.CollectionPending; i++)
            looping.RetrySafePoint();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, AtomTable.ClaimsOf(held));
        looping.LeaveAtomRun(completed: true);
    }

    /// <summary>Compiled code polls with heap indices and registers in its locals,
    /// where a trace cannot see them.</summary>
    [Fact]
    public void TheBacktrackPollOfCompiledCodeLeavesTheMarkAlone()
    {
        var looping = NewActivation();
        looping.EnterAtomRun();
        AtomTable.RequestCollection();

        for (int i = 0; i < 2 * 4096; i++)
            looping.BacktrackSafePoint();

        Assert.True(AtomTable.CollectionPending);
        Assert.Equal(0, looping.AtomTracesAlone);
        looping.LeaveAtomRun(completed: true);
        Assert.False(AtomTable.CollectionPending);
    }

    [Fact]
    public void TheRunningMemberOfANestedDomainMarksTheOthers()
    {
        var outer = NewActivation();
        int outerAtom = Fresh("outer_atom"), innerAtom = Fresh("inner_atom");
        outer.SetRegister(0, Cell.Atom(outerAtom));
        outer.EnterAtomRun();
        var inner = NewActivation(outer);
        inner.SetRegister(0, Cell.Atom(innerAtom));
        inner.EnterAtomRun();

        AtomTable.RequestCollection();
        inner.MaybeCollectHeap();

        Assert.False(AtomTable.CollectionPending);
        Assert.Equal(1, AtomTable.ClaimsOf(outerAtom));
        Assert.Equal(1, AtomTable.ClaimsOf(innerAtom));
        inner.LeaveAtomRun(completed: true);
        Assert.True(outer.AtomDomain!.Claimant.IsRunning);
        outer.LeaveAtomRun(completed: true);
        Assert.False(outer.AtomDomain.Claimant.IsRunning);
    }

    [Fact]
    public void AClaimantThatLeavesCompletesTheCollection()
    {
        var running = NewActivation();
        running.EnterAtomRun();
        AtomTable.RequestCollection();
        running.LeaveAtomRun(completed: false);
        Assert.True(AtomTable.CollectionPending);

        running.LeaveAtomDomain();

        Assert.False(AtomTable.CollectionPending);
    }

    [Fact]
    public async Task ARunWaitsWhileTheCollectionMarksItsClaimant()
    {
        using var inside = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var e = NewActivation();
        e.SetRegister(0, e.MakeForeign(new Blocking(inside, gate)));
        var collecting = Task.Run(AtomTable.RequestCollection);
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(30)), "the collection never marked the claimant");

            using var entered = new ManualResetEventSlim();
            var entering = Task.Run(() =>
            {
                e.EnterAtomRun();
                entered.Set();
                e.LeaveAtomRun(completed: false);
            });
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(200)), "the run did not wait for the mark");
            gate.Set();
            await entering.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(entered.IsSet);
        }
        finally
        {
            gate.Set();
            await collecting;
        }
        Assert.False(AtomTable.CollectionPending);
    }
}
