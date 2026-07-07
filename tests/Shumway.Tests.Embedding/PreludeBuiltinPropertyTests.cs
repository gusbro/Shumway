using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): <c>predicate_property/2</c> reports the
/// prelude's library predicates (ISO / de-facto standards written in Prolog
/// rather than C#) as <c>built_in</c>. A client like Logtalk's linter checks
/// this to tell a call to a known system predicate from an undefined one; the
/// prelude's <c>sub_atom/5</c>, <c>subsumes_term/2</c>, etc. were previously
/// reported <c>static</c> and flagged as unknown.
/// </summary>
public class PreludeBuiltinPropertyTests
{
    private static bool Holds(string goal) => new PrologEngine().Query(goal).Success;

    [Theory]
    [InlineData("member(_, _)")]
    [InlineData("sub_atom(_, _, _, _, _)")]
    [InlineData("subsumes_term(_, _)")]
    [InlineData("maplist(_, _)")]
    [InlineData("length(_, _)")]
    [InlineData("select(_, _, _)")]
    public void PreludePredicate_IsBuiltIn(string call) =>
        Assert.True(Holds($"predicate_property({call}, built_in)."));

    [Fact]
    public void CSharpBuiltin_StillBuiltIn() =>
        Assert.True(Holds("predicate_property(atom_length(_, _), built_in)."));

    [Fact]
    public void UndefinedPredicate_HasNoProperty() =>
        Assert.False(Holds("predicate_property(no_such_pred_xyz(_), built_in)."));

    [Fact]
    public void UserPredicate_IsNotBuiltIn()
    {
        var e = new PrologEngine();
        e.ConsultString("my_user_pred(1). my_user_pred(2).");
        Assert.False(e.Query("predicate_property(my_user_pred(_), built_in).").Success);
        Assert.True(e.Query("predicate_property(my_user_pred(_), static).").Success);
    }
}
