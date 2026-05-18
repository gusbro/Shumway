using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 48: parse / lex errors now prepend <c>line:col</c> to their
/// message, and the Tier-1 IL subset gains <c>get_structure</c>,
/// <c>put_structure</c>, and the <c>unify_*</c> family — so predicates
/// with compound head or body arguments (like <c>p(foo(X))</c>) now
/// promote to IL.
/// </summary>
public class Chunk48Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] a) => new CompoundTerm(f, a);

    // ============================================================================
    // Error message line/col prefix
    // ============================================================================

    [Fact]
    public void ParseError_IncludesLineAndColumn()
    {
        // Unclosed paren — the lexer parses the opening fine; the parser
        // discovers the EOF before the close.
        var ex = Assert.Throws<ParseException>(
            () => new Parser(new global::Shumway.Compiler.Lexer.Lexer("foo(a, b"))
                  .ReadClauseTerm());
        Assert.Matches(@"^\d+:\d+:", ex.Message);
    }

    [Fact]
    public void LexError_IncludesLineAndColumn()
    {
        var ex = Assert.Throws<LexerException>(
            () => {
                var lex = new global::Shumway.Compiler.Lexer.Lexer("foo \"unterminated");
                while (lex.NextToken().Kind != TokenKind.Eof) { }
            });
        Assert.Contains("1:", ex.Message);
    }

    [Fact]
    public void ParseError_ConsultStringSurfacesPosition()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ParseException>(
            () => engine.ConsultString("ok_clause.\nbad_clause(\n"));
        // Should contain "line:col:" somewhere — the exact line depends
        // on where the parser stops, but the message must start with the
        // position prefix.
        Assert.Matches(@"^\d+:\d+:", ex.Message);
    }

    // ============================================================================
    // IL compound-argument support
    // ============================================================================

    [Fact]
    public void Il_CompoundHeadArg_Promotes()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public pair_a/1.\npair_a(pair(a, _)).");
        Assert.True(engine.Query("pair_a(pair(a, b)).").Success);
        Assert.False(engine.Query("pair_a(pair(b, b)).").Success);
        Assert.False(engine.Query("pair_a(foo(a, b)).").Success);
    }

    [Fact]
    public void Il_NestedCompoundHead_Promotes()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public deep/1.\ndeep(foo(bar(baz))).");
        Assert.True(engine.Query("deep(foo(bar(baz))).").Success);
        Assert.False(engine.Query("deep(foo(bar(other))).").Success);
    }

    [Fact]
    public void Il_CompoundUnifiesWithVar()
    {
        // Head pattern matches against an unbound query var; IL should
        // construct the compound on the heap (write mode) and bind it.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public point/1.\npoint(p(1, 2)).");
        var sol = engine.Query("point(P).");
        Assert.True(sol.Success);
        Assert.Equal(Cmp("p", Int(1), Int(2)), sol["P"]);
    }

    [Fact]
    public void Il_BindsHeadArgumentVarToCallerCell()
    {
        // p(pair(X, Y)) head match against pair(a, b) should bind X=a, Y=b.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public split/3.\nsplit(pair(X, Y), X, Y).");
        var sol = engine.Query("split(pair(hello, world), A, B).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["A"]);
        Assert.Equal(Atom("world"), sol["B"]);
    }

    [Fact]
    public void Il_CompoundProducesSameResultsAsTier0()
    {
        var src = ":- public g/1.\ng(t(1, two, three)).";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sol0 = tier0.Query("g(T).");

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("g(_).");   // warm
        var sol1 = tier1.Query("g(T).");

        Assert.Equal(sol0["T"], sol1["T"]);
    }
}
