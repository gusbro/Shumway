using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class CodeSpaceTests
{
    // From the split up the program view reads the query overlay: code
    // appended there would never run, and what runs instead is the query's.
    [Fact]
    public void AppendCodeStopsAtTheQueryOverlay()
    {
        var engine = new Activation
        {
            CurrentProgram = new byte[64],
            CurrentQueryOverlay = new byte[8],
            CurrentQuerySplit = 100,
        };
        engine.SetInitialProgramLength(40);

        Assert.Equal(40, engine.AppendCode(new byte[60]));
        Assert.Equal(0, engine.CodeRoom);
        Assert.True(engine.CurrentProgram!.Length <= 100, "grew past the split");

        var ex = Assert.Throws<PrologRuntimeException>(() => engine.AppendCode(new byte[1]));
        Assert.Equal("resource_error", ex.Kind);
        Assert.Equal("code_space", ex.Detail);
        Assert.Equal(100, engine.ProgramLength);
    }

    [Fact]
    public void WithoutAnOverlayTheProgramKeepsGrowing()
    {
        var engine = new Activation { CurrentProgram = new byte[64] };
        engine.SetInitialProgramLength(40);

        Assert.Equal(40, engine.AppendCode(new byte[200]));
        Assert.Equal(240, engine.ProgramLength);
        Assert.Equal(int.MaxValue, engine.CodeRoom);
    }
}
