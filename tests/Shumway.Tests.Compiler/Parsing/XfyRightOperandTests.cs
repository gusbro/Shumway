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

    private static string Canonical(Term t) => t switch
    {
        CompoundTerm c => c.Functor + "(" + string.Join(",",
            System.Array.ConvertAll(c.Args, Canonical)) + ")",
        AtomTerm a => a.Name,
        IntTerm i => i.Value.ToString(),
        _ => t.ToString() ?? "?",
    };
}
