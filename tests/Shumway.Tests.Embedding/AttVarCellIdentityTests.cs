using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An ATTVAR cell is the one cell that names its own slot: the
/// payload is the home address, and that address is the key into the
/// attribute table. So the cell may be REFERENCED from anywhere but never
/// COPIED to another address — a copy is a second cell claiming a home that
/// is not its own, and the first lookup through it finds no record.
///
/// <para>The list-peeling helper handed the raw head and tail cells to its
/// callers, so any builtin that rebuilt a spine (append/3 does, cell by
/// cell) planted such a copy. The report was a KeyNotFoundException escaping
/// the engine from dif/2's trial unification, which was merely the first
/// thing to look the copy up.</para>
///
/// <para>The invariant itself is pinned as a unit test over the peeling
/// helper in Shumway.Tests.Core; these two are the reported goals, end to
/// end through the library that found it.</para></summary>
public class AttVarCellIdentityTests
{
    private static PrologEngine Coroutining()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        return e;
    }

    /// <summary>Runs bounded: the shapes here are non-terminating by nature
    /// (append/3 over two open lists enumerates for ever), so the assertion
    /// is about surviving the first stretch of the search, not about an
    /// answer. A crash was immediate; a regression would be too.</summary>
    private static void RunsWithoutCrashing(string goal, int seconds = 20)
    {
        Exception? failure = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                var e = Coroutining();
                foreach (var _ in e.QueryAll(goal)) break;
            }
            catch (System.Collections.Generic.KeyNotFoundException ex) { failure = ex; }
            catch (Exception ex) when (ex is not Shumway.Core.PrologRuntimeException)
            { failure = ex; }
        })
        { IsBackground = true };
        t.Start();
        t.Join(TimeSpan.FromSeconds(seconds));
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(
                $"`{goal}` escaped with {failure.GetType().Name}: {failure.Message}");
    }

    [Fact]
    public void TheReportedGoalDoesNotCrashTheEngine()
    {
        // The shape as reported: two open lists share a prefix, both watched
        // by one dif. append/3 rebuilds each spine, copying the watched
        // variable's cell; dif's trial unification then looked it up.
        RunsWithoutCrashing(
            "dif(Xs0,Ys0), append(Pr,Xs,Xs0), append(Pr,Ys,Ys0), Xs=[], Ys=[].");
    }

    [Fact]
    public void TheSameShapeWithTheBindingsInterleaved()
        => RunsWithoutCrashing(
            "dif(A,B), append(P,X,A), X=[], Y=[], append(P,Y,B).");
}
