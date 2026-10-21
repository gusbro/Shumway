using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: a head argument gets an image from the head threshold
/// (<c>SHUMWAY_IMAGE_HEAD_THRESHOLD</c>), which is the body's threshold
/// unless set: an engine with images off has none in its heads either.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class HeadThresholdTests
{
    private static int Count(string src, Opcode wanted)
    {
        var code = new PredicateCompiler().Compile(new ClauseReader(src).ReadAll().ToList()).BytecodeUnfused;
        int n = 0;
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) break;
            if (op == wanted) n++;
            pc += size;
        }
        return n;
    }

    [Fact]
    public void AHeadOfFourCellsGetsAnImageUnderItsOwnThreshold()
    {
        const string src = "p(t(l, 1, r)).";   // t/3: four cells
        int body = ClauseCompiler.ImageThreshold;
        try
        {
            ClauseCompiler.ResetHeadThreshold();
            Assert.Equal(ClauseCompiler.ImageThreshold, ClauseCompiler.HeadThreshold);
            Assert.Equal(0, Count(src, Opcode.GetBlit));
            ClauseCompiler.HeadThreshold = 4;
            Assert.Equal(1, Count(src, Opcode.GetBlit));
            // The body's threshold alone turns the heads off; a threshold of
            // their own keeps them.
            ClauseCompiler.ResetHeadThreshold();
            ClauseCompiler.ImageThreshold = 0;
            Assert.Equal(0, ClauseCompiler.HeadThreshold);
            Assert.Equal(0, Count(src, Opcode.GetBlit));
            ClauseCompiler.HeadThreshold = 4;
            Assert.Equal(1, Count(src, Opcode.GetBlit));
        }
        finally
        {
            ClauseCompiler.ImageThreshold = body;
            ClauseCompiler.ResetHeadThreshold();
        }
    }
}
