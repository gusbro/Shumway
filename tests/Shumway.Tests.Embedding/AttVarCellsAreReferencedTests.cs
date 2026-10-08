using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An attributed variable is its ATTVAR cell only in its home slot;
/// anywhere else it must be a REF to that slot. A builtin that resolved an
/// element and stored or bound the cell itself made a second variable the
/// attribute table did not know: sort/2 handed back copies of the domain
/// variables length/2 had created, and a binding of the copy skipped the
/// domain check. Each case puts the variable's home inside a term (a list
/// cell or an argument slot), the shape that exposes a copy.</summary>
public sealed class AttVarCellsAreReferencedTests
{
    public static TheoryData<string> Builtins() => new()
    {
        "T = f(X), put_attr(X, m, 1), arg(1, T, A)",
        "T = f(X), put_attr(X, m, 1), T =.. [_, A]",
        "L = [X], put_attr(X, m, 1), nth0(0, L, A)",
        "L = [X], put_attr(X, m, 1), nth1(1, L, A)",
        "L = [a, X], put_attr(X, m, 1), nth0(1, L, A, _)",
        "L = [X], put_attr(X, m, 1), last(L, A)",
        "L = [X], put_attr(X, m, 1), member(A, L)",
        "L = [X], put_attr(X, m, 1), memberchk(A, L)",
        "L = [X], put_attr(X, m, 1), append(L, [], [A])",
        "L = [X], put_attr(X, m, 1), append(_, [A], L)",
        "L = [X], put_attr(X, m, 1), reverse(L, [A])",
        "L = [X], put_attr(X, m, 1), msort(L, [A])",
        "L = [X], put_attr(X, m, 1), sort(L, [A])",
        "L = [X], put_attr(X, m, 1), sort(0, @>=, L, [A])",
        "L = [X], put_attr(X, m, 1), predsort(compare, L, [A])",
        "L = [k-X], put_attr(X, m, 1), keysort(L, [_-A])",
        "L = [X], put_attr(X, m, 1), select(A, L, _)",
        "L = [X], put_attr(X, m, 1), exclude(==(z), L, [A])",
        "L = [X], put_attr(X, m, 1), list_to_set(L, [A])",
        "T = f(X), put_attr(X, m, 1), term_variables(T, [A])",
        "L = [a|X], put_attr(X, m, 1), '$skip_list'(_, L, A)",
        "L = [k-X], put_attr(X, m, 1), pairs_values(L, [A])",
        "put_attr(X, m, 1), b_setval(k, X), b_getval(k, A)",
        "L = [X], put_attr(X, m, 1), maplist(=(A), L)",
        "L = [X], put_attr(X, m, 1), max_member(A, L)",
    };

    [Theory]
    [MemberData(nameof(Builtins))]
    public void TheBuiltinHandsBackTheVariable(string goal)
    {
        var e = new PrologEngine();
        Assert.True(e.Query(goal + ".").Success, "the goal itself failed: nothing here is measured");
        Assert.True(e.Query(goal + ", A == X.").Success, "the builtin handed back a copy, not the variable");
        Assert.True(e.Query(goal + ", get_attr(A, m, 1).").Success, "the variable handed back lost its attribute");
    }

    [Fact]
    public void ASortedDomainVariableKeepsItsDomain()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        Assert.True(e.Query("length(L, 2), L ins 0..1, msort(L, [A, _]), A = 1.").Success,
            "a binding inside the domain failed: nothing here is measured");
        Assert.False(e.Query("length(L, 2), L ins 0..1, msort(L, [A, _]), A = 5.").Success,
            "a binding outside the domain succeeded");
    }
}
