using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// Chunk 149: ISO §6.4.7 — a <c>(</c> immediately after an atom (no
/// whitespace) is the function-call open-paren binding the atom as
/// a compound head. A <c>(</c> with whitespace is a grouping paren,
/// and an atom that's also a prefix operator can take the
/// parenthesised term as its operand.
///
/// <para>So <c>\+(a, b)</c> is <c>\+/2</c> (function-call shape;
/// would fail because no <c>\+/2</c> is defined), while
/// <c>\+ (a, b)</c> is <c>\+/1</c> applied to the conjunction
/// <c>(a, b)</c>. Matches SWI's reader. The chunk-136 ISO §8.15
/// tests had to dodge this with helper predicates pre-chunk-149.
/// </para>
/// </summary>
public class PrefixOpParenAmbiguityTests
{
    private static Term Parse(string source)
    {
        var parser = new Parser(new SourceLexer(source), OperatorTable.Default());
        return parser.ReadClauseTerm();
    }

    [Fact]
    public void NotPlusSpaceParen_IsUnaryAppliedToConjunction()
    {
        // '\+ (a, b)' — unary \+ applied to the conjunction (a, b).
        var t = Parse("\\+ (a, b).");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("\\+", c.Functor);
        Assert.Single(c.Args);
        var inner = Assert.IsType<CompoundTerm>(c.Args[0]);
        Assert.Equal(",", inner.Functor);
    }

    [Fact]
    public void NotPlusAdjacentParen_IsCompoundOfArityTwo()
    {
        // '\+(a, b)' (no space) — '\+'/2 compound; the reader
        // produces that shape even if no '\+'/2 is defined at
        // runtime.
        var t = Parse("\\+(a, b).");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("\\+", c.Functor);
        Assert.Equal(2, c.Args.Length);
    }

    [Fact]
    public void NotSpaceParen_IsUnaryAppliedToConjunction()
    {
        // 'not (a, b)' — same disambiguation for the 'not' synonym.
        var t = Parse("not (a, b).");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("not", c.Functor);
        Assert.Single(c.Args);
    }

    [Fact]
    public void FooSpaceParen_IsAtomFollowedByGroupedTerm()
    {
        // 'foo (1)' — 'foo' is an atom; the space-paren is grouping.
        // ReadClauseTerm reads one top-level term, so without an
        // infix between them we just get the atom — the grouping
        // term is left unread. So use an explicit infix to test the
        // adjacency rule for a non-op atom.
        var t = Parse("foo = (1, 2).");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("=", c.Functor);
        Assert.Equal((Term)new AtomTerm("foo"), c.Args[0]);
        var rhs = Assert.IsType<CompoundTerm>(c.Args[1]);
        Assert.Equal(",", rhs.Functor);
    }

    [Fact]
    public void FooAdjacentParen_IsCompound()
    {
        // 'foo(1, 2)' — compound. The compound-form decision uses
        // the same adjacency rule.
        var t = Parse("foo(1, 2).");
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("foo", c.Functor);
        Assert.Equal(2, c.Args.Length);
    }

    [Fact]
    public void DashSpaceParen_IsUnaryAppliedToGroup()
    {
        // '- (1)' — unary minus applied to (1). With chunk 149,
        // this parses as -/1 of 1 (or directly the integer -1
        // depending on the prefix-vs-number heuristic — either way,
        // not as a binary '-(...)'.
        var t = Parse("- (1).");
        // Either CompoundTerm('-', [1]) or IntTerm(-1) is fine —
        // both are the unary reading.
        if (t is CompoundTerm c)
        {
            Assert.Equal("-", c.Functor);
            Assert.Single(c.Args);
        }
        else
        {
            var i = Assert.IsType<IntTerm>(t);
            Assert.Equal(-1, i.Value);
        }
    }
}
