using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A predicate of one instruction is padded with nop to
/// <c>PredicateCompiler.MinimumLength</c>. The tier takes the padding as a
/// no-op, as the IL tier does, rather than leave the predicate on the
/// interpreter.</summary>
public sealed class PaddedPredicateTests
{
    [Fact]
    public void AOneInstructionPredicateCompilesToTheTier()
    {
        var (engine, members) = TieredEngine.Build("""
            tiny(_).
            use(X) :- tiny(X).
            """);

        Assert.True(engine.Query("use(a).").Success);
        // tiny/1 is the member whose code is a proceed and its padding.
        Assert.Contains(members, m =>
            m.Predicate.Bytecode.Length > 1
            && m.Predicate.Bytecode[0] == (byte)Shumway.Core.Opcode.Proceed
            && m.Predicate.Bytecode[1] == (byte)Shumway.Core.Opcode.Nop);
    }
}
