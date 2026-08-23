using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// An attributed variable IS an unbound variable: every builtin that
/// dispatches direction on "is this argument unbound?" must treat
/// <c>Tag.AttVar</c> exactly like <c>Tag.Ref</c>, and bind it through the
/// unify path (which queues the hook wakeups). The Trealla campaign found
/// between/3 failing silently and atom_chars/atom_codes/number_chars/name
/// throwing type_error at a frozen output argument — each saw the ATTVAR
/// tag as "bound to something that is not an atom/number".
/// </summary>
public class AttvarBindingBuiltinsTests
{
    [Theory]
    [InlineData("atom_chars(V, [a,b]), V == ab")]
    [InlineData("atom_codes(V, [0'a]), V == a")]
    [InlineData("number_chars(V, ['4']), V == 4")]
    [InlineData("name(V, [0'a]), V == a")]
    [InlineData("findall(V, between(1, 3, V), L), L == [1, 2, 3]")]
    public void FrozenOutputArgumentsBindLikePlainVariables(string goal)
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(coroutining)).");
        Assert.True(e.Query($"freeze(V, true), {goal}.").Success);
    }

    [Fact]
    public void TheWakeupStillFiresOnTheBuiltinBinding()
    {
        // Binding through the builtin must run the frozen goal — a filter
        // that rejects the value makes the builtin call fail.
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(coroutining)).");
        Assert.False(e.Query("freeze(V, fail), atom_chars(V, [a]).").Success);
        Assert.True(e.Query(
            "freeze(V, V == a), atom_chars(V, [a]), V == a.").Success);
    }

    [Fact]
    public void ResidueVarsSeeAVariableReconstrainedAfterBacktracking()
    {
        // The attribute table keeps ORPHAN rows for homes whose promotion was
        // backtracked; the call_residue_vars entry snapshot must skip them, or
        // a second findall iteration over goals sharing subterms reports [].
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(coroutining)).");
        Assert.True(e.Query("""
            EDif = dif(X, Y), SB = (Y = [] * []), SA = (X = [] * _C),
            G1 = (EDif, SB, SA), G2 = (SB, SA, EDif),
            findall(R, (member(G, [G1, G2]),
                        call_residue_vars(G, Vs), length(Vs, R)), Rs),
            Rs == [1, 1].
            """).Success);
    }
}
