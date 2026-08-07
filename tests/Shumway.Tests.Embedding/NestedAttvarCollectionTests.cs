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

/// <summary>
/// Walking a term must not cost a C# frame per list element. The engine models
/// Prolog's own recursion on its heap, so a long list is ordinary — but the
/// converters and collectors that walk one are C# code, and one frame per
/// element is a stack overflow waiting for a big enough answer. A browser makes
/// it arrive early: its stack is small enough that a THOUSAND-element list
/// crashed the top level, because copy_term/3 runs on every answer.
/// </summary>
public sealed class LongTermWalkTests
{
    [Fact]
    public void CopyTermSurvivesAVeryLongList()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        var s = e.Query("numlist(1, 200000, L), copy_term(L, C, _), length(C, N).");
        Assert.True(s.Success);
        Assert.Equal(200000L, Assert.IsType<IntTerm>(s["N"]!).Value);
    }

    [Fact]
    public void TermAttvarsSurvivesAVeryLongList()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        Assert.True(e.Query("numlist(1, 200000, L), term_attvars(L, []).").Success);
    }

    [Fact]
    public void AttributesInALongListAreStillFoundInOrder()
    {
        // Iterating must not reorder what it collects: the answer displays the
        // constraints in the order they were found.
        var e = new PrologEngine { Out = new StringWriter() };
        e.UseClpfd();
        var s = e.Query("length(Qs, 3), Qs ins 1..3, copy_term(Qs, C, Gs), "
                      + "Gs = [G1|_], C = [C1|_], G1 = (V in _), V == C1.");
        Assert.True(s.Success);
    }
}
