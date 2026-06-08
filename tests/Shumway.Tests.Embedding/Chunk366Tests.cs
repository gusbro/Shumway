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
    [InlineData("chain(X, Y) :- a(X, M), b(M, Y).")]  // ends in a tail user-call (chunk 368)
    [InlineData("guarded(X) :- X > 0, q(X).")]        // guard then tail user-call
    public void SingleClauseCutFreeRule_IsInlinableRule(string src)
        => Assert.True(IlPredicateCompiler.IsInlinableRule(CompileOne(src)));

    [Theory]
    [InlineData("p(X) :- q(X), !.")]                  // trailing cut
    [InlineData("p(X) :- q(X), !, r(X).")]            // mid-body cut
    [InlineData("p(X) :- !, q(X).")]                  // neck cut
    [InlineData("p(0). p(X) :- q(X).")]               // multi-clause
    public void CutOrMultiClause_IsNotInlinableRule(string src)
        => Assert.False(IlPredicateCompiler.IsInlinableRule(CompileOne(src)));

    // NOTE on builtins: a tail-position builtin (between/3, atom/1, …) is rejected
    // because the LINKER rewrites it Execute -> ExecuteBuiltin (chunk 248), which
    // the detector rejects. An ISOLATED PredicateCompiler compile (no link) leaves
    // it a generic Execute, so its classification is not asserted here. The rule
    // inliner runs on the linked runtime bytecode, where the distinction holds.
}
