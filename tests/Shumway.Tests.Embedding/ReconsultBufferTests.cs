using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Loading the same buffer twice. An editor's "load what I am looking at" is a
/// RE-consult: the predicates the text defines become what the text says. Plain
/// consult appends, which turns a second press of the button into duplicate
/// clauses and duplicate solutions.
/// </summary>
public sealed class ReconsultBufferTests
{
    [Fact]
    public void LoadingTheSameBufferTwiceDefinesItOnce()
    {
        var e = new PrologEngine();
        const string src = "p(1).\np(2).\n";
        e.ReconsultString(src);
        e.ReconsultString(src);
        var s = e.Query("findall(X, p(X), L), length(L, N).");
        Assert.True(s.Success);
        Assert.Equal(2L, Assert.IsType<IntTerm>(s["N"]!).Value);
    }

    [Fact]
    public void ItReplacesRatherThanMerges()
    {
        var e = new PrologEngine();
        e.ReconsultString("p(1).\np(2).\n");
        e.ReconsultString("p(3).\n");
        var s = e.Query("findall(X, p(X), L).");
        Assert.True(s.Success);
        Assert.Equal("[3]", AstTermRenderer.Render(s["L"]!, 1200, e.Operators));
    }

    [Fact]
    public void PredicatesFromOtherLoadsAreUntouched()
    {
        var e = new PrologEngine();
        e.ReconsultString("a(1).\n");
        e.ReconsultString("b(2).\n");
        Assert.True(e.Query("a(1), b(2).").Success);
    }

    [Fact]
    public void ABufferThatDeclaresItsOwnLibraryReloads()
    {
        // The heads are only knowable after the file's own directives have run:
        // until use_module executes, `ins` is not an operator and the text does
        // not parse at all. A reconsult that scanned the text first could not
        // load this even once.
        var e = new PrologEngine();
        const string src = """
            :- use_module(library(clpfd)).
            r(X) :- [X] ins 1..3, X #> 2.
            """;
        e.ReconsultString(src);
        e.ReconsultString(src);
        var s = e.Query("findall(X, (r(X), label([X])), L), length(L, N).");
        Assert.True(s.Success);
        Assert.Equal(1L, Assert.IsType<IntTerm>(s["N"]!).Value);
    }

    [Fact]
    public void DcgRulesAreReplacedToo()
    {
        // A DCG rule's real head is the TRANSLATED one (g//0 defines g/2);
        // the replacement scan read the whole rule as '-->'/2, which
        // abolished nothing — reloading a grammar buffer duplicated its
        // rules. WebShumway's Consult button and the REPL buffer both take
        // this path.
        var e = new PrologEngine();
        const string src = "g --> [a].\ng --> [b].\n";
        e.ReconsultString(src);
        e.ReconsultString(src);
        e.ReconsultString(src);
        var s = e.Query("findall(x, phrase(g, [a]), L), length(L, N).");
        Assert.True(s.Success);
        Assert.Equal(1L, Assert.IsType<IntTerm>(s["N"]!).Value);
        var all = e.Query(
            "findall(x, (member(W, [[a],[b]]), phrase(g, W)), L), length(L, N).");
        Assert.True(all.Success);
        Assert.Equal(2L, Assert.IsType<IntTerm>(all["N"]!).Value);
    }

    [Fact]
    public void DynamicPredicatesAreReplacedToo()
    {
        var e = new PrologEngine();
        const string src = ":- dynamic(d/1).\nd(1).\n";
        e.ReconsultString(src);
        e.Query("assertz(d(99)).");
        e.ReconsultString(src);
        var s = e.Query("findall(X, d(X), L).");
        Assert.True(s.Success);
        Assert.Equal("[1]", AstTermRenderer.Render(s["L"]!, 1200, e.Operators));
    }
}
