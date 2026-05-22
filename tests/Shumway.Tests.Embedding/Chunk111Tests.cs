using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 111 (Phase 8): iterative list materialisation.
///
/// <para>Diagnosis of the Phase-8 deep-recursion overflow: the engine
/// <em>does</em> have last-call optimisation, but converting between WAM
/// heap cells and the <see cref="Shumway.Compiler.Ast.Term"/> AST —
/// <c>Materializer.MaterializeAsCell</c> and <c>TermReader.Materialize</c>
/// — recursed once per list element. A long list (a tabled predicate's
/// thousands of <c>clause/2</c>-visible facts, or a tail recursion
/// accumulating a list) therefore overflowed the C# stack. Both
/// materialisers now walk the list spine iteratively.</para>
/// </summary>
public class Chunk111Tests
{
    [Fact]
    public void DeepTabledChain_DoesNotOverflow()
    {
        // ~2500 fixpoint rounds; each builds on a '$tbl_ans' dynamic
        // predicate that grows to thousands of facts, read with clause/2.
        var sb = new StringBuilder(":- table path/2.\n");
        const int n = 2500;
        for (int i = 1; i < n; i++) sb.Append($"edge({i}, {i + 1}).\n");
        sb.Append("path(X, Y) :- edge(X, Y).\n");
        sb.Append("path(X, Y) :- path(X, Z), edge(Z, Y).\n");
        var engine = new PrologEngine();
        engine.ConsultString(sb.ToString());
        Assert.Equal(n - 1, engine.QueryAll("path(1, X).").Count());
    }

    [Fact]
    public void TailRecursionBuildingALongList_DoesNotOverflow()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "build(0, Acc, Acc).\n" +
            "build(N, Acc, Out) :- N > 0, N1 is N - 1, build(N1, [N|Acc], Out).");
        // The result is a 50 000-element list — materialising it as a
        // query binding must not recurse per element.
        var sols = engine.QueryAll("build(50000, [], L).").ToList();
        Assert.Single(sols);
    }

    [Fact]
    public void ClauseEnumerationOverManyFacts()
    {
        // clause/2 over a dynamic predicate with thousands of facts
        // materialises the matching-clause list — once per element.
        var sb = new StringBuilder(":- dynamic fact/1.\n");
        for (int i = 0; i < 20000; i++) sb.Append($"fact({i}).\n");
        var engine = new PrologEngine();
        engine.ConsultString(sb.ToString());
        Assert.Equal(20000, engine.QueryAll("clause(fact(X), true).").Count());
    }
}
