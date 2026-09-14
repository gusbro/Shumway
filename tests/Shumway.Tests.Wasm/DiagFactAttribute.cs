using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A test that reads the tier's diagnostic counters. Those are
/// <c>[Conditional("SHUMWAY_DIAG")]</c>, so a stock build does not count and
/// every tally reads zero -- against which an assertion would either fail or,
/// worse, pass for the wrong reason. This skips instead, naming the build
/// that runs it: <c>dotnet test ... -p:ShumwayDiag=true</c>.
///
/// <para>A skip is the honest outcome here. The alternative that looks
/// tempting -- assert only when the counters exist -- turns the test green
/// in the build nobody measured, which is exactly the vacuous pass the
/// suite is meant to catch.</para></summary>
public sealed class DiagFactAttribute : FactAttribute
{
    public DiagFactAttribute()
    {
        if (!WasmTierDelegate.DiagCompiledIn)
            Skip = "reads the tier's diagnostic counters: build with -p:ShumwayDiag=true";
    }
}

/// <summary>The <see cref="DiagFactAttribute"/> of a parameterised test.</summary>
public sealed class DiagTheoryAttribute : TheoryAttribute
{
    public DiagTheoryAttribute()
    {
        if (!WasmTierDelegate.DiagCompiledIn)
            Skip = "reads the tier's diagnostic counters: build with -p:ShumwayDiag=true";
    }
}
