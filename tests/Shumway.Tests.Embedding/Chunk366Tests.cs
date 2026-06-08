using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Modes;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 366 (Phase 29, case 2 — detector + sizing): the
/// <see cref="IlPredicateCompiler.IsInlinableRule"/> detector — a single-clause
/// RULE inlinable into a caller's IL method, generalising the case-1 leaf-rule
/// detector to a body that also makes USER calls and uses an environment frame
/// (permanents). It is cut-free for now: pruning the caller's choice points
/// across an inline boundary needs scoped-barrier handling (deferred), and the
/// Blint sizing showed that is THE prerequisite — only 10/108 single-clause-rule
/// call sites are cut-free, vs 89/108 once a (mid-body) cut is handled. So this
/// chunk is the detector + the finding; the cut-scoping emit is the real case-2
/// work.
/// </summary>
public class Chunk366Tests
{
    private static CompiledPredicate CompileOne(string src)
    {
        var clauses = ClausePipeline.Apply(new ClauseReader(src).ReadAll(), new ModeTable())
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        return new PredicateCompiler { EmitDebugInfo = false }.Compile(clauses);
    }

    [Theory]
    [InlineData("p(X, Z) :- q(X, Y), Z is Y + 1.")]   // body user call (non-tail) + arith
    [InlineData("inc(X, Y) :- Y is X + 1.")]          // leaf rule (subset)
    [InlineData("twocall(X, Z) :- q(X, Y), r(Y, W), Z is W + 1.")] // two non-tail calls + arith
    public void SingleClauseCutFreeRule_IsInlinableRule(string src)
        => Assert.True(IlPredicateCompiler.IsInlinableRule(CompileOne(src)));

    [Theory]
    [InlineData("p(X) :- q(X), !.")]                  // trailing cut
    [InlineData("p(X) :- q(X), !, r(X).")]            // mid-body cut
    [InlineData("p(X) :- !, q(X).")]                  // neck cut
    [InlineData("p(0). p(X) :- q(X).")]               // multi-clause
    [InlineData("p(X) :- between(1, 3, X).")]         // backtrackable builtin (tail Execute)
    // A rule whose LAST goal is a user call lowers to a tail Execute; un-tailing it
    // at a non-tail inline site is deferred to the emit, so the cut-free detector
    // rejects a trailing Execute for now.
    [InlineData("chain(X, Y) :- a(X, M), b(M, Y).")]
    [InlineData("guarded(X) :- X > 0, q(X).")]
    public void CutTailCallOrMultiClause_IsNotInlinableRule(string src)
        => Assert.False(IlPredicateCompiler.IsInlinableRule(CompileOne(src)));
}
