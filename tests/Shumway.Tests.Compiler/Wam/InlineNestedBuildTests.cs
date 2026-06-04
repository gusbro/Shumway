using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-019 — a nested compound in the LAST argument position is built
/// (write) / matched (read) inline with <c>unify_structure</c> / <c>unify_list</c>,
/// continuing the same unify stream instead of deferring to a temporary register
/// + a separate <c>get_structure</c> / <c>get_list</c> per nesting level
/// (matching GProlog). A non-last nested compound keeps the BFS.</summary>
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
    public void NestedStructure_NonLastArg_KeepsBfs()
    {
        // foo(bar(x), y): bar(x) is NOT the last arg → the BFS (temp +
        // get_structure) is kept, since inlining it would need to resume y.
        string text = Dis("p :- q(foo(bar(x), y)).");
        Assert.Contains("get_structure", text);   // bar(x) via deferred get_structure
    }
}
