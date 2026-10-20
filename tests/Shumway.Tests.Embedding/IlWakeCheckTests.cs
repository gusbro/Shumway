using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The wake check a goal boundary makes (<c>HasPendingWakeups</c>)
/// is emitted as a read of the queue's count through the activation's
/// field, not as a call of the getter: nothing for the JIT to inline, no
/// temporaries against its cap on locals. Exclusive: the IL dump path is
/// one for the process, and another class compiling meanwhile would write
/// to this test's file.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlWakeCheckTests
{
    [Fact]
    public void TheWakeCheckReadsTheQueueCountInPlace()
    {
        string dump = Path.Combine(Path.GetTempPath(), $"shumway-wake-{Guid.NewGuid():N}.txt");
        string? prev = IlPredicateCompiler.IlDumpPath;
        try
        {
            IlPredicateCompiler.IlDumpPath = dump;
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 1;
            e.IlPromotion.BackgroundCompilation = false;
            // A cut after a builtin call: the commit checks for pending wakes,
            // and so does the boundary after the call.
            e.ConsultString("wk(X, Y) :- atom(X), !, Y = X.\nwk(_, none).\n");
            Assert.True(e.Query("wk(a, Y).").Success);
            Assert.True(e.Query("wk(a, Y).").Success);
            e.IlPromotion.WaitForPendingPromotions();
            var q = e.Query("wk(1, Y).");
            Assert.True(q.Success);
            Assert.Equal("none", q["Y"]!.ToString());
        }
        finally
        {
            IlPredicateCompiler.IlDumpPath = prev;
        }
        string text = File.ReadAllText(dump);
        File.Delete(dump);
        Assert.Contains("_pendingWakeups", text);
        Assert.Contains("Count", text);
        Assert.DoesNotContain("get_HasPendingWakeups", text);
    }
}
