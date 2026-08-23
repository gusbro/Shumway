using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 34: operator-aware TermRenderer, atom_to_term/3,
/// with_output_to/2, and graceful halt in QueryAll.
/// </summary>
public class Chunk34Tests
{
    // A double-quoted literal reaches C# as the LIST it is (ADR-047 decision 6):
    // the representation is not observable at the boundary, so what arrives is
    // the same whether or not the engine stored it packed.
    private static Term Text(string s)
    {
        Term t = new AtomTerm("[]");
        for (int i = s.Length - 1; i >= 0; i--)
            t = new CompoundTerm(".", new Term[] { new AtomTerm(s[i].ToString()), t });
        return t;
    }

    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);

    private static PrologEngine WithCaptureOut(out StringWriter sw)
    {
        sw = new StringWriter();
        return new PrologEngine { Out = sw };
    }

    // ---------- Operator-aware rendering ----------

    [Fact]
    public void WriteTerm_PlusCompound_RendersInOperatorForm()
    {
        // write_term emits operator form. Symbolic infix operators
        // render tight (no surrounding spaces), matching SWI / GNU /
        // SICStus — chunk 209+ made that the default.
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(a + b, []).");
        Assert.Equal("a+b", sw.ToString());
    }

    [Fact]
    public void WriteTerm_NestedOperators_RenderWithSpaces()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(1 + 2 * 3, []).");
        Assert.Contains("+", sw.ToString());
        Assert.Contains("*", sw.ToString());
    }

    [Fact]
    public void WriteTerm_PrefixOperatorOnNonNumeric_RendersInOperatorForm()
    {
        // `-` applied to a non-numeric arg stays as a compound (the
        // negative-literal collapse only fires for Integer / Float
        // tokens), so the renderer's prefix-op path kicks in. Spacing is
        // fuse-minimal (Neumerkel #140: `-a`, not `- a`).
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(- foo, []).");
        Assert.Equal("-foo", sw.ToString());
    }

    [Fact]
    public void WriteTerm_IgnoreOps_RendersCanonicalForm()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(a + b, [ignore_ops(true)]).");
        Assert.Equal("+(a,b)", sw.ToString());   // Phase 33: compact ISO layout
    }

    // ---------- atom_to_term/3 ----------

    [Fact]
    public void AtomToTerm_GroundCompound_NoBindings()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_to_term('foo(1, 2)', T, Bindings).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        // Bindings list is empty for a ground term.
        Assert.Equal(Atom("[]"), sol["Bindings"]);
    }

    [Fact]
    public void AtomToTerm_WithVariables_ReturnsNamedBindings()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_to_term('foo(X, Y)', T, Bs).");
        Assert.True(sol.Success);
        // Bindings = ['X' = _, 'Y' = _]: two '='(Name, Var) compounds.
        var head = Assert.IsType<CompoundTerm>(sol["Bs"]);
        Assert.Equal(".", head.Functor);
        var firstPair = Assert.IsType<CompoundTerm>(head.Args[0]);
        Assert.Equal("=", firstPair.Functor);
        Assert.Equal(Atom("X"), firstPair.Args[0]);
    }

    // ---------- with_output_to/2 ----------

    [Fact]
    public void WithOutputTo_Atom_CapturesGoalOutput()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("with_output_to(atom(A), write(hello)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["A"]);
    }

    [Fact]
    public void WithOutputTo_String_CapturesGoalOutput()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("with_output_to(string(S), write(world)).");
        Assert.True(sol.Success);
        Assert.Equal(Text("world"), sol["S"]);
    }

    [Fact]
    public void WithOutputTo_MultipleWrites_AllCaptured()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "with_output_to(atom(A), (write(a), write(' '), write(b))).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a b"), sol["A"]);
    }

    // ---------- halt in QueryAll ----------

    [Fact]
    public void Halt_InsideQueryAll_StopsIterationGracefully()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic v/1.");
        engine.Query("assertz(v(1)).");
        engine.Query("assertz(v(2)).");

        // QueryAll over v(X). Halt fires synthetically when X = 2 is seen.
        // For simplicity: query that explicitly halts.
        var solutions = engine.QueryAll("(v(X) ; halt(99)).").ToList();
        // Solutions yielded before halt = the assertz'd facts.
        Assert.True(solutions.Count >= 1);
        Assert.Equal(99, engine.LastHaltExitCode);
    }

    [Fact]
    public void Halt_ExitCodeReset_BetweenQueries()
    {
        var engine = new PrologEngine();
        engine.Query("halt(5).");
        Assert.Equal(5, engine.LastHaltExitCode);

        // Subsequent normal query resets the halt indicator.
        engine.Query("true.");
        Assert.Null(engine.LastHaltExitCode);
    }
}
