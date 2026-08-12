using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 31: DCG rules with disjunction and if-then-else; the in-memory
/// <c>read_term_from_atom/2</c> for terms parsed at runtime; and the
/// extra write-family builtins (<c>write_canonical/1</c>,
/// <c>print/1</c>).
/// </summary>
public class DcgIoExtraTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) => new CompoundTerm(f, args);

    private static PrologEngine WithCaptureOut(out StringWriter sw)
    {
        sw = new StringWriter();
        return new PrologEngine { Out = sw };
    }

    // ---------- DCG disjunction ----------

    [Fact]
    public void Dcg_Disjunction_TakesEitherBranch()
    {
        // colour --> [red] ; [green] ; [blue].
        var engine = new PrologEngine();
        engine.ConsultString("colour --> [red] ; [green] ; [blue].\n");

        Assert.True(engine.Query("colour([red], []).").Success);
        Assert.True(engine.Query("colour([green], []).").Success);
        Assert.True(engine.Query("colour([blue], []).").Success);
        Assert.False(engine.Query("colour([purple], []).").Success);
    }

    [Fact]
    public void Dcg_IfThenElse_BranchesOnPredicate()
    {
        // sign(P) --> [N], ( { N > 0 } -> { P = positive } ; { P = zero_or_neg } ).
        // We pass already-positive / zero ints via the input list to sidestep
        // the negative-literal parsing question (-3 parses as -(3), not the
        // integer cell the integer/1 test would need).
        var engine = new PrologEngine();
        engine.ConsultString(
            "sign(P) --> [N], ( { N > 0 } -> { P = positive } ; { P = zero_or_neg } ).\n");

        Assert.Equal(Atom("positive"), engine.Query("sign(P, [5], []).")["P"]);
        Assert.Equal(Atom("zero_or_neg"), engine.Query("sign(P, [0], []).")["P"]);
    }

    [Fact]
    public void Dcg_DisjunctionInsideConjunction()
    {
        // greeting --> ([hello] ; [hi]), [world].
        var engine = new PrologEngine();
        engine.ConsultString("greeting --> ([hello] ; [hi]), [world].\n");

        Assert.True(engine.Query("greeting([hello, world], []).").Success);
        Assert.True(engine.Query("greeting([hi, world], []).").Success);
        Assert.False(engine.Query("greeting([hello, planet], []).").Success);
    }

    // ---------- read_term_from_atom/2 ----------

    [Fact]
    public void ReadTerm_ParsesAtomIntoCompound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("read_term_from_atom('foo(1, 2, 3)', T).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(3, t.Args.Length);
        Assert.Equal(Int(1), t.Args[0]);
        Assert.Equal(Int(2), t.Args[1]);
        Assert.Equal(Int(3), t.Args[2]);
    }

    [Fact]
    public void ReadTerm_ParsesAtomicAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("read_term_from_atom(hello, T).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["T"]);
    }

    [Fact]
    public void ReadTerm_ParsesIntegerInAtomText()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("read_term_from_atom('42', T).");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["T"]);
    }

    // ---------- write_canonical/1 + print/1 ----------

    [Fact]
    public void WriteCanonical_UnfoldsListsToFunctionalNotation()
    {
        // ISO §7.10.5 ignore_ops: canonical form writes the list compound
        // '.'(H,T) in functional notation (Neumerkel #34), unlike write/1.
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_canonical(foo(a, 1, [b])).");
        Assert.Equal("foo(a,1,'.'(b,[]))", sw.ToString());
    }

    [Fact]
    public void Print_OutputsSameAsWrite()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("print(hello).");
        Assert.Equal("hello", sw.ToString());
    }
}
