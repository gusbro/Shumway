using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// The Phase 20 profiler is gated on the SHUMWAY_PROFILE compile
/// constant. The test suite builds without it, so here we only assert
/// the disabled-build contract: the hooks are stripped (Enabled is
/// false) and Report returns nothing. The recording paths themselves
/// are exercised by hand via a profiling build (dotnet build
/// -p:ShumwayProfile=true) — they can't be unit-tested in a stripped
/// build because the [Conditional] calls don't exist.
/// </summary>
public class ProfilerTests
{
    [Fact]
    public void DisabledBuild_ReportsNotEnabled()
    {
        Assert.False(Profiler.Enabled);
    }

    [Fact]
    public void DisabledBuild_ReportIsEmpty()
    {
        // Hooks are no-ops here; the report short-circuits to "".
        Profiler.Reset();
        Profiler.Opcode(0x01);
        Profiler.Backtrack();
        Profiler.StopRun();
        Assert.Equal(string.Empty, Profiler.Report());
    }
}
