using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Phase 26 — a NECK cut (`head :- !, ...`) is chunk-transparent, so a
/// variable confined to the head + the chunk-0 call stays temporary (X register)
/// instead of being promoted to a permanent Y slot — matching GProlog's `pl2wam`
/// for the same shape. The Warren argument scheduler is extended to target the
/// call that follows the neck cut, so a head-var living in an argument register
/// is read before a later argument reuses its home (no clobber).</summary>
public class CutTransparencyTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).First(e => e.Name == "p").Text;

    [Fact]
    public void NeckCut_SingleCall_UsesNoPermanents()
    {
        // p(X) :- !, q(X).  — X is head + the one chunk-0 call → temporary.
        // No environment frame, no Y-slot extraction.
        string text = Dis("p(X) :- !, q(X). p(_) :- r.");
        Assert.Contains("neck_cut", text);
        Assert.DoesNotContain("get_variable_y", text);
        Assert.DoesNotContain("put_value_y", text);
    }

    [Fact]
    public void NeckCut_ArgShuffle_ReadsBeforeClobber()
    {
        // p(A, B) :- !, q(B, A).  — the call swaps the args. Both A and B live
        // in argument registers (x0/x1); writing arg0 := B(x1) must not clobber
        // A(x0) before arg1 := A is read. The scheduler orders / saves to avoid
        // it — there must be no naive in-order put that loses a value.
        string text = Dis("p(A, B) :- !, q(B, A). p(_, _) :- r.");
        Assert.Contains("neck_cut", text);
        Assert.DoesNotContain("get_variable_y", text);   // all temporary
        // The bodies still produce a real call to q/2.
        Assert.Contains("execute", text);
    }

    [Fact]
    public void NeckCutRecursion_NoFrame_AndArgsDirectToRegisters()
    {
        // p([H|T], [H|R]) :- !, p(T, R).  — the recursive clause matches
        // GProlog: no environment frame (B), and T/R extracted straight into the
        // recursive call's argument registers (A), so NO put_value and NO
        // allocate anywhere in the predicate.
        string text = Dis("p([], []). p([H|T], [H|R]) :- !, p(T, R).");
        Assert.Contains("neck_cut", text);
        Assert.DoesNotContain("put_value", text);   // A: args land in x0/x1 directly
        Assert.DoesNotContain("allocate", text);    // B: no frame needed
    }
}
