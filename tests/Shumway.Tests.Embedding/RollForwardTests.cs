using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A framework-dependent executable <c>shumway-link --exe</c>
/// builds runs on .NET 10, or on a later major version when 10 is not
/// installed, as the toolchain's own executables do: its project rolls
/// forward to the next major. A self-contained one carries its runtime and
/// the setting does not apply.</summary>
public sealed class RollForwardTests
{
#if !NETFRAMEWORK
    [Theory]
    [InlineData(ExecutableDeploymentMode.FrameworkDependent)]
    [InlineData(ExecutableDeploymentMode.SelfContained)]
    public void AnExecutablesProjectRollsForwardToTheNextMajor(ExecutableDeploymentMode mode)
    {
        string project = ExecutableEmitter.GenerateProjectFile("app", "win-x64", mode,
            Array.Empty<string>(), Array.Empty<string>());
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project);
        Assert.Contains("<RollForward>Major</RollForward>", project);
    }
#endif
}
