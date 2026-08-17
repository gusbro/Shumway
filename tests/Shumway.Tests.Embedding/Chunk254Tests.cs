using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 254: <c>listing/0,1</c> walks the AST clauses directly
/// instead of going through <c>clause/2 + write/1</c>, so variable
/// names captured by the parser survive into the output. Before
/// the fix: <c>greet(_G23, _G24) :- _G24 = hello(_G23)</c>. After:
/// <c>greet(X, Y) :- Y = hello(X)</c>.
/// </summary>
public class Chunk254Tests
{
    private static string CaptureListing(string source, string predName)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query($"listing({predName}).");
        return sw.ToString();
    }

    [Fact]
    public void ConsultedFact_PreservesVariableNames()
    {
        var output = CaptureListing(
            ":- public greet/2.\n"
            + "greet(X, Y) :- Y = hello(X).\n",
            "greet");
        // Variable names X and Y must appear verbatim — not _Gn.
        Assert.Contains("greet(X, Y)", output);
        Assert.Contains("Y=hello(X)", output);
        Assert.DoesNotContain("_G", output);
    }

    [Fact]
    public void MultiClause_PreservesPerClauseNames()
    {
        var output = CaptureListing(
            ":- public sum_of/2.\n"
            + "sum_of([], 0).\n"
            + "sum_of([H|T], Sum) :- sum_of(T, Rest), Sum is H + Rest.\n",
            "sum_of");
        Assert.Contains("sum_of([], 0).", output);
        Assert.Contains("sum_of([H | T], Sum)", output);
        Assert.Contains("sum_of(T, Rest)", output);
        Assert.Contains("Sum is H+Rest", output);
        Assert.DoesNotContain("_G", output);
    }

    [Fact]
    public void Fact_PrintsOnSingleLine()
    {
        var output = CaptureListing(
            ":- public colour/1.\n"
            + "colour(red).\n"
            + "colour(green).\n",
            "colour");
        Assert.Contains("colour(red).", output);
        Assert.Contains("colour(green).", output);
    }

    [Fact]
    public void Rule_IndentsBodyGoalsOnSeparateLines()
    {
        var output = CaptureListing(
            ":- public test/0.\n"
            + "test :- foo(a), bar(b), baz(c).\n",
            "test");
        Assert.Contains("test :-", output);
        Assert.Contains("    foo(a)", output);
        Assert.Contains("    bar(b)", output);
        Assert.Contains("    baz(c)", output);
    }

    [Fact]
    public void DynamicSeed_PreservesNames()
    {
        // `:- dynamic` declared clauses parsed from source: AST
        // clauses retain VarTerm names.
        var output = CaptureListing(
            ":- dynamic(item/2).\n"
            + "item(Key, Value) :- Value > 0, Key = positive.\n",
            "item");
        Assert.Contains("item(Key, Value)", output);
        Assert.Contains("Value>0", output);
        Assert.DoesNotContain("_G", output);
    }

    [Fact]
    public void RuntimeAsserted_FallsBackToSyntheticNames()
    {
        // assertz at runtime: the clause travels through the heap
        // before being stored, where original VarTerm names are
        // lost. Listing emits the synthetic _Gn names — the user
        // gets *something* readable rather than nothing.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(foo/2).");
        engine.Query("assertz((foo(X, Y) :- Y = bar(X))).");
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("listing(foo).");
        string output = sw.ToString();
        // Synthetic names show; the user can still see the
        // structure of the asserted clause.
        Assert.Contains("foo(", output);
        Assert.Contains("=bar(", output);
    }

    [Fact]
    public void Listing_All_IncludesEveryUserPredicate()
    {
        var output = CaptureListing(
            ":- public a/0.\n"
            + ":- public b/1.\n"
            + "a :- true.\n"
            + "b(X) :- X = 42.\n",
            // pass a no-op "listing/0" path indirectly — use no name
            "_");  // placeholder, we'll re-do the call
        // Re-call with listing/0 (no arg) for the whole-engine view.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public a/0.
            :- public b/1.
            a :- true.
            b(X) :- X = 42.
            """);
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("listing.");
        string allOut = sw.ToString();
        Assert.Contains("a.", allOut);
        Assert.Contains("b(X)", allOut);
        Assert.DoesNotContain("_G", allOut);
    }

    [Fact]
    public void AstRenderer_OperatorAware()
    {
        // Sanity: AstTermRenderer renders operators / lists / atoms
        // in the parser-compatible form.
        Assert.Equal("X is 1+2",
            AstTermRenderer.Render(new Shumway.Compiler.Ast.CompoundTerm(
                "is", new Shumway.Compiler.Ast.Term[]
                {
                    new Shumway.Compiler.Ast.VarTerm("X"),
                    new Shumway.Compiler.Ast.CompoundTerm("+", new Shumway.Compiler.Ast.Term[]
                    {
                        new Shumway.Compiler.Ast.IntTerm(1),
                        new Shumway.Compiler.Ast.IntTerm(2),
                    }),
                })));

        // List syntax (./2 → [...]).
        Assert.Equal("[a, b, c]",
            AstTermRenderer.Render(
                new Shumway.Compiler.Ast.CompoundTerm(".", new Shumway.Compiler.Ast.Term[]
                {
                    new Shumway.Compiler.Ast.AtomTerm("a"),
                    new Shumway.Compiler.Ast.CompoundTerm(".", new Shumway.Compiler.Ast.Term[]
                    {
                        new Shumway.Compiler.Ast.AtomTerm("b"),
                        new Shumway.Compiler.Ast.CompoundTerm(".", new Shumway.Compiler.Ast.Term[]
                        {
                            new Shumway.Compiler.Ast.AtomTerm("c"),
                            new Shumway.Compiler.Ast.AtomTerm("[]"),
                        }),
                    }),
                })));
    }
}
