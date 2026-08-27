using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// An ATTVAR cell is self-homed, and putting one into a structure must write a
/// REF to its home rather than a copy of the cell.
///
/// <para>The attribute table is keyed by the variable's own address, so a
/// copied cell is a second variable claiming attributes stored under the first
/// one's address: reading them through the copy found nothing and threw
/// KeyNotFoundException out of the engine, where a Prolog program could reach
/// it. <c>unify_variable</c> already captured a bare attvar as a REF to its
/// home ("a bare ATTVAR is captured as a REF to its home"); the write-mode half
/// of <c>unify_value</c> did not.</para>
///
/// <para>Only the last test here reproduces the crash (it is the CLP(R)
/// program that found it, cut down); the first three pin the invariant it
/// violated and pass either way. That is worth saying out loud: a test that
/// looks like it covers a bug and does not is worse than no test, so the
/// regression was checked by reverting the fix and watching exactly one of
/// these four fail.</para>
/// </summary>
public class AttVarAliasTests
{
    private static PrologEngine Attributed()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- public m:attr_unify_hook/2.
            m:attr_unify_hook(_, _).
            pair(X, f(X)).
            """);
        return e;
    }

    [Fact]
    public void AnAttributedVariableSurvivesBeingPutIntoABuiltStructure()
    {
        var e = Attributed();
        var sol = e.Query(
            "put_attr(V, m, hello), pair(V, T), arg(1, T, A), get_attr(A, m, W).");
        Assert.True(sol.Success);
        Assert.Equal("hello", ((Shumway.Compiler.Ast.AtomTerm)sol["W"]!).Name);
    }

    [Fact]
    public void TheCopyIsTheSameVariable()
    {
        // Not merely readable: it must BE the variable, so binding through the
        // structure binds the original.
        var e = Attributed();
        Assert.True(e.Query("put_attr(V, m, hello), pair(V, T), arg(1, T, A), A == V.").Success);
        var bound = e.Query("put_attr(V, m, hello), pair(V, T), arg(1, T, A), A = 1, V == 1.");
        Assert.True(bound.Success);
    }

    [Fact]
    public void AlsoThroughAPermanentVariable()
    {
        // The Y-register path is a separate opcode with the same hole. A goal
        // between the two uses forces V into a permanent slot.
        var e = new PrologEngine();
        e.ConsultString("""
            :- public m:attr_unify_hook/2.
            m:attr_unify_hook(_, _).
            keep(V, T) :- put_attr(V, m, hello), between(1, 1, _), T = f(V).
            """);
        var sol = e.Query("keep(V, T), arg(1, T, A), get_attr(A, m, W).");
        Assert.True(sol.Success);
        Assert.Equal("hello", ((Shumway.Compiler.Ast.AtomTerm)sol["W"]!).Name);
    }

    [Fact]
    public void ClprReachesItThroughInfAndSup()
    {
        // How it was found: CLP(R)'s optimiser builds terms over constrained
        // variables, and the second store's variables came back unreadable.
        var e = new PrologEngine();
        e.UseClpr();
        e.ConsultString("""
            s(G) :- ( catch(call(G), _, fail) -> true ; true ).
            two :- s(({X >= 3, X =< 9}, inf(X, I), format(atom(_), "~w", [I]))),
                   s(({A >= 1, B >= 1, A + B =:= 10}, inf(A, J),
                      format(atom(_), "~w", [J]))).
            """);
        Assert.True(e.Query("two.").Success);
    }
}
