using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Modes;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 367 (Phase 29, case 2 — emit): inlining a single-clause RULE that makes
/// user calls and/or cuts into a metaCp caller. The mid-body cut is the
/// soundness-critical part: the emit sets <c>B0 = engine.B</c> at the inline
/// entry, so the body's deep cut (allocate_get_level / get_level → cut) captures
/// THAT barrier and prunes only the inlined body's choice points — not the
/// caller's pre-existing ones. The body threads its non-tail calls through the
/// caller's forward-resume cursor space (the resume-label array is sized to
/// include them). Gated behind <c>SHUMWAY_INLINE_RULES2</c> while validated.
///
/// End-to-end cut soundness is validated by the full Embedding suite run with the
/// flag on, and by discriminating REPL cases (a cut commits the callee but a
/// caller choice point created before the call survives). These tests pin the
/// <see cref="IlPredicateCompiler.IsInlinableRule"/> eligibility with allowCut.
/// </summary>
public class Chunk367Tests
{
    private static CompiledPredicate CompileOne(string src)
    {
        var clauses = ClausePipeline.Apply(new ClauseReader(src).ReadAll(), new ModeTable())
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        return new PredicateCompiler { EmitDebugInfo = false }.Compile(clauses);
    }

    [Theory]
    [InlineData("commit(X) :- a(X), !.")]              // call + trailing deep cut
    [InlineData("c2(X) :- a(X), b(X), !.")]            // two calls then cut
    [InlineData("g(X) :- X > 0, !.")]                  // guard + cut (no-op cut)
    [InlineData("mid(X, Z) :- a(X), !, Z is X + 1.")] // mid-body cut + arith after
    [InlineData("midtail(X) :- a(X), !, b(X).")]       // mid-body cut + trailing tail call (ch368)
    public void CutRule_IsInlinable_WhenAllowCut(string src)
    {
        Assert.True(IlPredicateCompiler.IsInlinableRule(CompileOne(src), allowCut: true));
        // Without allowCut the same cut-bearing rule is rejected (cut-free use).
        Assert.False(IlPredicateCompiler.IsInlinableRule(CompileOne(src), allowCut: false));
    }

    [Theory]
    [InlineData("p(0) :- !. p(X) :- a(X).")]           // multi-clause (not 1 clause)
    public void NotEligible_EvenWithAllowCut(string src)
        => Assert.False(IlPredicateCompiler.IsInlinableRule(CompileOne(src), allowCut: true));

    // NOTE: a backtrackable builtin (between/3, …) in a rule body is rejected via
    // the CallBuiltin case — but only once it is RESOLVED to a CallBuiltin opcode.
    // In an isolated PredicateCompiler compile it may still be an unresolved
    // generic Call (resolved to the builtin only at link/runtime), so its
    // classification is not asserted here. The rule inliner runs on the runtime
    // promotion path, where the builtin is a CallBuiltin and is rejected.

    [Fact]
    public void Case2Flag_DefaultsOff_UntilValidated()
        => Assert.False(IlPredicateCompiler.InlineRules2);
}
