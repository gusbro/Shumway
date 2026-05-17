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
        Assert.Equal(Atom("foo(1, 2)"), sol["A"]);
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

    // ---------- atom_string/2 ----------

    [Fact]
    public void AtomString_AtomToString()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_string(hello, S).");
        Assert.True(sol.Success);
        Assert.Equal(new StringTerm("hello"), sol["S"]);
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
        engine.ConsultString(
            ":- module(m).\n" +
            ":- discontiguous foo/2.\n" +
            "foo(a, 1).\n" +
            "bar(x).\n" +
            "foo(b, 2).\n");
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
        engine.ConsultString(
            ":- module(shared).\n" +
            ":- multifile fact/1.\n" +
            "fact(a).\n");
        var manifest = engine.Modules["shared"];
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("fact", permanent: true).Id, 1);
        Assert.Contains(fid, manifest.MultifileFunctors);
    }

    [Fact]
    public void ModeDirective_StoresArgumentModes()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(typed).\n" +
            ":- mode add(+, +, -).\n" +
            "add(X, Y, Z) :- Z is X + Y.\n");
        var manifest = engine.Modules["typed"];
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("add", permanent: true).Id, 3);
        Assert.True(manifest.ModeDeclarations.ContainsKey(fid));
        Assert.Equal(new[] { "+", "+", "-" }, manifest.ModeDeclarations[fid]);
    }

    [Fact]
    public void DirectivesDontInterfereWithQuery()
    {
        // The whole point of accepting these directives in v1 is that they
        // don't break compilation — the program below has all three and
        // still computes the right answer.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(arith).\n" +
            ":- public sum/3.\n" +
            ":- mode sum(+, +, -).\n" +
            ":- discontiguous sum/3.\n" +
            "sum(X, Y, Z) :- Z is X + Y.\n");
        Assert.Equal(Int(7), engine.Query("sum(3, 4, R).")["R"]);
    }

    [Fact]
    public void DiscontiguousListForm_Accepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(m2).\n" +
            ":- discontiguous [foo/1, bar/2].\n" +
            "foo(a).\n" +
            "bar(x, 1).\n");
        var manifest = engine.Modules["m2"];
        Assert.Equal(2, manifest.DiscontiguousFunctors.Count);
    }
}
