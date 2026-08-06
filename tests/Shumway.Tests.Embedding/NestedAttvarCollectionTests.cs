using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Attributed variables that live in a compound's OWN argument cell.
///
/// <para>An unbound variable inside a list or structure is stored in the
/// argument cell itself, so once it gains an attribute the attvar's heap
/// address IS the compound's. Anything that walks a term collecting attributed
/// variables has to keep the two apart, or the compound's visited-mark swallows
/// the variable and the term looks unconstrained — <c>Qs ins 1..N</c> then
/// projects no domains at all, which is an answer that is silently wrong rather
/// than merely unhelpful.</para>
/// </summary>
public sealed class NestedAttvarCollectionTests
{
    // Attaches an attribute to each element IN PLACE — the element cell is the
    // variable, which is the shape under test. `X = [A], put_attr(A, ...)` is a
    // different shape: there the attvar is a separate cell the list points at.
    private const string Attach = """
        attach([]).
        attach([X|Xs]) :- put_attr(X, m, seen), attach(Xs).
        """;

    [Fact]
    public void TermAttvarsFindsVariablesInsideAList()
    {
        var e = new PrologEngine();
        e.ConsultString(Attach);
        var s = e.Query("length(L, 3), attach(L), term_attvars(L, Vs), length(Vs, N).");
        Assert.True(s.Success);
        Assert.Equal(3L, Assert.IsType<IntTerm>(s["N"]).Value);
    }

    [Fact]
    public void TermAttvarsFindsVariablesInsideAStructure()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            attach(T) :- T = f(A, B), put_attr(A, m, seen), put_attr(B, m, seen).
            """);
        var s = e.Query("attach(T), term_attvars(T, Vs), length(Vs, N).");
        Assert.True(s.Success);
        Assert.Equal(2L, Assert.IsType<IntTerm>(s["N"]).Value);
    }

    [Fact]
    public void CopyTerm3ProjectsDomainsOfVariablesInsideAList()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        // Passing the list gave R = [] while passing its elements one by one gave
        // the domains: the same variables, so the difference was the walk.
        var s = e.Query("length(Qs, 3), Qs ins 1..3, copy_term(Qs, _, R), length(R, N).");
        Assert.True(s.Success);
        Assert.Equal(3L, Assert.IsType<IntTerm>(s["N"]).Value);
    }
}
