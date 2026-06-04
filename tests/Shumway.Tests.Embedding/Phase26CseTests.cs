using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Phase 26 — common-subexpression elimination: a sub-term identical
/// (incl. variable names) to a top-level head-argument compound already matched
/// into an argument register is referenced via <c>unify_value</c> instead of
/// being rebuilt. <c>unify_value</c> is mode-sensitive, so this is correct both
/// when the output is built (write) and when it is matched (read), and the
/// shared variable propagates. (Even GProlog rebuilds here.)</summary>
public class Phase26CseTests
{
    private static PrologEngine Load(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    private const string Prog =
        "tok(token(L, eof), _, [token(L, eof)]) :- !.\n" +
        "tok(_, _, []).\n";

    [Fact]
    public void Cse_BuildsOutput_FromSharedInput()
    {
        // Write mode: the output [token(5,eof)] is produced by referencing the
        // matched arg0 structure (checked by unification, format-independent).
        Assert.True(Load(Prog).Query("tok(token(5, eof), x, R), R = [token(5, eof)].").Success);
    }

    [Fact]
    public void Cse_MatchesBoundOutput_AndRejectsMismatch()
    {
        var engine = Load(Prog);
        // Read mode: a matching bound output succeeds.
        Assert.True(engine.Query("tok(token(5, eof), x, [token(5, eof)]).").Success);
        // A differing sub-term (9 vs 5) must be rejected by the unify_value.
        Assert.False(engine.Query("tok(token(5, eof), x, [token(9, eof)]).").Success);
    }

    [Fact]
    public void Cse_SharesTheVariable()
    {
        // The matched arg0 and the referenced output share the same variable,
        // so binding it afterwards shows through both.
        Assert.True(Load(Prog)
            .Query("tok(token(A, eof), x, R), A = 7, R = [token(7, eof)].").Success);
    }
}
