using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression: a predicate body with more live temporaries than
/// the engine's initial X-register bank (default 64) used to
/// crash with <c>IndexOutOfRangeException</c> at the first
/// <c>UnifyVariableX</c> or <c>PutVariableX</c> referencing the
/// over-the-limit register. Blint.pl's <c>blint/2</c> body (the
/// real-world surfacer of this bug) compiles to a 30+-goal
/// sequence with many cross-goal variable threads — its register
/// peak well exceeds 64. The fix grows the bank on demand on
/// <c>Activation.SetRegister</c> and on <c>PushChoicePoint</c>.
/// </summary>
public class RegisterAutoGrowTests
{
    [Fact]
    public void HighArityCall_DoesNotCrash()
    {
        var e = new PrologEngine();
        // Compose a predicate that calls a 100-arity goal —
        // requires 100 X-registers at the call site, well past
        // the default 64.
        var args = string.Join(", ", Enumerable.Range(0, 100).Select(i => "x"));
        e.ConsultString(
            ":- public big_caller/0.\n"
            + ":- public big_pred/100.\n"
            + $"big_pred({args}) :- true.\n"
            + $"big_caller :- big_pred({args}).\n");
        Assert.True(e.Query("big_caller.").Success);
    }

    [Fact]
    public void BodyWithManyConjuncts_AllRegistersAllocated()
    {
        // Many conjuncts each binding fresh variables. The WAM
        // compiler allocates X-registers per goal — with enough
        // distinct variables threading through, the live-X-register
        // count exceeds the default bank.
        var e = new PrologEngine();
        var body = string.Join(",\n  ",
            Enumerable.Range(0, 80).Select(i => $"X{i} = {i}"));
        e.ConsultString(
            ":- public wide/0.\n"
            + $"wide :-\n  {body}.\n");
        Assert.True(e.Query("wide.").Success);
    }
}
