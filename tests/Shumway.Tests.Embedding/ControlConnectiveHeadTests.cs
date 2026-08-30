using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A clause whose head is a control connective — `a,b.` reads as a
/// clause FOR ','/2 — is undispatchable by construction: the compiler lowers
/// those functors inline, so the stored clauses were dead weight listing/0
/// showed and nothing could call. Consult now refuses them the way assertz/1
/// always did (permission_error, §8.9.2.3), reporting and loading on.</summary>
public sealed class ControlConnectiveHeadTests
{
    [Theory]
    [InlineData("a,b.", "(,)/2")]
    [InlineData("c;d.", "(;)/2")]
    [InlineData("(e -> f).", "(->)/2")]
    [InlineData("!.", "(!)/0")]
    public void Consult_RefusesConnectiveHeads_AndLoadsOn(string clause, string indicator)
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultString(clause + "\nok_after.\n");
        Assert.Contains($"no permission to modify static procedure {indicator}", warnings.ToString());
        // The rest of the file still loaded, and the refused clause left nothing.
        Assert.True(e.Query("ok_after.").Success);
    }

    [Fact]
    public void Assertz_StillRaisesThePermissionError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(assertz((x,y)), error(permission_error(modify, static_procedure, ','/2), _), true).")
            .Success);
    }
}
