using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 28: term_to_atom/2, atom_string/2, and parser acceptance of
/// :- mode / :- discontiguous / :- multifile directives. The directive
/// metadata is stored on the <see cref="ModuleManifest"/> but doesn't
/// drive code generation in Phase 1.
/// </summary>
public class Chunk28Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- term_to_atom/2 ----------

    [Fact]
    public void TermToAtom_GroundTerm_RenderedAsAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("term_to_atom(foo(1, 2), A).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo(1,2)"), sol["A"]);   // Phase 33: compact ISO layout
    }

    [Fact]
    public void TermToAtom_GroundAtom_ParsedAsTerm()
    {
        var engine = new PrologEngine();
        // 'foo(1, 2)' as an atom name, parsed back into a compound.
        var sol = engine.Query("term_to_atom(T, 'foo(1, 2)').");
        Assert.True(sol.Success);
        var ct = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", ct.Functor);
        Assert.Equal(Int(1), ct.Args[0]);
        Assert.Equal(Int(2), ct.Args[1]);
    }

    [Fact]
    public void TermToAtom_RoundTrip()
    {
        // Render then parse should yield a structurally equal term.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "term_to_atom(point(3, 4), A), term_to_atom(T, A).");
        Assert.True(sol.Success);
        var ct = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("point", ct.Functor);
    }

    [Fact]
    public void TermToAtom_OperatorTerm_RendersInOperatorForm()
    {
        // term_to_atom must render in operator notation (SWI-compatible),
        // not canonical `/(hola, 2)`. Symbolic operators print tight
        // (no surrounding spaces) and unquoted.
        var engine = new PrologEngine();
        Assert.Equal(Atom("hola/2"),
            engine.Query("term_to_atom(hola/2, A).")["A"]);
        Assert.Equal(Atom("1+2*3"),
            engine.Query("term_to_atom(1+2*3, A).")["A"]);
    }

    [Fact]
    public void TermToAtom_OperatorTerm_RoundTrips()
    {
        // The operator-form atom parses back to the same term.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "term_to_atom(T, 'hola/2'), T == hola/2.").Success);
        Assert.True(engine.Query(
            "term_to_atom(a-b+c, A), term_to_atom(T, A), T == a-b+c.").Success);
    }

    [Fact]
    public void Write_SymbolicOperator_RendersTight()
    {
        // write/1 renders symbolic infix operators space-free, matching
        // SWI / GNU / SICStus (`hola/2`, not `hola / 2`).
        var engine = new PrologEngine();
        var sw = new System.IO.StringWriter();
        engine.Out = sw;
        engine.Query("write(hola/2).");
        Assert.Equal("hola/2", sw.ToString());
    }

    // ---------- atom_string/2 ----------

    [Fact]
    public void AtomString_AtomToString()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_string(hello, S).");
        Assert.True(sol.Success);
        // atom_string/2 produces text as a SEQUENCE, which crosses as the list
        // it is (ADR-047 decision 6).
        Assert.True(sol["S"]!.TryAsText(out string s));
        Assert.Equal("hello", s);
    }

    [Fact]
    public void AtomString_StringToAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_string(A, \"world\").");
        Assert.True(sol.Success);
        Assert.Equal(Atom("world"), sol["A"]);
    }

    // ---------- :- mode / :- discontiguous / :- multifile ----------

    [Fact]
    public void DiscontiguousDirective_RecordedOnManifest()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(m).
            :- discontiguous foo/2.
            foo(a, 1).
            bar(x).
            foo(b, 2).
            """);
        var manifest = engine.Modules["m"];
        // The functor id for foo/2 in this module's discontiguous set:
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("foo", permanent: true).Id, 2);
        Assert.Contains(fid, manifest.DiscontiguousFunctors);
    }

    [Fact]
    public void MultifileDirective_AcceptedAndStored()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(shared).
            :- multifile fact/1.
            fact(a).
            """);
        var manifest = engine.Modules["shared"];
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("fact", permanent: true).Id, 1);
        Assert.Contains(fid, manifest.MultifileFunctors);
    }

    [Fact]
    public void ModeDirective_StoresArgumentModes()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(typed).
            :- mode add(+, +, -).
            add(X, Y, Z) :- Z is X + Y.
            """);
        var manifest = engine.Modules["typed"];
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("add", permanent: true).Id, 3);
        Assert.True(manifest.ModeDeclarations.ContainsKey(fid));
        // Chunk 73: storage is now a list of ModeDeclaration objects.
        var decl = Assert.Single(manifest.ModeDeclarations[fid]);
        Assert.Equal(
            new[]
            {
                Shumway.Compiler.Modes.ModeIndicator.Input,
                Shumway.Compiler.Modes.ModeIndicator.Input,
                Shumway.Compiler.Modes.ModeIndicator.Output,
            },
            decl.ArgModes);
    }

    [Fact]
    public void DirectivesDontInterfereWithQuery()
    {
        // The whole point of accepting these directives in v1 is that they
        // don't break compilation — the program below has all three and
        // still computes the right answer.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(arith).
            :- public sum/3.
            :- mode sum(+, +, -).
            :- discontiguous sum/3.
            sum(X, Y, Z) :- Z is X + Y.
            """);
        Assert.Equal(Int(7), engine.Query("sum(3, 4, R).")["R"]);
    }

    [Fact]
    public void DiscontiguousListForm_Accepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(m2).
            :- discontiguous [foo/1, bar/2].
            foo(a).
            bar(x, 1).
            """);
        var manifest = engine.Modules["m2"];
        Assert.Equal(2, manifest.DiscontiguousFunctors.Count);
    }
}
