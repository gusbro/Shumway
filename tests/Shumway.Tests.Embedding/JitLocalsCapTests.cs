using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The JIT's cap on locals is read the way the JIT reads it, and
/// the planner's default target follows it.</summary>
public sealed class JitLocalsCapTests
{
    [Theory]
    [InlineData(null, 0x400)]
    [InlineData("", 0x400)]
    [InlineData("0", 0x400)]
    [InlineData("zz", 0x400)]
    [InlineData("0x2000", 0x2000)]
    [InlineData("0XC00", 0xC00)]
    [InlineData("c00", 0xC00)]
    [InlineData(" 1600", 0x1600)]
    [InlineData("3072", 0x3072)]      // the runtime parses hex: not three thousand
    [InlineData("12zz", 0x12)]        // the leading digits, as the runtime takes them
    public void TheCapIsParsedAsTheRuntimeParsesIt(string? value, int cap)
        => Assert.Equal(cap, IlJitLimits.JitLocalsCap(value));

    [Fact]
    public void TheProcessCapAndTheDefaultTargetAgree()
    {
        string? set = Environment.GetEnvironmentVariable("DOTNET_JitMaxLocalsToTrack")
            ?? Environment.GetEnvironmentVariable("COMPlus_JitMaxLocalsToTrack");
        Assert.Equal(IlJitLimits.JitLocalsCap(set), IlJitLimits.InlineLocals);
        Assert.Equal(IlJitLimits.InlineLocals - IlRegionCost.CapSlack, IlRegionCost.DefaultLocalsTarget);
        if (set is null) Assert.Equal(1000, IlRegionCost.DefaultLocalsTarget);
    }
}
