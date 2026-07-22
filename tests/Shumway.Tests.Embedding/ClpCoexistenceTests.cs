using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// CLP(FD) and CLP(R) coexist on one engine: both declare their
/// verify_attributes/4 hook :- multifile, and the hook's first argument
/// (the attribute module) dispatches each wakeup to the right library.
/// Regression for the multifile clause-context bug: a multifile clause is
/// module-rewritten at consult under its ORIGIN module, so clpfd's hook
/// body still reaches clpfd's module-locals after clpr also loads.
/// </summary>
public class ClpCoexistenceTests
{
    private static PrologEngine BothLibraries(bool fdFirst = true)
    {
        var e = new PrologEngine();
        if (fdFirst) { e.UseClpfd(); e.UseClpr(); }
        else { e.UseClpr(); e.UseClpfd(); }
        return e;
    }

    [Fact]
    public void FdConstraintsWork_WithClprLoaded()
    {
        var e = BothLibraries();
        var sols = e.QueryAll("X #> 3, X #< 6, label([X]).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal("4", sols[0]["X"]!.ToString());
        Assert.Equal("5", sols[1]["X"]!.ToString());
    }

    [Fact]
    public void ClprConstraintsWork_WithClpfdLoaded()
    {
        var e = BothLibraries();
        var sol = e.Query("{Z = 2.0 * W, W = 1.5}.");
        Assert.True(sol.Success);
        Assert.Equal("3", sol["Z"]!.ToString());
    }

    [Fact]
    public void BothLibrariesInOneQuery_DisjointVariables()
    {
        var e = BothLibraries();
        var sols = e.QueryAll("B #> 1, B #< 4, {C = 1.5 * 2.0}, label([B]).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal("2", sols[0]["B"]!.ToString());
        Assert.Equal("3", sols[0]["C"]!.ToString());
    }

    [Fact]
    public void ReverseLoadOrder_AlsoWorks()
    {
        var e = BothLibraries(fdFirst: false);
        var sols = e.QueryAll(@"Q in 5..7, Q #\= 6, {P > 1.0}, label([Q]).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal("5", sols[0]["Q"]!.ToString());
        Assert.Equal("7", sols[1]["Q"]!.ToString());
    }

    [Fact]
    public void FdAloneStillWorks()
    {
        // Single-library regression: the multifile change must not affect
        // solo usage.
        var e = new PrologEngine();
        e.UseClpfd();
        var sols = e.QueryAll(@"X in 1..3, X #\= 2, label([X]).").ToList();
        Assert.Equal(2, sols.Count);
    }
}
