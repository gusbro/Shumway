using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// ISO §6.3.1.3: a bare operator-atom used as the OPERAND of an operator has
/// the operator's own priority, so it cannot sit where a lower-priority term
/// is required — it must be parenthesised. But it stays valid as a delimited
/// argument / list element, when quoted, or when parenthesised.
/// </summary>
public class OperatorAtomOperandTests
{
    private static Term Parse(string source) =>
        new Parser(new SourceLexer(source), OperatorTable.Default()).ReadClauseTerm();

    [Theory]
    [InlineData("- -")]          // '-' as operand of prefix '-' (needs ≤ 200, has 500)
    [InlineData("- - -")]
    [InlineData("a * *")]        // '*' as right operand of '*' (needs ≤ 399, has 400)
    public void BareOperatorAtom_AsOperatorOperand_IsRejected(string source)
    {
        Assert.Throws<ParseException>(() => Parse(source + " ."));
    }

    [Theory]
    [InlineData("f(:-)")]        // operator-atom as a functor argument (max 999)
    [InlineData("[:-, -]")]      // …and as list elements
    [InlineData("f(;, '|', ';;')")]
    [InlineData("X = '<'")]      // quoted operator-atom is a plain atom
    [InlineData("- (a)")]        // parenthesised / compound operand
    [InlineData("a < b")]        // '<' used AS the infix operator
    public void OperatorAtom_AsArgumentOrQuotedOrParenthesised_IsAccepted(string source)
    {
        var t = Parse(source + " .");
        Assert.NotNull(t);
    }
}
