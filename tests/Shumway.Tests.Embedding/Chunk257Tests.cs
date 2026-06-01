using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 257: <c>portray_clause/1,2</c> SWI-style clause
/// pretty-printer + shared <see cref="ClausePortrayer"/> helper
/// the listing path now delegates to. Variables with synthetic
/// <c>_G&lt;n&gt;</c> names get renumbered to <c>A</c>, <c>B</c>,
/// …; original parser-given names pass through.
/// </summary>
public class Chunk257Tests
{
    private static string Run(string source, string query)
    {
        var engine = new PrologEngine();
        if (!string.IsNullOrEmpty(source)) engine.ConsultString(source);
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query(query);
        return sw.ToString();
    }

    [Fact]
    public void PortrayClause_BareAtom_PrintsAsFact()
    {
        Assert.Equal("foo." + System.Environment.NewLine,
            Run("", "portray_clause(foo)."));
    }

    [Fact]
    public void PortrayClause_Compound_PrintsAsFact()
    {
        Assert.Equal("foo(a, b)." + System.Environment.NewLine,
            Run("", "portray_clause(foo(a, b))."));
    }

    [Fact]
    public void PortrayClause_Rule_HeadAndIndentedBody()
    {
        string output = Run("",
            "portray_clause((bar(X, Y) :- baz(X), qux(Y))).");
        // Variables get renumbered to A, B (X and Y came in as
        // heap variables → synthetic names → renamed).
        Assert.Contains("bar(A, B) :-", output);
        Assert.Contains("    baz(A),", output);
        Assert.Contains("    qux(B).", output);
    }

    [Fact]
    public void PortrayClause_Directive_OneLine()
    {
        string output = Run("",
            "portray_clause((:- use_module(library(lists)))).");
        Assert.Equal(
            ":- use_module(library(lists))." + System.Environment.NewLine,
            output);
    }

    [Fact]
    public void PortrayClause_Dcg_UsesArrow()
    {
        string output = Run("",
            "portray_clause((noun --> [cat])).");
        Assert.Contains("noun -->", output);
        Assert.Contains("    [cat].", output);
    }

    [Fact]
    public void PortrayClause_VariableSharing_SameLetter()
    {
        string output = Run("",
            "portray_clause((eq(X, X) :- true)).");
        // Body is `true` → fact-style one-liner. Both args share
        // the same heap variable; both should print as A.
        Assert.Equal(
            "eq(A, A)." + System.Environment.NewLine,
            output);
    }

    [Fact]
    public void PortrayClause_Two_Streams()
    {
        // portray_clause/2 writes to the given stream. Run a query
        // that opens a memory stream via with_output_to.
        // Simpler: use current_output as the stream (we can verify
        // the engine.Out path).
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("current_output(S), portray_clause(S, foo(bar)).");
        Assert.Contains("foo(bar).", sw.ToString());
    }

    [Fact]
    public void Listing_RuntimeAsserted_UsesRenamedVars()
    {
        // assertz routes the clause through the heap, so the AST
        // stored in _dynamicClauses has synthetic _Gn names. The
        // chunk-257 path renames them to A, B, C.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(my_pred/2).\n");
        engine.Query("assertz((my_pred(X, Y) :- Y is X + 1)).");
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("listing(my_pred).");
        string output = sw.ToString();
        Assert.Contains("my_pred(A, B)", output);
        Assert.Contains("B is A+1", output);
        Assert.DoesNotContain("_G", output);
    }

    [Fact]
    public void Listing_ConsultedSource_PreservesUserNames()
    {
        // Source clauses keep their parser-given names (chunk 254
        // behaviour, still works after the chunk-257 refactor).
        var output = Run(
            ":- public greet/2.\n"
            + "greet(X, Y) :- Y = hello(X).\n",
            "listing(greet).");
        Assert.Contains("greet(X, Y)", output);
        Assert.Contains("Y=hello(X)", output);
        Assert.DoesNotContain("_G", output);
        // Should NOT have been renumbered to A, B since the names
        // came from source.
        Assert.DoesNotContain("greet(A, B)", output);
    }

    [Fact]
    public void Portrayer_DirectHelper_StableLayout()
    {
        // Drive ClausePortrayer.Print directly with a hand-built
        // term — confirms the helper is the shared rendering path.
        var sw = new StringWriter();
        var head = new Shumway.Compiler.Ast.CompoundTerm("p",
            new Shumway.Compiler.Ast.Term[] { new Shumway.Compiler.Ast.VarTerm("Var") });
        var body = new Shumway.Compiler.Ast.CompoundTerm("q",
            new Shumway.Compiler.Ast.Term[] { new Shumway.Compiler.Ast.VarTerm("Var") });
        var rule = new Shumway.Compiler.Ast.CompoundTerm(":-",
            new Shumway.Compiler.Ast.Term[] { head, body });
        ClausePortrayer.Print(sw, rule);
        string output = sw.ToString();
        Assert.Contains("p(Var) :-", output);
        Assert.Contains("    q(Var).", output);
    }
}
