using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>`V = T` compiles inline as head-style get / unify instead of a
/// call to the =/2 builtin (mirrors GProlog's `X = [A|B]` → get_list +
/// unify): a temporary in its register, a permanent through a scratch
/// register. Every goal ends a chunk, so a variable of two goals is
/// permanent.</summary>
public class InlineUnifyTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).Single().Text;

    private static int Count(string text, string op)
    {
        int n = 0;
        for (int i = text.IndexOf(op, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(op, i + op.Length, System.StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>The call and execute instructions of a listing; the =/2
    /// builtin is one of them when it is called.</summary>
    private static int Calls(string text) => Count(text, "call ") + Count(text, "execute ");

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
    public void FirstOccurrencePermanent_BuildsTheTermThenTakesIt()
    {
        // X spans two goals → permanent; the list is built into a scratch
        // register and get_variable_y makes it X's first cell.
        string text = Dis("u(A, B) :- X = [A|B], s(X).");
        Assert.Contains("put_list", text);
        Assert.Contains("get_variable_y", text);
        Assert.Equal(1, Calls(text));
    }

    [Fact]
    public void SeenPermanent_MatchesTheTermAsAHeadArgument()
    {
        // X has its cell from s(X): a copy to a scratch register, then
        // get_structure / unify against it.
        string text = Dis("v(A) :- s(X), X = f(A).");
        Assert.Contains("put_value_y", text);
        Assert.Contains("get_structure", text);
        Assert.Contains("unify_value_y", text);
        Assert.Equal(1, Calls(text));
    }

    [Fact]
    public void TwoSeenPermanents_UnifyThroughAScratchRegister()
    {
        string text = Dis("w :- s(X), s(Y), X = Y.");
        Assert.Contains("get_value_y", text);
        Assert.Equal(2, Calls(text));
    }

    [Fact]
    public void TwoFirstOccurrencePermanents_ShareOneFreshVariable()
    {
        string text = Dis("w2 :- X = Y, s(X), s(Y).");
        Assert.Contains("get_variable_y", text);
        Assert.Equal(2, Calls(text));
        // One cell for the two: the other put_variable_y is s(Y)'s... none;
        // X and Y both have their cell before the calls.
        Assert.Equal(1, Count(text, "put_variable_y"));
    }

    [Fact]
    public void Permanent_AgainstASeenTemporary_UsesItsRegister()
    {
        // B is a head temporary, X a permanent without a cell yet: Y := A0;
        // the one put_value_y is s(X)'s argument.
        string text = Dis("z(B) :- X = B, s(X).");
        Assert.Contains("get_variable_y", text);
        Assert.Equal(1, Count(text, "put_value_y"));
        Assert.Equal(1, Calls(text));
        // X permanent with its cell from the head: unify(Y, A1).
        text = Dis("z2(X, B) :- X = B, s(X).");
        Assert.Contains("get_value_y", text);
        Assert.Equal(1, Calls(text));
    }

    [Fact]
    public void Permanent_AgainstAFirstOccurrenceTemporary_IsCopiedToIt()
    {
        string text = Dis("z3 :- s(X), X = _B.");
        Assert.Contains("put_value_y", text);
        Assert.DoesNotContain("get_value_y", text);
        Assert.Equal(1, Calls(text));
    }

    [Fact]
    public void FirstOccurrencePermanent_InsideItsOwnTerm_IsMatchedNotBuilt()
    {
        // X = f(X): built, the inner X would read the slot before it is
        // written; so X gets a fresh cell and f(X) is matched against it.
        string text = Dis("z4 :- X = f(X), s(X).");
        Assert.Contains("put_variable_y", text);
        Assert.Contains("get_structure", text);
        Assert.Contains("unify_value_y", text);
        Assert.Equal(1, Calls(text));
    }

    [Fact]
    public void Anonymous_IsNothingToMatch_OrTheBuiltinWhenTheVariableHasNoCell()
    {
        string text = Dis("z5 :- s(X), X = _.");
        Assert.Equal(1, Calls(text));
        Assert.DoesNotContain("get_value_y", text);
        text = Dis("z6 :- X = _, s(X).");
        Assert.Equal(2, Calls(text));
    }

    [Fact]
    public void BothSidesNonVariables_StayACall()
    {
        string text = Dis("z7 :- f(a) = f(_).");
        Assert.Equal(2, Count(text, "put_structure"));
        Assert.Equal(1, Calls(text));
    }
}
