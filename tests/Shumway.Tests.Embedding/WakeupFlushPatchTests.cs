using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A Call/Execute dispatch tick runs the pending-wakeup flush BEFORE using its
/// operand — and the flush runs arbitrary goals. If a goal crosses a callee's
/// promotion threshold, the install path (OnCalleePromoted) rewrites that
/// callee's Call/Execute sites in place to CallIl/ExecuteIl with the FUNCTOR ID
/// as the operand — including the very site the tick is standing on. Reading
/// the operand after the flush then pairs the already-dispatched Call/Execute
/// opcode with the functor id and jumps to it as a bytecode address: garbage
/// decode (the clpz cross-query reserved_invalid / IndexOutOfRange crash).
/// The dispatch cases must read their operands before flushing.
/// </summary>
public class WakeupFlushPatchTests
{
    private const string Program =
        // The hook's returned goals warm the callee past the promotion
        // threshold DURING the flush that runs inside the trigger clause's
        // final Execute tick.
        "verify_attributes(m, _, _, [warmup, warmup, warmup]).\n" +
        "warmup :- tailpred(0).\n" +
        "tailpred(0).\n" +
        "tailpred(1) :- tailpred(0).\n" +
        "trigger(R) :- put_attr(V, m, a), V = b, tailpred(R).\n";

    [Fact]
    public void PromotionInstallDuringWakeupFlush_DoesNotTearTheExecuteSite()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 2;
        e.IlPromotion.BackgroundCompilation = false;   // install inside the flush
        e.ConsultString(Program);
        // The bind V = b queues the wakeup; the clause's last goal
        // (Execute tailpred) flushes it, the hook goals promote tailpred,
        // and the site under execution gets patched mid-tick.
        var s = e.Query("trigger(0).");
        Assert.True(s.Success);
        // Engine must still be sane afterwards.
        var s2 = e.Query("trigger(1).");
        Assert.True(s2.Success);
    }

    [Fact]
    public void PromotionInstallDuringWakeupFlush_NonTailCallSite()
    {
        // Same shape through a non-tail Call site (Call → CallIl repatch).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 2;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program +
            "trigger2(R) :- put_attr(V, m, a), V = b, tailpred(R), R == 0.\n");
        Assert.True(e.Query("trigger2(0).").Success);
        Assert.True(e.Query("trigger2(0).").Success);
    }
}
