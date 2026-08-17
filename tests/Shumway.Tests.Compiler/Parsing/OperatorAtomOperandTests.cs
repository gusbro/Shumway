using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// ISO §6.3.1.3: an operator-atom used as the OPERAND of an operator has the
/// operator's own priority, so it cannot sit where a lower-priority term is
/// required — it must be parenthesised. Quoting does NOT exempt it: quotes
/// change the token, not the atom, so <c>X = '&lt;'</c> is the same error as
/// <c>X = *</c> (Neumerkel syntax #106). It stays valid as a delimited
/// argument / list element or when parenthesised; <c>arity_compat</c> and the
/// SWI-lenient flag restore the quoted acceptance.
/// </summary>
public class OperatorAtomOperandTests
{
    private static Term Parse(string source, PrologFlags? flags = null) =>
        new Parser(new SourceLexer(source), OperatorTable.Default(),
                flags ?? new PrologFlags())
            .ReadClauseTerm();

    [Theory]
    [InlineData("- -")]          // '-' as operand of prefix '-' (needs ≤ 200, has 500)
    [InlineData("- - -")]
    [InlineData("a * *")]        // '*' as right operand of '*' (needs ≤ 399, has 400)
    [InlineData("X = '<'")]      // QUOTED operator-atom: same atom, same error
    [InlineData(@"X = '\\'")]    // Neumerkel syntax #106
    public void OperatorAtom_AsOperatorOperand_IsRejected(string source)
    {
        Assert.Throws<ParseException>(() => Parse(source + " ."));
    }

    [Theory]
    [InlineData("f(:-)")]        // operator-atom as a functor argument (max 999)
    [InlineData("[:-, -]")]      // …and as list elements
    [InlineData("f(;, '|', ';;')")]
    [InlineData("X = ('<')")]    // the conforming spelling: parenthesised
    [InlineData(@"X = ('\\')")]
    [InlineData("- (a)")]        // parenthesised / compound operand
    [InlineData("a < b")]        // '<' used AS the infix operator
    public void OperatorAtom_AsArgumentOrParenthesised_IsAccepted(string source)
    {
        var t = Parse(source + " .");
        Assert.NotNull(t);
    }

    [Theory]
    [InlineData("X = '<'")]
    [InlineData(@"X = '\\'")]
    public void QuotedOperatorAtomOperand_AcceptedUnderArityCompat(string source)
    {
        var flags = new PrologFlags { ArityCompat = true };
        Assert.NotNull(Parse(source + " .", flags));
    }
}
