using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>'$stream'(X) with X unbound is not a stream-term, but every
/// stream-term is an instance of it. So what is wrong with it is the missing
/// binding, and the error has to be one that binding could make good:
/// instantiation_error, not domain_error(stream_or_alias, ...). stc#72 puts it
/// as the rule this engine now follows -- (a) a variable OR a non-ground term
/// with instances that are stream-terms is instantiation_error; (d) a term
/// with no instance that is a stream-term is the domain error.
///
/// <para>This is the same principle as the closed-stream round: an error must
/// describe the situation it is in. GNU still answers domain_error here, so it
/// is not the reference for this one; the rationale is.</para></summary>
public sealed class PartialStreamTermTests
{
    private static bool Raises(string goal, string ball)
    {
        var e = new PrologEngine();
        return e.Query($"catch(({goal}), error({ball}, _), true).").Success;
    }

    /// <summary>Every stream position, not just close/1: they share one
    /// resolver and must not drift apart.</summary>
    [Theory]
    [InlineData("close(S)")]
    [InlineData("set_input(S)")]
    [InlineData("set_output(S)")]
    [InlineData("write(S, a)")]
    [InlineData("current_input(S)")]
    [InlineData("current_output(S)")]
    [InlineData("stream_property(S, _)")]
    public void APartialStreamTerm_IsInstantiationNotDomain(string goal)
    {
        Assert.True(Raises($"S = '$stream'(_), {goal}", "instantiation_error"));
    }

    /// <summary>ANTI-VACUITY: a term no binding can rescue keeps its domain
    /// error, so the change did not turn every stream complaint into
    /// instantiation. Covers stc#72's rule (d) explicitly with f(X): non-ground,
    /// yet no instance of it is a stream-term.</summary>
    [Theory]
    [InlineData("'$stream'(foo)")]
    [InlineData("'$stream'(-1)")]
    [InlineData("'$stream'(a, b)")]
    [InlineData("f(_)")]
    [InlineData("1 + 1")]
    public void ATermNoBindingCanRescue_KeepsItsDomainError(string culprit)
    {
        Assert.True(Raises($"close({culprit})",
                           "domain_error(stream_or_alias, _)"));
    }

    /// <summary>ANTI-VACUITY: the unchanged neighbours. A bare variable was
    /// always instantiation_error, and a real stream still works.</summary>
    [Fact]
    public void TheSurroundingCasesAreUnchanged()
    {
        var e = new PrologEngine();
        Assert.True(Raises("close(_)", "instantiation_error"));
        Assert.True(e.Query("current_input(S), S = '$stream'(N), integer(N).")
                     .Success);
        Assert.True(e.Query("current_output(S), stream_property(S, mode(_)).")
                     .Success);
    }
}
