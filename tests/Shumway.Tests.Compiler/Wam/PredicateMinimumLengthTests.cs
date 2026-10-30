using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Every compiled predicate takes at least
/// <see cref="PredicateCompiler.MinimumLength"/> bytes, the room a reload inside
/// a running query needs to redirect the old entry with <c>execute</c>. A
/// predicate of one instruction is padded with nop after it, where nothing
/// runs.</summary>
public class PredicateMinimumLengthTests
{
    private static CompiledPredicate Compile(string src) =>
        new PredicateCompiler().Compile(new ClauseReader(src).ReadAll().ToList());

    [Theory]
    [InlineData(@"p.")]
    [InlineData(@"p(_).")]
    [InlineData(@"p(_, _).")]
    public void AFactOfOneProceedIsPaddedWithNop(string source)
    {
        var cp = Compile(source);

        Assert.Equal(PredicateCompiler.MinimumLength, cp.Bytecode.Length);
        Assert.Equal((byte)Opcode.Proceed, cp.Bytecode[0]);
        Assert.All(cp.Bytecode.Skip(1), b => Assert.Equal((byte)Opcode.Nop, b));
    }

    [Theory]
    [InlineData(@"p :- true.")]
    [InlineData(@"p(_) :- fail.")]
    [InlineData(@"p(a).")]
    [InlineData(@"p(X) :- X > 1.")]
    [InlineData(@"p(a). p(b).")]
    public void EveryPredicateTakesAtLeastTheMinimum(string source)
    {
        Assert.True(Compile(source).Bytecode.Length >= PredicateCompiler.MinimumLength);
    }

    [Fact]
    public void APredicateLongEnoughIsLeftAsItIs()
    {
        var cp = Compile(@"p(a).");

        Assert.Equal((byte)Opcode.Proceed, cp.Bytecode[^1]);
    }
}
