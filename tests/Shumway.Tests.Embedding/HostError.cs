using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An error a builtin raised, as the host receives it from a query:
/// the embedding exception with the ball, and the builtin's own exception as
/// its inner one.</summary>
internal static class HostError
{
    public static PrologRuntimeException Raised(Func<object?> run)
        => Inner(Assert.Throws<ShumwayPrologException>(run));

    public static PrologRuntimeException Raised(Action run)
        => Inner(Assert.Throws<ShumwayPrologException>(run));

    private static PrologRuntimeException Inner(ShumwayPrologException ex)
        => Assert.IsType<PrologRuntimeException>(ex.InnerException);
}
