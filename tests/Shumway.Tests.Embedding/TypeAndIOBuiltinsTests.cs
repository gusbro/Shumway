using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class TypeAndIOBuiltinsTests
{
    // ---------- 10d: Type tests ----------

    [Fact]
    public void Var_TrueForUnbound_FalseForBound()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("var(X).").Success);
        Assert.False(engine.Query("var(foo).").Success);
        Assert.False(engine.Query("X = 1, var(X).").Success);    // bound to int
    }

    [Fact]
    public void Nonvar_IsTheNegationOfVar()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("nonvar(X).").Success);
        Assert.True(engine.Query("nonvar(foo).").Success);
        Assert.True(engine.Query("X = 1, nonvar(X).").Success);
    }

    [Fact]
    public void Atom_RecognisesUnquotedAndEmptyList()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("atom(foo).").Success);
        Assert.True(engine.Query("atom([]).").Success);                 // [] is the atom '[]'
        Assert.False(engine.Query("atom(42).").Success);
        Assert.False(engine.Query("atom(X).").Success);                  // unbound
        Assert.False(engine.Query("atom(foo(a)).").Success);             // compound
        Assert.False(engine.Query("atom([a]).").Success);                // non-empty list
    }

    [Fact]
    public void Integer_OnlyForInts()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("integer(42).").Success);
        Assert.True(engine.Query("X is 1 + 2, integer(X).").Success);
        Assert.False(engine.Query("integer(foo).").Success);
        Assert.False(engine.Query("X is 10 / 4, integer(X).").Success);  // float
    }

    [Fact]
    public void Float_OnlyForFloats()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("X is 10 / 4, float(X).").Success);
        Assert.False(engine.Query("float(42).").Success);
        Assert.False(engine.Query("float(foo).").Success);
    }

    [Fact]
    public void Number_IntegerOrFloat()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("number(42).").Success);
        Assert.True(engine.Query("X is 10 / 4, number(X).").Success);
        Assert.False(engine.Query("number(foo).").Success);
        Assert.False(engine.Query("number([1, 2]).").Success);
    }

    [Fact]
    public void Atomic_NonCompoundNonVar()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("atomic(foo).").Success);
        Assert.True(engine.Query("atomic(42).").Success);
        Assert.True(engine.Query("atomic([]).").Success);
        Assert.False(engine.Query("atomic(X).").Success);
        Assert.False(engine.Query("atomic([a, b]).").Success);
        Assert.False(engine.Query("atomic(foo(a)).").Success);
    }

    [Fact]
    public void Compound_StructuredTerms()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("compound(foo(a)).").Success);
        Assert.True(engine.Query("compound([a, b]).").Success);
        Assert.False(engine.Query("compound(foo).").Success);            // zero-arg atom
        Assert.False(engine.Query("compound([]).").Success);              // empty list is atom '[]'
        Assert.False(engine.Query("compound(42).").Success);
        Assert.False(engine.Query("compound(X).").Success);
    }

    [Fact]
    public void IsList_ProperListsOnly()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("is_list([]).").Success);
        Assert.True(engine.Query("is_list([a]).").Success);
        Assert.True(engine.Query("is_list([a, b, c]).").Success);
        Assert.False(engine.Query("is_list(foo).").Success);
        Assert.False(engine.Query("is_list([a | b]).").Success);          // improper tail
        Assert.False(engine.Query("is_list(X).").Success);                // unbound
    }

    // ---------- 10e: I/O ----------

    [Fact]
    public void Write_AtomLiteral()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("write(hello).").Success);
        Assert.Equal("hello", sw.ToString());
    }

    [Fact]
    public void Write_Integer()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("write(42).").Success);
        Assert.Equal("42", sw.ToString());
    }

    [Fact]
    public void Write_Compound()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("write(foo(a, 1)).").Success);
        Assert.Equal("foo(a, 1)", sw.ToString());
    }

    [Fact]
    public void Write_List()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("write([a, b, c]).").Success);
        Assert.Equal("[a, b, c]", sw.ToString());
    }

    [Fact]
    public void Write_PartialList()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        // [a | T] where T is unbound — renders with " | _Gn" tail.
        Assert.True(engine.Query("write([a | T]).").Success);
        string output = sw.ToString();
        Assert.StartsWith("[a | _G", output);
        Assert.EndsWith("]", output);
    }

    [Fact]
    public void Nl_WritesNewline()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("nl.").Success);
        Assert.Equal(Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void Writeln_AtomThenNewline()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("writeln(hello).").Success);
        Assert.Equal("hello" + Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void Write_BoundVariable_RendersTheBoundValue()
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        Assert.True(engine.Query("X = foo(bar), write(X).").Success);
        Assert.Equal("foo(bar)", sw.ToString());
    }

    [Fact]
    public void Write_NestedCompoundAndArithmetic()
    {
        // Show that I/O composes with arithmetic — the result of N is X + 1
        // shows up correctly when written.
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;

        engine.ConsultString("show_inc(X) :- N is X + 1, write(N), nl.");
        Assert.True(engine.Query("show_inc(41).").Success);
        Assert.Equal("42" + Environment.NewLine, sw.ToString());
    }
}
