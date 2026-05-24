using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// Chunk 146: the parser's "split a graphic-atom token when it
/// isn't a registered infix" pass. The lexer reads a maximal run
/// of graphic characters, so <c>1+-2</c> tokenises as
/// <c>Int(1) Atom("+-") Int(2)</c>. With no infix <c>+-</c> defined
/// the parser tries the longest infix prefix and pushes back the
/// remainder so the right operand can pick it up as a prefix op —
/// matching SWI's reader. The user's 2016 GProlog year-arithmetic
/// program generates exactly these adjacent-operator chains.
/// </summary>
public class OperatorSplitTests
{
    private static Term Parse(string source)
    {
        var parser = new Parser(new SourceLexer(source), OperatorTable.Default());
        return parser.ReadClauseTerm();
    }

    [Fact]
    public void OnePlusMinusOne_SplitsIntoBinaryPlusUnaryMinus()
    {
        // 1+-1 ≡ +(1, -1) (binary + applied to 1 and the negative
        // integer literal -1). The parser may treat '-1' as either a
        // literal IntTerm(-1) or as '-'(IntTerm(1)) — both are
        // structurally valid; pin the outer + only.
        var t = Parse("1+-1.");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("+", c.Functor);
        Assert.Equal(2, c.Args.Length);
        Assert.Equal((Term)new IntTerm(1), c.Args[0]);
    }

    [Fact]
    public void TwoTimesMinusThree_SplitsIntoBinaryTimesUnaryMinus()
    {
        var t = Parse("2*-3.");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("*", c.Functor);
        Assert.Equal((Term)new IntTerm(2), c.Args[0]);
    }

    [Fact]
    public void OnePlusMinusVar_StillSplits()
    {
        // 1+-X ≡ +(1, -(X)).
        var t = Parse("1+-X.");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("+", c.Functor);
        Assert.Equal(2, c.Args.Length);
    }

    [Fact]
    public void WholeAtomIsInfix_NoSplit()
    {
        // '=..' IS a registered infix — should NOT be split.
        var t = Parse("X =.. [foo, 1, 2].");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("=..", c.Functor);
    }
}
