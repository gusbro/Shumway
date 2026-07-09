using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// ADR-031 — the fold recogniser (<see cref="ClauseFold"/>). Pins which clause
/// groups match the <c>Guard,!,Body / Rest</c> shape and how their heads classify.
/// The fold TRANSFORM itself is not implemented: disassembly showed routing the
/// fold through the existing ITE-helper path is a structural no-op (the helper is
/// byte-identical <c>try_me_else+cut</c>), so the recogniser exists for the
/// <c>--foldcensus</c> sizing pass and any future CP-free-codegen prototype.
/// </summary>
public class ClauseFoldTests
{
    private static ClauseFold.FoldKind Classify(string src) =>
        ClauseFold.Classify(new ClauseReader(src).ReadAll().ToList());

    [Fact]
    public void TrivialVarHeads_SameVarPattern()
    {
        Assert.Equal(ClauseFold.FoldKind.TrivialVarHeads,
            Classify("p(X):-guard(X),!,body(X). p(X):-rest(X)."));
    }

    [Fact]
    public void ThreadedVarHeads_RepeatedVar()
    {
        // max(X,Y,X) repeats X in arg3 → the pattern differs from max(X,Y,Y).
        Assert.Equal(ClauseFold.FoldKind.ThreadedVarHeads,
            Classify("max(X,Y,X):-X>=Y,!. max(X,Y,Y)."));
    }

    [Fact]
    public void SingleClause_NotAFoldCandidate()
    {
        Assert.Equal(ClauseFold.FoldKind.None, Classify("p(X):-a(X),!,b(X)."));
    }

    [Fact]
    public void StructuredHead_NotFoldable_IndexingSeparates()
    {
        // A non-var (list) first arg → first-argument indexing already separates
        // the clauses; the cut is not doing cross-clause selection.
        Assert.Equal(ClauseFold.FoldKind.None,
            Classify("p([H|T]):-guard(H),!,body(T). p([]):-rest."));
    }

    [Fact]
    public void NoLeadingCut_NotFoldable()
    {
        Assert.Equal(ClauseFold.FoldKind.None, Classify("p(X):-a(X). p(X):-b(X)."));
    }

    [Fact]
    public void LaterClauseCut_NotFoldable()
    {
        // A second top-level cut is a different multi-commit shape.
        Assert.Equal(ClauseFold.FoldKind.None,
            Classify("p(X):-a(X),!,b(X). p(X):-c(X),!. p(X):-d(X)."));
    }

    [Fact]
    public void TrailingCutFirstClause_StillCandidate()
    {
        // `Guard, !` (empty body) as a non-last clause still commits selection →
        // folds to (Guard -> true ; Rest).
        Assert.Equal(ClauseFold.FoldKind.TrivialVarHeads,
            Classify("p(X):-guard(X),!. p(X):-rest(X)."));
    }
}
