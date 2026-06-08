using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Modes;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 365 (Phase 29, case 1): the <see cref="IlPredicateCompiler.IsInlinableLeafRule"/>
/// detector — a single-clause RULE whose body is deterministic builtins /
/// arithmetic / unification only, so it can be inlined FLAT into a caller's IL
/// method by the existing chunk-69 leaf-inline emit (no env frame, no choice
/// point, no cut, no user call). Generalises <see cref="IlPredicateCompiler.IsLeafPredicate"/>
/// (head-match-only) to a builtin body. The emit wiring is gated behind
/// <c>SHUMWAY_INLINE_RULES</c> while it is validated; these tests pin the
/// eligibility predicate, which is what decides correctness of the gate.
/// </summary>
public class Chunk365Tests
{
    private static CompiledPredicate CompileOne(string src)
    {
        var clauses = ClausePipeline.Apply(new ClauseReader(src).ReadAll(), new ModeTable())
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        return new PredicateCompiler { EmitDebugInfo = false }.Compile(clauses);
    }

    [Theory]
    [InlineData("inc(X, Y) :- Y is X + 1.")]                  // arithmetic body
    [InlineData("pos(N) :- N > 0.")]                          // comparison (inline a_int_cmp)
    [InlineData("eq(X, X).")]                                 // head-only (also a leaf)
    [InlineData("pair(X, Y, p(X, Y)).")]                      // head structure build
    [InlineData("classify(X, big) :- X > 100.")]             // head atom unify + compare
    [InlineData("scale(X, Y) :- Y is X * 2 + 1.")]           // multi-op arithmetic, no permanent
    public void DeterministicBuiltinBody_IsInlinableLeafRule(string src)
        => Assert.True(IlPredicateCompiler.IsInlinableLeafRule(CompileOne(src)));

    [Theory]
    [InlineData("p(X) :- q(X).")]                             // user call (non-leaf)
    [InlineData("p(X) :- q(X), r(X).")]                       // permanents + user calls
    [InlineData("acc(A, B, R) :- R0 is A + B, R is R0 * 2.")] // permanent (R0) → allocate
    [InlineData("m(0) :- !. m(_).")]                          // multi-clause + cut
    [InlineData("first(X) :- !, X = 1.")]                     // cut in body
    [InlineData("gen(X) :- between(1, 3, X).")]               // backtrackable builtin
    [InlineData("d(1). d(2). d(3).")]                          // multi-clause fact (not 1 clause)
    public void NotFlatInlinable_IsNotInlinableLeafRule(string src)
        => Assert.False(IlPredicateCompiler.IsInlinableLeafRule(CompileOne(src)));

    // NOTE: a rule whose body is a single builtin in TAIL position (e.g.
    // `is_int(X) :- integer(X).`) compiles to either a tail call (Execute, →
    // not inlinable) or `CallBuiltin builtin/N; proceed` (→ inlinable, and the
    // inline is sound — a deterministic builtin) depending on whether the builtin
    // is resolvable at compile time. The classification is therefore not stable
    // across global state, so it is not asserted here; both outcomes are correct.

    [Fact]
    public void Flag_DefaultsOff_UntilValidated()
        => Assert.False(IlPredicateCompiler.InlineLeafRules);
}
