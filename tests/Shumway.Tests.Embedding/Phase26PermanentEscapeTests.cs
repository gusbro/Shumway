using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 26 D — Shumway heap-allocates permanent (Y-slot) variables
/// (<c>put_variable_y</c> → <c>AllocateHeapUnbound</c>; a Y-slot only ever holds
/// a Ref to a heap cell or an immediate, never a stack-resident unbound). So a
/// permanent variable written into a heap structure via <c>unify_value_y</c> is
/// always a heap reference: the structure stays valid after the environment is
/// deallocated, with NO <c>unify_local_value</c>-style globalisation needed
/// (unlike classical WAM, where <c>put_variable Yn</c> is stack-resident). These
/// tests pin that invariant — they would corrupt if a permanent ever became
/// stack-local.
/// </summary>
public class Phase26PermanentEscapeTests
{
    private static PrologEngine Load(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void PermanentVar_InEscapingStructure_SurvivesFrameDeallocation()
    {
        // X is permanent (spans the gen/1 call and the Out = box(X) goal). The
        // structure box(X) is bound to the head argument Out and so OUTLIVES
        // escaper's environment. Binding it afterwards must reach X's heap cell.
        var engine = Load("""
            escaper(Out) :- gen(X), Out = box(X), keep.
            gen(_).
            keep.
            """);
        var sol = engine.Query("escaper(O), O = box(V), V = filled.");
        Assert.True(sol.Success);
        Assert.Equal("box(filled)", sol["O"]!.ToString());
    }

    [Fact]
    public void PermanentVar_InEscapingList_SurvivesFrameDeallocation()
    {
        // Mirrors Blint's `concat(['x', Name], _)` shape: a permanent (Name)
        // built into a heap list that escapes via the head argument.
        var engine = Load("""
            wrap(Name, List) :- pre(Name), List = [a, Name, c], post.
            pre(_).
            post.
            """);
        var sol = engine.Query("wrap(N, L), N = b, L = [a, b, c].");
        Assert.True(sol.Success);
    }
}
