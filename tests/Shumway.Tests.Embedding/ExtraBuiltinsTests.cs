using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 26 coverage: format/2, write_term/2, sub_atom/5, between/3,
/// succ/2, numbervars/3. Each builtin is exercised in its declared
/// Phase-1 modes; non-deterministic modes that were explicitly deferred
/// just check they raise rather than producing wrong answers.
/// </summary>
public class ExtraBuiltinsTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine WithCaptureOut(out StringWriter sw)
    {
        sw = new StringWriter();
        return new PrologEngine { Out = sw };
    }

    // ---------- format/2 ----------

    [Fact]
    public void Format_AtomFormatString_PlainText()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('hello', []).");
        Assert.Equal("hello", sw.ToString());
    }

    [Fact]
    public void Format_TildeW_RendersAnyTerm()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('value=~w', [42]).");
        Assert.Equal("value=42", sw.ToString());
    }

    [Fact]
    public void Format_TildeA_RendersAtom()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('~a says hi', [alice]).");
        Assert.Equal("alice says hi", sw.ToString());
    }

    [Fact]
    public void Format_TildeD_RendersInteger()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('count=~d', [7]).");
        Assert.Equal("count=7", sw.ToString());
    }

    [Fact]
    public void Format_TildeN_WritesNewline()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('a~nb', []).");
        Assert.Equal("a" + System.Environment.NewLine + "b", sw.ToString());
    }

    [Fact]
    public void Format_LiteralTilde_Escape()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('a~~b', []).");
        Assert.Equal("a~b", sw.ToString());
    }

    [Fact]
    public void Format_MultipleSpecs_ConsumesArgsInOrder()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("format('[~a/~d]', [name, 3]).");
        Assert.Equal("[name/3]", sw.ToString());
    }

    // ---------- write_term/2 ----------

    [Fact]
    public void WriteTerm_RendersLikeWrite()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(foo(1, bar), []).");
        Assert.Equal("foo(1, bar)", sw.ToString());
    }

    // ---------- sub_atom/5 ----------

    [Fact]
    public void SubAtom_ExtractByBeforeAndLength()
    {
        var engine = new PrologEngine();
        // sub_atom(hello, 1, 3, After, Sub) → After = 1, Sub = ell.
        var sol = engine.Query("sub_atom(hello, 1, 3, After, Sub).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["After"]);
        Assert.Equal(Atom("ell"), sol["Sub"]);
    }

    [Fact]
    public void SubAtom_FindFirstOccurrenceBySubAtom()
    {
        var engine = new PrologEngine();
        // sub_atom(banana, Before, Length, After, ana).
        var sol = engine.Query("sub_atom(banana, Before, Length, After, ana).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["Before"]);
        Assert.Equal(Int(3), sol["Length"]);
        Assert.Equal(Int(2), sol["After"]);
    }

    [Fact]
    public void SubAtom_NotFound_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("sub_atom(banana, _, _, _, zzz).").Success);
    }

    // ---------- between/3 ----------

    [Fact]
    public void Between_GroundInRange_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("between(1, 10, 5).").Success);
        Assert.True(engine.Query("between(1, 10, 1).").Success);
        Assert.True(engine.Query("between(1, 10, 10).").Success);
    }

    [Fact]
    public void Between_GroundOutOfRange_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("between(1, 10, 0).").Success);
        Assert.False(engine.Query("between(1, 10, 11).").Success);
    }

    [Fact]
    public void Between_UnboundVar_BindsToLow()
    {
        // Phase-1 first-solution semantics: X gets bound to the low bound.
        var engine = new PrologEngine();
        var sol = engine.Query("between(5, 10, X).");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["X"]);
    }

    [Fact]
    public void Between_EmptyRange_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("between(10, 5, _).").Success);
    }

    // ---------- succ/2 ----------

    [Fact]
    public void Succ_Forward_FromKnownX()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(8), engine.Query("succ(7, Y).")["Y"]);
        Assert.Equal(Int(1), engine.Query("succ(0, Y).")["Y"]);
    }

    [Fact]
    public void Succ_Backward_FromKnownY()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(6), engine.Query("succ(X, 7).")["X"]);
        Assert.Equal(Int(0), engine.Query("succ(X, 1).")["X"]);
    }

    [Fact]
    public void Succ_Zero_HasNoPredecessor()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("succ(_, 0).").Success);
    }

    [Fact]
    public void Succ_NegativeX_Throws()
    {
        // Phase-9 chunk 131b: succ(-1, _) now raises a catchable
        // domain_error(not_less_than_zero, _) rather than the
        // uncatchable InvalidOperationException earlier phases used.
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("succ(-1, _)."));
        Assert.Equal("domain_error", ex.Kind);
        Assert.Equal("not_less_than_zero", ex.Detail);
    }

    // ---------- numbervars/3 ----------

    [Fact]
    public void NumberVars_GroundTerm_LeavesUnchanged()
    {
        var engine = new PrologEngine();
        // No vars to number, End = Start.
        var sol = engine.Query("numbervars(foo(1, 2), 0, End).");
        Assert.True(sol.Success);
        Assert.Equal(Int(0), sol["End"]);
    }

    [Fact]
    public void NumberVars_SingleVar_BindsToVar0()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("numbervars(X, 0, End).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["End"]);
        // X is now bound to '$VAR'(0).
        var x = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("$VAR", x.Functor);
        Assert.Equal(Int(0), x.Args[0]);
    }

    [Fact]
    public void NumberVars_TwoDistinctVars_GetSequentialNumbers()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("numbervars(p(X, Y), 0, End).");
        Assert.True(sol.Success);
        Assert.Equal(Int(2), sol["End"]);
        var xVar = Assert.IsType<CompoundTerm>(sol["X"]);
        var yVar = Assert.IsType<CompoundTerm>(sol["Y"]);
        Assert.Equal(Int(0), xVar.Args[0]);
        Assert.Equal(Int(1), yVar.Args[0]);
    }

    [Fact]
    public void NumberVars_SharedVar_GetsOneNumber()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("numbervars(p(X, X), 5, End).");
        Assert.True(sol.Success);
        Assert.Equal(Int(6), sol["End"]);
        // Same number for both occurrences of X.
        var xVar = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal(Int(5), xVar.Args[0]);
    }
}
