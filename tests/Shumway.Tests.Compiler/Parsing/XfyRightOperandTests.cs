using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>The RIGHT operand of an <c>xfy</c> operator sits in a y position:
/// it may reach the operator's own priority. The chain reader read every
/// operand one below that — right for the ones that are the left operand of
/// the next nesting, wrong for the last — so a prefix operator of exactly
/// that priority was rejected there (Neumerkel syntax #163). The expected
/// trees below are SWI's, read back from the same sources.</summary>
public class XfyRightOperandTests
{
    private static Term Parse(string source)
    {
        var ops = OperatorTable.Default();
        ops.Define("p", 9, OperatorType.Fy);
        ops.Define("p", 9, OperatorType.Xfy);
        return new Parser(new SourceLexer(source), ops, new PrologFlags())
            .ReadClauseTerm();
    }

    [Theory]
    // The suite's own case: an infix p whose right operand is a chain of
    // prefix p, all at priority 9.
    [InlineData("1 p p p 2", "p(1,p(p(2)))")]
    [InlineData("1 p p 2", "p(1,p(2))")]
    // Unchanged shapes, as the guard against reading too much.
    [InlineData("1 p 2", "p(1,2)")]
    [InlineData("p p 2", "p(p(2))")]
    // The prefix operand takes the rest with it: at priority 9 its own
    // operand may hold the infix p again.
    [InlineData("1 p p 2 p 3", "p(1,p(p(2,3)))")]
    public void PrefixOperandOfTheOperatorsOwnPriorityIsAccepted(
        string source, string canonical)
    {
        Assert.Equal(canonical, Canonical(Parse(source + " .")));
    }

    /// <summary>The chain reader exists so a long xfy chain — a clause body's
    /// commas — costs no recursion per element. That is the OTHER half of the
    /// contract, and a rewrite that restores the y position by going back to
    /// the recursive reader would break it silently, so both halves are
    /// pinned together: a thousand-goal conjunction reads, and reads as the
    /// right-nested tree.</summary>
    [Fact]
    public void ALongChainStillReadsWithoutRecursionPerElement()
    {
        const int n = 1000;
        var sb = new System.Text.StringBuilder("x0");
        for (int i = 1; i < n; i++) sb.Append(", x").Append(i);
        Term t = Parse(sb.ToString() + " .");
        // Right-nested: ','(x0, ','(x1, ... x999)).
        for (int i = 0; i < n - 1; i++)
        {
            var c = Assert.IsType<CompoundTerm>(t);
            Assert.Equal(",", c.Functor);
            Assert.Equal("x" + i, Assert.IsType<AtomTerm>(c.Args[0]).Name);
            t = c.Args[1];
        }
        Assert.Equal("x" + (n - 1), Assert.IsType<AtomTerm>(t).Name);
    }

    /// <summary>The tail of an xfy chain may still carry another operator of
    /// the chain's own priority — the case the chain reader handles by
    /// continuing the term after the operand. Kept beside the y-position
    /// cases: both are ways a term of priority opPrec reaches the last
    /// operand, and a fix for one must not lose the other.</summary>
    [Theory]
    [InlineData("1 p 2 q 3", "p(1,q(2,3))")]
    [InlineData("1 p 2 p 3 q 4", "p(1,p(2,q(3,4)))")]
    public void AnotherOperatorOfTheSamePriorityStillEndsTheChain(
        string source, string canonical)
    {
        var ops = OperatorTable.Default();
        ops.Define("p", 9, OperatorType.Fy);
        ops.Define("p", 9, OperatorType.Xfy);
        ops.Define("q", 9, OperatorType.Xfx);
        Term t = new Parser(new SourceLexer(source + " ."), ops, new PrologFlags())
            .ReadClauseTerm();
        Assert.Equal(canonical, Canonical(t));
    }

    private static string Canonical(Term t) => t switch
    {
        CompoundTerm c => c.Functor + "(" + string.Join(",",
            System.Array.ConvertAll(c.Args, Canonical)) + ")",
        AtomTerm a => a.Name,
        IntTerm i => i.Value.ToString(),
        _ => t.ToString() ?? "?",
    };
}
