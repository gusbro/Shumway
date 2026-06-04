using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Phase 26 — a fully-literal arithmetic expression in <c>X is Expr</c>
/// is evaluated at compile time and delivered as a direct unification of the
/// target with the resulting literal, instead of an <c>a_int_*</c>/<c>a_eval_*</c>
/// runtime computation.</summary>
public class ConstantFoldingTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).First(e => e.Name == "p").Text;

    [Fact]
    public void ConstantProduct_FoldsToDirectUnify()
    {
        // p(A) :- A is 1*2.  →  A unified with 2 (get_integer), no arithmetic op.
        string text = Dis("p(A) :- A is 1*2.");
        Assert.Contains("get_integer", text);
        Assert.DoesNotContain("a_int_bin", text);
        Assert.DoesNotContain("a_eval", text);
    }

    [Fact]
    public void NestedConstant_RespectsPrecedence()
    {
        // 3 + 4 * 5 = 23 (not 35) — folded with the runtime evaluator.
        string text = Dis("p(X) :- X is 3 + 4 * 5.");
        Assert.Contains("get_integer  [23", text);
    }

    [Fact]
    public void ExpressionWithAVariable_IsNotFolded()
    {
        // A free operand → no fold; the fused integer op stays.
        string text = Dis("p(X, N) :- X is N * 2.");
        Assert.Contains("a_int_bin", text);
        Assert.DoesNotContain("get_integer", text);
    }

    [Fact]
    public void ZeroDivisor_IsNotFolded_LeftToRuntime()
    {
        // 1//0 would raise at evaluation — must NOT be folded away; the runtime
        // path (a_int_bin / a_eval) is kept so the error fires when executed.
        string text = Dis("p(X) :- X is 1 // 0.");
        Assert.True(text.Contains("a_int_bin") || text.Contains("a_eval"),
            "a zero-divisor expression must keep its runtime evaluation, not fold");
    }
}
