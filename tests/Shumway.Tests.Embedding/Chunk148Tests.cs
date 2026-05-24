using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 148: <see cref="TermReader.Materialize"/> used to overflow
/// the C# stack on a cyclic term. Plain <c>=/2</c> can build one
/// (ISO permits it — only <c>unify_with_occurs_check/2</c> refuses);
/// observing the resulting binding from .NET then hit infinite
/// recursion. Cycles are now broken via a synthetic
/// <c>VarTerm("_C{addr}")</c> marker at the back-edge.
///
/// <para>This closes the chunk-132 recorded gap in
/// TermUnificationConformance.</para>
/// </summary>
public class Chunk148Tests
{
    [Fact]
    public void CyclicCompound_TerminatesMaterialise()
    {
        // X = f(X) — building succeeds; reading X back was the
        // problem before chunk 148. With cycle detection it
        // terminates; the value slot is a compound whose first
        // arg references a cycle marker.
        var e = new PrologEngine();
        var sol = e.Query("X = f(X), Y = a.");
        Assert.True(sol.Success);
        var x = sol["X"];
        // Outer is f/1 compound.
        var c = Assert.IsType<CompoundTerm>(x);
        Assert.Equal("f", c.Functor);
        Assert.Single(c.Args);
        // Inner arg is the cycle marker — a VarTerm with name
        // starting "_C" (chunk-148 synthetic).
        var inner = Assert.IsType<VarTerm>(c.Args[0]);
        Assert.StartsWith("_C", inner.Name);
    }

    [Fact]
    public void DeeperCyclicCompound_AlsoTerminates()
    {
        // X = f(g(X)) — two levels deep before the cycle.
        var e = new PrologEngine();
        var sol = e.Query("X = f(g(X)), Y = a.");
        Assert.True(sol.Success);
        var f = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("f", f.Functor);
        var g = Assert.IsType<CompoundTerm>(f.Args[0]);
        Assert.Equal("g", g.Functor);
        var inner = Assert.IsType<VarTerm>(g.Args[0]);
        Assert.StartsWith("_C", inner.Name);
    }

    [Fact]
    public void CyclicList_TerminatesMaterialise()
    {
        // X = [a | X] — a cons-cell-level cycle.
        var e = new PrologEngine();
        var sol = e.Query("X = [a | X], Y = b.");
        Assert.True(sol.Success);
        var cons = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal(".", cons.Functor);
        Assert.Equal((Term)new AtomTerm("a"), cons.Args[0]);
        // The tail is the cycle marker.
        var tail = Assert.IsType<VarTerm>(cons.Args[1]);
        Assert.StartsWith("_C", tail.Name);
    }

    [Fact]
    public void NonCyclicCompound_StillWorks()
    {
        // Regression: ensure the cycle-detection path doesn't break
        // normal terms.
        var e = new PrologEngine();
        var sol = e.Query("X = f(g(1), h(2, 3)).");
        Assert.True(sol.Success);
        var f = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("f", f.Functor);
        Assert.Equal(2, f.Args.Length);
    }

    [Fact]
    public void SharedSubTerm_NotMisreportedAsCycle()
    {
        // X = f(Y, Y), Y = hello — Y appears twice but it isn't a
        // cycle. The cycle detector should only fire on a true back
        // edge through an STR/LIS cell, not on shared values.
        var e = new PrologEngine();
        var sol = e.Query("Y = hello, X = f(Y, Y).");
        Assert.True(sol.Success);
        var f = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal((Term)new AtomTerm("hello"), f.Args[0]);
        Assert.Equal((Term)new AtomTerm("hello"), f.Args[1]);
    }
}
