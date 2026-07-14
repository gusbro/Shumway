using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-035 — the depth, counted no further than it has to be.
///
/// <para>A step's condition compares the depth of the port it is at against the depth the step
/// was taken FROM, so every port deeper than that is uninteresting — but ASKING costs a walk of
/// the environment chain, and a step over a goal that runs for a while passes millions of ports
/// at whatever depth that goal reaches. Stepping over one goal of a real program took 140
/// seconds against the 20 it takes to run: not the program, the counting.</para>
/// </summary>
public class EnvDepthCappedTests
{
    private static Activation Nested(int depth)
    {
        var engine = new Activation();
        for (int i = 0; i < depth; i++)
            engine.Allocate(1);
        return engine;
    }

    [Fact]
    public void ItIsExactUpToTheCap()
    {
        var engine = Nested(5);
        Assert.Equal(5, engine.EnvDepth);
        Assert.Equal(5, engine.EnvDepthCapped(5));
        Assert.Equal(5, engine.EnvDepthCapped(100));
    }

    [Fact]
    public void AndBeyondItSaysOnlyThatItIsDeeper()
    {
        var engine = Nested(500);

        // The answer past the cap is not the depth — it is "more than you asked about", which
        // is all a step condition needs, and it costs three loads instead of five hundred.
        Assert.Equal(3, engine.EnvDepthCapped(2));
        Assert.True(engine.EnvDepthCapped(2) > 2);
        Assert.Equal(500, engine.EnvDepth);   // the real one, when somebody really wants it
    }

    [Fact]
    public void AnEmptyStackHasNoDepthAtAnyCap()
    {
        var engine = new Activation();
        Assert.Equal(0, engine.EnvDepth);
        Assert.Equal(0, engine.EnvDepthCapped(1));
        Assert.Equal(0, engine.EnvDepthCapped(1000));
    }
}
