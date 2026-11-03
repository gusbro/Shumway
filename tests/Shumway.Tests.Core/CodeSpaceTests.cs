using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>The code buffer grows by doubling up to <see cref="Activation.CodeLimit"/>,
/// past which an append is <c>resource_error(code_space)</c>: addresses stay
/// below the resume markers, and a host may set the limit lower.</summary>
public class CodeSpaceTests
{
    [Fact]
    public void AppendCodeStopsAtTheCodeLimit()
    {
        var engine = new Activation { CurrentProgram = new byte[64], CodeLimit = 100 };
        engine.SetInitialProgramLength(40);

        Assert.Equal(40, engine.AppendCode(new byte[60]));
        Assert.Equal(0, engine.CodeRoom);
        Assert.True(engine.CurrentProgram!.Length <= 100, "grew past the limit");

        var ex = Assert.Throws<PrologRuntimeException>(() => engine.AppendCode(new byte[1]));
        Assert.Equal("resource_error", ex.Kind);
        Assert.Equal("code_space", ex.Detail);
        Assert.Equal(100, engine.ProgramLength);
    }

    [Fact]
    public void TheLimitIsTheResumeMarkersUnlessSet()
    {
        var engine = new Activation { CurrentProgram = new byte[64] };
        engine.SetInitialProgramLength(40);

        Assert.Equal(Activation.ResumeMarkerBase, engine.CodeLimit);
        Assert.Equal(40, engine.AppendCode(new byte[200]));
        Assert.Equal(240, engine.ProgramLength);
        Assert.Equal(Activation.ResumeMarkerBase - 240, engine.CodeRoom);
    }

    [Fact]
    public void GrowthDoublesTheArrayUpToTheLimit()
    {
        var engine = new Activation { CurrentProgram = new byte[64], CodeLimit = 1000 };
        engine.SetInitialProgramLength(0);

        Assert.Equal(0, engine.AppendCode(new byte[10]));
        Assert.Equal(64, engine.CurrentProgram!.Length);     // fits: no growth
        Assert.Equal(10, engine.AppendCode(new byte[60]));
        Assert.Equal(128, engine.CurrentProgram!.Length);    // doubled
        Assert.Equal(70, engine.AppendCode(new byte[900]));
        Assert.Equal(970, engine.CurrentProgram!.Length);    // what was needed, under the limit
    }
}
