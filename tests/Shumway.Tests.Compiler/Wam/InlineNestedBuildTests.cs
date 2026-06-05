using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-019 / ADR-020 — a nested compound is built (write) inline with
/// <c>unify_structure</c> / <c>unify_list</c>, continuing the same unify stream
/// instead of deferring to a temporary register + a separate <c>get_structure</c>
/// / <c>get_list</c> per nesting level. ADR-019 covers the LAST argument position
/// (linear, no resume); ADR-020 extends it to NON-last positions in body building
/// via the reserve-upfront roots <c>put_structure_r</c> / <c>put_list_r</c> and a
/// runtime write-pointer frame stack.</summary>
public class InlineNestedBuildTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).First(e => e.Name == "p").Text;

    [Fact]
    public void ListLiteral_BuildsWithInlineUnifyList()
    {
        // X = [a, b, c]  → get_list; unify_atom a; unify_list; unify_atom b;
        //                  unify_list; unify_atom c; unify_nil.  No temp, no
        //                  second get_list.
        string text = Dis("p(X) :- X = [a, b, c].");
        Assert.Equal(2, text.Split("unify_list").Length - 1);  // two nested conses
        Assert.DoesNotContain("get_structure", text);
        // The only list-open is the outer one (get_list); the tails are inline.
        Assert.Equal(1, text.Split("get_list").Length - 1);
    }

    [Fact]
    public void NestedStructure_LastArg_BuildsWithUnifyStructure()
    {
        // X = wrap(box(N))  → get_structure wrap; unify_structure box; unify_value N.
        string text = Dis("p(X, N) :- X = wrap(box(N)).");
        Assert.Contains("unify_structure", text);
    }

    [Fact]
    public void NestedStructure_NonLastArg_BuildsInlineReserved()
    {
        // ADR-020: foo(bar(x), y) has bar(x) in NON-last position. It is now
        // built inline via the reserve-upfront root (put_structure_r) + a nested
        // unify_structure, with the write-pointer frame stack resuming foo's
        // second arg after bar completes — no temp, no deferred get_structure.
        string text = Dis("p :- q(foo(bar(x), y)).");
        Assert.Contains("put_structure_r", text);
        Assert.Contains("unify_structure", text);  // bar(x) inline
        Assert.DoesNotContain("get_structure", text);
    }
}
