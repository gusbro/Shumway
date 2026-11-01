using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A re-entrant solve on the live activation (a foreign predicate's SolveOnce,
/// the portray hook, a consult-time goal) reached from a continuation the host
/// resumed by asking for another solution. The interpreter's Backtrack entry
/// has to aim the solve at the running program as Run does; it used to leave
/// the view Run restored on its way out, so the second solution's print/1
/// crashed in the hook.
/// </summary>
public sealed class ReentrantSolveAfterBacktrackTests
{
    [Fact]
    public void ThePortrayHookRunsAgainOnASolutionReachedByBacktracking()
    {
        var output = new StringWriter();
        var e = new PrologEngine { Out = output };
        e.ConsultString("portray(X) :- integer(X), write(seen(X)).");
        var solutions = e.QueryAll("member(X, [1, 2]), print(X), nl.").ToList();
        Assert.Equal(2, solutions.Count);
        Assert.Equal("seen(1)\nseen(2)\n", output.ToString().Replace("\r\n", "\n"));
    }
}
