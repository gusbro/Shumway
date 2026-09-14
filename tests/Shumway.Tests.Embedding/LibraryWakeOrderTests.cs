using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Two library bundles seeding one dynamic hook must both be
/// dispatchable, whichever loaded first.
///
/// <para>clpfd, clpr and coroutining each ship a `verify_attributes/4`
/// clause as a dynamic seed. The seed append bypassed the dynamic-store
/// mutation funnel (InvalidateDynamicCache), so the compiled trampoline's
/// first-argument switch never learned the second library's key: a
/// bound-module call missed the clause and FAILED while clause/2 saw it.
/// The wake driver calls with the module bound, so the SECOND library
/// loaded lost its suspensions silently — freeze/2 answered false after
/// clpfd-then-coroutining, and labeling answered false after
/// coroutining-then-clpfd. The unbound walk still found everything, which
/// is what made it a dispatch bug and not a store bug.</para></summary>
public sealed class LibraryWakeOrderTests
{
    private static PrologEngine Loaded(bool tier1, params string[] libs)
    {
        var e = new PrologEngine();
        if (tier1) e.IlPromotion.Threshold = 1;
        e.Query("true.");
        foreach (string lib in libs)
            Assert.True(e.Query($"use_module(library({lib})).").Success, lib);
        if (tier1)
        {
            var r = e.Query("compile_all(N), N > 0.");
            Assert.True(r.Success, "compile_all promoted nothing: Tier-1 not exercised");
        }
        return e;
    }

    private static void BothHooksDispatch(PrologEngine e)
    {
        // The wake driver's shape: module BOUND. This is the call that used
        // to miss the second library's clause.
        Assert.True(e.Query(
            "verify_attributes(clpfd, fd(a, b), _W, _G).").Success,
            "clpfd's hook is not dispatchable with the module bound");
        Assert.True(e.Query(
            "verify_attributes(coroutining, frozen(foo), 1, G), G == [foo].").Success,
            "coroutining's hook is not dispatchable with the module bound");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClpfdThenCoroutining_BothWake(bool tier1)
    {
        var e = Loaded(tier1, "clpfd", "coroutining");
        BothHooksDispatch(e);
        Assert.True(e.Query("freeze(Y, Z = w), Y = 1, Z == w.").Success,
            "freeze/2 did not wake");
        Assert.True(e.Query("when(nonvar(Y), Z = w), Y = 1, Z == w.").Success,
            "when/2 did not wake");
        Assert.True(e.Query("dif(A, B), A = 1, B = 2.").Success);
        Assert.False(e.Query("dif(A, B), A = 1, B = 1.").Success);
        Assert.True(e.Query("X in 1..3, X #> 2, label([X]), X == 3.").Success,
            "clpfd stopped labeling");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoroutiningThenClpfd_BothWake(bool tier1)
    {
        var e = Loaded(tier1, "coroutining", "clpfd");
        BothHooksDispatch(e);
        Assert.True(e.Query("freeze(Y, Z = w), Y = 1, Z == w.").Success);
        Assert.True(e.Query("X in 1..3, X #> 2, label([X]), X == 3.").Success,
            "clpfd, loaded second, stopped labeling");
    }

    [Fact]
    public void ClprThenCoroutining_Wakes()
    {
        var e = Loaded(tier1: false, "clpr", "coroutining");
        Assert.True(e.Query("freeze(Y, Z = w), Y = 1, Z == w.").Success);
        Assert.True(e.Query("{X + Y2 =:= 10, X - Y2 =:= 2}, X =:= 6.0.").Success,
            "clpr stopped solving");
    }

    [Fact]
    public void AllThree_EveryHookStillDispatches()
    {
        var e = Loaded(tier1: false, "clpfd", "clpr", "coroutining");
        BothHooksDispatch(e);
        Assert.True(e.Query("freeze(Y, Z = w), Y = 1, Z == w.").Success);
        Assert.True(e.Query("X in 1..3, X #> 2, label([X]), X == 3.").Success);
        Assert.True(e.Query("{A + B =:= 10, A - B =:= 2}, A =:= 6.0.").Success);
    }
}
