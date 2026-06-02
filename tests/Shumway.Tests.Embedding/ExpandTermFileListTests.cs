using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunks 273 + 274 — expand_term/2 (DCG expansion exposed)
/// and file_list/1,2 (Arity-Prolog plain-text database dump).
/// </summary>
public class ExpandTermFileListTests : IDisposable
{
    private readonly string _tmp;

    public ExpandTermFileListTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(),
            "shumway_filelist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string P(string rel) => Path.Combine(_tmp, rel).Replace('\\', '/');

    // ----- expand_term/2 -----

    [Fact]
    public void ExpandTerm_DcgRule_ExpandsToDifferenceListClause()
    {
        // A DCG rule `greeting --> [hello]` expands to a clause with two
        // extra args threading the difference list.
        var e = new PrologEngine();
        var sol = e.Query("expand_term((greeting --> [hello]), X).");
        Assert.True(sol.Success);
        // X should be `:-(greeting(S0, S), S0 = [hello | S])`.
        var c = (CompoundTerm)sol["X"]!;
        Assert.Equal(":-", c.Functor);
        Assert.Equal(2, c.Args.Length);
        var head = (CompoundTerm)c.Args[0];
        Assert.Equal("greeting", head.Functor);
        Assert.Equal(2, head.Args.Length);
    }

    [Fact]
    public void ExpandTerm_NonDcgTerm_PassesThrough()
    {
        var e = new PrologEngine();
        var sol = e.Query("expand_term(foo(a, b), X).");
        Assert.True(sol.Success);
        var c = (CompoundTerm)sol["X"]!;
        Assert.Equal("foo", c.Functor);
        Assert.Equal(2, c.Args.Length);
    }

    [Fact]
    public void ExpandTerm_PlainFact_PassesThrough()
    {
        var e = new PrologEngine();
        var sol = e.Query("expand_term(hello, X).");
        Assert.True(sol.Success);
        Assert.Equal("hello", ((AtomTerm)sol["X"]!).Name);
    }

    // ----- file_list/1, file_list/2 -----

    [Fact]
    public void FileList1_DumpsThenConsultBack_PreservesFacts()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic colour/1.");
        e.Query("assertz(colour(red)).");
        e.Query("assertz(colour(blue)).");
        var path = P("dump.pl");
        Assert.True(e.Query($"file_list('{path}').").Success);

        // The dump should re-consult into a fresh engine and reproduce
        // the same facts.
        var e2 = new PrologEngine();
        e2.Query($"consult('{path}').");
        var sol = e2.Query("findall(C, colour(C), L), sort(L, S).");
        Assert.True(sol.Success);
        Assert.Equal("[blue, red]", AstTermRenderer.Render(sol["S"]!));
    }

    [Fact]
    public void FileList2_SinglePredIndicator_DumpsOnlyThatPredicate()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic kept/1. :- dynamic skipped/1.");
        e.Query("assertz(kept(yes)).");
        e.Query("assertz(skipped(no)).");
        var path = P("partial.pl");
        Assert.True(e.Query($"file_list('{path}', kept/1).").Success);
        string content = File.ReadAllText(path);
        Assert.Contains("kept", content);
        Assert.DoesNotContain("skipped", content);
    }

    [Fact]
    public void FileList2_PredIndicatorList_DumpsEachInList()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic a/1. :- dynamic b/1. :- dynamic c/1.");
        e.Query("assertz(a(1)).");
        e.Query("assertz(b(2)).");
        e.Query("assertz(c(3)).");
        var path = P("multi.pl");
        Assert.True(e.Query($"file_list('{path}', [a/1, c/1]).").Success);
        string content = File.ReadAllText(path);
        Assert.Contains("a(1)", content);
        Assert.Contains("c(3)", content);
        Assert.DoesNotContain("b(2)", content);
    }

    [Fact]
    public void FileList1_OutputIncludesDynamicDirective()
    {
        // The dump emits `:- dynamic foo/N.` so a re-consult under
        // ISO-strict (implicit_dynamic=false) still works.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic stuff/2.");
        e.Query("assertz(stuff(a, 1)).");
        var path = P("dyn.pl");
        e.Query($"file_list('{path}').");
        string content = File.ReadAllText(path);
        Assert.Contains(":- dynamic stuff/2.", content);
    }

    [Fact]
    public void FileList2_RulesArePreserved()
    {
        var e = new PrologEngine();
        e.ConsultString(@"
            :- dynamic doubled/2.
            doubled(X, Y) :- Y is X * 2.
        ");
        var path = P("rules.pl");
        e.Query($"file_list('{path}', doubled/2).");
        // Re-consult and exercise.
        var e2 = new PrologEngine();
        e2.Query($"consult('{path}').");
        var sol = e2.Query("doubled(7, R).");
        Assert.True(sol.Success);
        Assert.Equal(14L, ((IntTerm)sol["R"]!).Value);
    }
}
