using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// The <c>portray/1</c> hook: <c>print/1,2</c>, format's <c>~p</c> and
/// write_term's <c>portrayed(true)</c> give the user's portray/1 first shot at
/// every subterm. It runs RE-ENTRANTLY on the live activation, which is what
/// made the nested-solve environment restore below matter.
/// </summary>
public class PortrayHookConformance
{
    private const string Hook =
        "portray(A) :- atom(A), write(A), write(A). "
        + "portray(F) :- float(F), I is truncate(F), write(I). ";

    private static string Captured(string goal)
    {
        var engine = new PrologEngine();
        engine.ConsultString(Hook);
        var sol = engine.Query($"with_output_to(atom(Out), ({goal})).");
        Assert.True(sol.Success, $"goal failed: {goal}");
        return ((Shumway.Compiler.Ast.AtomTerm)sol["Out"]!).Name;
    }

    [Fact]
    public void PrintConsultsThePortrayHookForEverySubterm()
    {
        Assert.Equal("foofoo", Captured("print(foo)"));
        Assert.Equal("a(foofoo)", Captured("print(a(foo))"));
        Assert.Equal("a(foofoo,b(c(foofoo,3)))",
            Captured("print(a(foo, b(c(foo, 3.14))))"));
    }

    [Fact]
    public void PrintFallsBackToWriteWhenTheHookFails()
    {
        // portray/1 has no clause for an integer: render it normally.
        Assert.Equal("42", Captured("print(42)"));
        Assert.Equal("f(42,foofoo)", Captured("print(f(42, foo))"));
    }

    [Fact]
    public void WriteTermPortrayedAndFormatTildeP()
    {
        Assert.Equal("a(foofoo)", Captured("write_term(a(foo), [portrayed(true)])"));
        Assert.Equal("a(foofoo)", Captured("write_term(a(foo), [portray(true)])"));
        Assert.Equal("a(foofoo)", Captured("format('~p', [a(foo)])"));
        // portrayed(false) is the plain rendering.
        Assert.Equal("a(foo)", Captured("write_term(a(foo), [portrayed(false)])"));
        // write/1 never consults the hook.
        Assert.Equal("a(foo)", Captured("write(a(foo))"));
    }

    [Fact]
    public void PortrayIsMultifileAndDynamicWithoutBeingDeclared()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("predicate_property(portray(_), (multifile)).").Success);
        Assert.True(engine.Query("predicate_property(portray(_), (dynamic)).").Success);
        // …and with no clauses, printing is just writing.
        Assert.True(engine.Query(
            "with_output_to(atom(A), print(a(foo))), A == 'a(foo)'.").Success);
    }

    [Fact]
    public void FailingNestedSolveKeepsTheCallersContinuation()
    {
        // The regression this arc turned on: a re-entrant solve that FAILS left
        // the last clause tried as the current environment, so the caller
        // resumed against a foreign frame and its continuation vanished —
        // visible only under an enclosing catch/3.
        var engine = new PrologEngine();
        engine.ConsultString(Hook);
        Assert.True(engine.Query(
            "catch(print(42), _, true), X = after, X == after.").Success);
        Assert.True(engine.Query(
            "catch((print(42), Y = inner), _, true), Y == inner, Z = out, Z == out.").Success);
        Assert.True(engine.Query(
            "with_output_to(atom(A), format('~p', [a(foo)])), A == 'a(foofoo)'.").Success);
    }
}
