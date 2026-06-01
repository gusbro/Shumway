using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 258: <c>portray_clause</c> always breaks
/// <c>,</c>-conjunctions across lines (SWI / SICStus convention),
/// aligning each argument past the open paren. Comma rendering
/// uses <c>", "</c> (no leading space) instead of the symbolic
/// fallback <c>" , "</c>.
/// </summary>
public class Chunk258Tests
{
    private static string Run(string query)
    {
        var engine = new PrologEngine();
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query(query);
        return sw.ToString();
    }

    [Fact]
    public void Comma_Renders_NoLeadingSpace()
    {
        // Sanity: the AST renderer's comma special case fires.
        var sw = new StringWriter();
        var t = new Shumway.Compiler.Ast.CompoundTerm(",",
            new Shumway.Compiler.Ast.Term[]
            {
                new Shumway.Compiler.Ast.AtomTerm("a"),
                new Shumway.Compiler.Ast.AtomTerm("b"),
            });
        Assert.Equal("a, b", AstTermRenderer.Render(t));
    }

    [Fact]
    public void Semicolon_Renders_NoLeadingSpace()
    {
        // Same special case as comma: disjunction is a sequence
        // operator, not an arithmetic symbol — `a; b` not `a ; b`.
        var t = new Shumway.Compiler.Ast.CompoundTerm(";",
            new Shumway.Compiler.Ast.Term[]
            {
                new Shumway.Compiler.Ast.AtomTerm("a"),
                new Shumway.Compiler.Ast.AtomTerm("b"),
            });
        Assert.Equal("a; b", AstTermRenderer.Render(t));
    }

    [Fact]
    public void NestedConjunction_AlwaysBreaks_MatchesSwiLayout()
    {
        // The exact case from chunk-258 issue: nested conjunctions
        // inside catch / findall must each break across lines.
        string output = Run(
            "portray_clause((bar(X) :- aa, "
            + "catch((a,b,c),_,findall(z,(g,h,i),X)))).");
        // Top-level body breaks: aa then catch(...).
        Assert.Contains("bar(A) :-", output);
        Assert.Contains("    aa,", output);
        Assert.Contains("    catch(", output);
        // Inner (a, b, c) paren-broken at the catch open paren's
        // alignment column.
        Assert.Contains("( a,", output);
        Assert.Contains("b,", output);
        // The closing paren of (a, b, c) sits on its own line
        // aligned with its opener.
        Assert.Matches(@"c\s*$", output.Split('\n').First(l => l.TrimEnd().EndsWith("c")));
        // findall(...) gets its args on separate lines too.
        Assert.Contains("findall(z,", output);
        // The inner (g, h, i) breaks similarly.
        Assert.Contains("( g,", output);
    }

    [Fact]
    public void NoConjunction_StaysInline()
    {
        // A goal without any ,-chain inside renders inline — the
        // chunk-258 layout only kicks in when there's a sequence
        // to break.
        string output = Run(
            "portray_clause((foo(X) :- bar(X, baz(qux)))).");
        Assert.Contains("foo(A) :-", output);
        // bar(...) all on one line — no nested ,, no break.
        Assert.Contains("    bar(A, baz(qux)).", output);
    }

    [Fact]
    public void CompoundContainingConjunction_BreaksItsArgs()
    {
        // Once a compound has a nested ,-chain in any arg, all
        // its args go on separate lines aligned past the open paren.
        string output = Run(
            "portray_clause((foo :- bar(a, (b, c), d))).");
        // bar's args break:
        //     bar(a,
        //         ( b,
        //           c
        //         ),
        //         d)
        Assert.Contains("    bar(a,", output);
        Assert.Contains("( b,", output);
        Assert.Contains("d)", output);
    }

    [Fact]
    public void RuleWithoutConjunction_StaysSimple()
    {
        // A rule with a single goal body — no ,-chain at all,
        // body fits on one line, indented under head.
        string output = Run(
            "portray_clause((foo(X) :- write(X))).");
        Assert.Contains("foo(A) :-", output);
        Assert.Contains("    write(A).", output);
    }

    [Fact]
    public void EachConjunctionGoalEndsWithCommaExceptLast()
    {
        // Chunk-258 layout invariant: in a broken conjunction,
        // every goal line ends with `,` except the final one.
        // The final goal's line carries the clause-terminating `.`
        // (added at the clause level).
        string output = Run(
            "portray_clause((p :- a, b, c)).");
        var lines = output.Split('\n');
        // body lines: "    a,", "    b,", "    c"
        Assert.Contains(lines, l => l.TrimEnd() == "    a,");
        Assert.Contains(lines, l => l.TrimEnd() == "    b,");
        Assert.Contains(lines, l => l.TrimEnd() == "    c.");
    }
}
