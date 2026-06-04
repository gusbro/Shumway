using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Phase 26 — `X = T` for a temporary variable compiles inline as
/// head-style get / unify instead of a call to the =/2 builtin (mirrors
/// GProlog's `X = [A|B]` → get_list + unify).</summary>
public class InlineUnifyTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).Single().Text;

    [Fact]
    public void VarEqualsList_CompilesToGetListAndUnify_NotACall()
    {
        // X is the head arg (a seen temp): unify it against [A|B] in place.
        string text = Dis("p(X, A, B) :- X = [A|B].");
        Assert.Contains("get_list", text);
        Assert.Contains("unify_value_x", text);
        Assert.DoesNotContain("execute", text);   // no tail call to =/2
        Assert.DoesNotContain("call", text);
    }

    [Fact]
    public void VarEqualsVar_CompilesToGetValue_NotACall()
    {
        string text = Dis("q(X, Y) :- X = Y.");
        Assert.Contains("get_value_x", text);
        Assert.DoesNotContain("execute", text);
        Assert.DoesNotContain("call", text);
    }

    [Fact]
    public void FirstOccurrenceVar_BuildsTheTerm()
    {
        // X is first-occurrence and confined to one chunk (temp): X := f(A,B)
        // builds the structure into X's home, no =/2 call.
        string text = Dis("r(A, B) :- X = f(A, B).");
        Assert.Contains("put_structure", text);
        Assert.DoesNotContain("execute", text);
        Assert.DoesNotContain("call", text);
    }

    [Fact]
    public void PermanentVar_FallsBackToBuiltin()
    {
        // X spans two goals (a call between) → permanent (Y) → not inlined yet;
        // the =/2 builtin call is kept (safe fallback).
        string text = Dis("u(A, B) :- X = [A|B], s(X).");
        Assert.Contains("put_list", text);   // built then passed to the =/2 call
    }
}
