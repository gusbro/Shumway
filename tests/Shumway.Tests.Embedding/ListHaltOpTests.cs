using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 32: native implementations of the common list utilities plus
/// halt/0/1 termination and op/3 runtime operator definition.
/// </summary>
public class ListHaltOpTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- member/2 ----------

    [Fact]
    public void Member_FirstSolution_BindsFirstElement()
    {
        // member(X, [a, b, c]) — Phase-1 first-solution only.
        var engine = new PrologEngine();
        var sol = engine.Query("member(X, [a, b, c]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["X"]);
    }

    [Fact]
    public void Member_GroundElement_ChecksMembership()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("member(b, [a, b, c]).").Success);
        Assert.False(engine.Query("member(z, [a, b, c]).").Success);
    }

    [Fact]
    public void Member_EmptyList_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("member(_, []).").Success);
    }

    // ---------- nth0/3 + nth1/3 ----------

    [Fact]
    public void Nth0_ZeroIndex_FirstElement()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("a"), engine.Query("nth0(0, [a, b, c], X).")["X"]);
        Assert.Equal(Atom("c"), engine.Query("nth0(2, [a, b, c], X).")["X"]);
    }

    [Fact]
    public void Nth1_OneIndex_FirstElement()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("a"), engine.Query("nth1(1, [a, b, c], X).")["X"]);
        Assert.Equal(Atom("c"), engine.Query("nth1(3, [a, b, c], X).")["X"]);
    }

    [Fact]
    public void Nth_OutOfRange_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("nth0(99, [a, b], _).").Success);
        Assert.False(engine.Query("nth1(0, [a, b], _).").Success);   // 1-based; 0 not allowed
    }

    // ---------- reverse/2 ----------

    [Fact]
    public void Reverse_ReversesList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("reverse([a, b, c, d], R).");
        Assert.True(sol.Success);
        // [d, c, b, a]
        Assert.Equal(
            new CompoundTerm(".",
                new[] { Atom("d"),
                    new CompoundTerm(".",
                        new[] { Atom("c"),
                            new CompoundTerm(".",
                                new[] { Atom("b"),
                                    new CompoundTerm(".",
                                        new[] { Atom("a"), Atom("[]") }) }) }) }),
            sol["R"]);
    }

    [Fact]
    public void Reverse_EmptyList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("reverse([], R).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("[]"), sol["R"]);
    }

    // ---------- last/2 ----------

    [Fact]
    public void Last_BindsFinalElement()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("c"), engine.Query("last([a, b, c], X).")["X"]);
        Assert.Equal(Int(42), engine.Query("last([1, 2, 42], X).")["X"]);
    }

    [Fact]
    public void Last_EmptyList_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("last([], _).").Success);
    }

    // ---------- list_to_set/2 ----------

    [Fact]
    public void ListToSet_DropsDuplicatesPreservingOrder()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("list_to_set([a, b, a, c, b, a], S).");
        Assert.True(sol.Success);
        // Expected first-occurrence order: [a, b, c].
        Assert.Equal(
            new CompoundTerm(".",
                new[] { Atom("a"),
                    new CompoundTerm(".",
                        new[] { Atom("b"),
                            new CompoundTerm(".",
                                new[] { Atom("c"), Atom("[]") }) }) }),
            sol["S"]);
    }

    // ---------- halt/0 + halt/1 ----------

    [Fact]
    public void Halt0_TerminatesWithExitCodeZero()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologHaltException>(
            () => engine.Query("halt."));
        Assert.Equal(0, ex.ExitCode);
    }

    [Fact]
    public void Halt1_TerminatesWithGivenExitCode()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologHaltException>(
            () => engine.Query("halt(42)."));
        Assert.Equal(42, ex.ExitCode);
    }

    [Fact]
    public void Halt_NotCaughtByCatch()
    {
        // halt is a terminating action — catch/3 should NOT intercept it.
        // (It propagates straight to the .NET caller.)
        var engine = new PrologEngine();
        Assert.Throws<Shumway.Core.PrologHaltException>(
            () => engine.Query("catch(halt(7), _, true)."));
    }

    // ---------- op/3 runtime ----------

    [Fact]
    public void Op_DefinesOperatorForSubsequentParses()
    {
        var engine = new PrologEngine();
        // Define a new infix operator at priority 500 so it nests cleanly
        // inside the standard = operator (which is xfx 700).
        var defineSol = engine.Query("op(500, xfx, plus_op).");
        Assert.True(defineSol.Success);

        // Now parse and run a query that uses it.
        var sol = engine.Query("X = (a plus_op b).");
        Assert.True(sol.Success);
        var ct = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("plus_op", ct.Functor);
        Assert.Equal(2, ct.Args.Length);
    }

    [Fact]
    public void Op_AcceptsListOfNames()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("op(500, yfx, [op_a, op_b]).").Success);
        Assert.True(engine.Query("X = (a op_a b).").Success);
        Assert.True(engine.Query("X = (a op_b b).").Success);
    }

    [Fact]
    public void Op_BadType_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => engine.Query("op(700, bogus, foo)."));
    }
}
