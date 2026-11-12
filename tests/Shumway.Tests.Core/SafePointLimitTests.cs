using System;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>A safe point compares the heap top with one limit
/// (<c>Activation.RearmSafePointLimit</c>). Whatever must stop a running
/// query lowers it: a heap far below the collection threshold still takes
/// the slow path, and the slow path does what was asked.</summary>
public class SafePointLimitTests
{
    // A fresh activation with a little heap, far below its threshold.
    private static Activation Quiet()
    {
        var e = new Activation(new ActivationConfig { GcThreshold = 1 << 20 });
        for (int i = 0; i < 10; i++) e.AllocateHeapUnbound();
        Assert.False(e.CallSafePointDue);
        return e;
    }

    [Fact]
    public void ACancellationReachesTheNextSafePoint()
    {
        var e = Quiet();
        e.RequestCancellation();
        Assert.True(e.CallSafePointDue);
        Assert.Throws<OperationCanceledException>(() => e.MaybeCollectHeapAtCall(
            FunctorTable.Intern(AtomTable.Intern("p", permanent: true).Id, 0)));
    }

    [Fact]
    public void AnExpiredDeadlineReachesTheNextSafePoint()
    {
        var e = Quiet();
        e.PushDeadline(0);
        Assert.True(e.CallSafePointDue);
        var ex = Assert.Throws<PrologRuntimeException>(() => e.MaybeCollectHeap());
        Assert.Equal("$timeout_expired", ex.Kind);
        // Popped, the limit is the threshold again.
        e.PopDeadline();
        Assert.False(e.CallSafePointDue);
    }

    [Fact]
    public void ARequestFromAnotherThreadRunsAtTheNextSafePointAndOnce()
    {
        var e = Quiet();
        int armed = 0;
        e.RequestDebugArm(_ => armed++);
        Assert.True(e.CallSafePointDue);
        e.MaybeCollectHeapAtReturn();
        Assert.Equal(1, armed);
        // Served: the limit is the threshold again.
        Assert.False(e.CallSafePointDue);
        e.MaybeCollectHeap();
        Assert.Equal(1, armed);
    }

    [Fact]
    public void TheThresholdItselfIsTheLimitWhileNothingElseIsPending()
    {
        var e = new Activation(new ActivationConfig { GcThreshold = 64 });
        for (int i = 0; i < e.RegisterCount; i++) e.SetRegister(i, Cell.Atom(0));
        while (e.HeapTop < 63) e.AllocateHeapUnbound();
        Assert.False(e.CallSafePointDue);
        e.AllocateHeapUnbound();
        Assert.True(e.CallSafePointDue);
        e.MaybeCollectHeap();
        Assert.Equal(1, e.HeapGcRuns);
        Assert.False(e.CallSafePointDue);
    }
}
