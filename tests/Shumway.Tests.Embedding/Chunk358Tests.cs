using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Modes;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 358 (Phase 28): first piece of the Tier-1 IL local-predicate inliner
/// (Phase 1, docs/design/il-local-inlining.md) — the eligibility predicate
/// <see cref="IlPredicateCompiler.IsFactPredicate"/>. It recognises a pure FACT
/// predicate (every clause is head matching only; the rest of the bytecode is
/// the clause-dispatch skeleton + proceed), which is what may have its clause
/// dispatch inlined into a caller. No emit yet — just the detector.
/// </summary>
public class Chunk358Tests
{
    private static CompiledPredicate CompileOne(string src)
    {
        var clauses = ClausePipeline.Apply(new ClauseReader(src).ReadAll(), new ModeTable())
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        return new PredicateCompiler { EmitDebugInfo = false }.Compile(clauses);
    }

    [Theory]
    [InlineData("odd(1). odd(3). odd(5). odd(7). odd(9).")]   // crypt's generator shape
    [InlineData("point(p).")]                                 // single-clause fact
    [InlineData("pt(a, b). pt(c, d).")]                       // multi-clause, two args
    [InlineData("lst([a, b]). lst([]).")]                     // list head match
    [InlineData("shape(circle(R)). shape(square(S)).")]       // compound head match
    public void FactPredicates_AreFacts(string src)
        => Assert.True(IlPredicateCompiler.IsFactPredicate(CompileOne(src)));

    [Theory]
    [InlineData("inc(X, Y) :- Y is X + 1.")]                  // arithmetic body
    [InlineData("pos(N) :- N > 0.")]                          // comparison body
    [InlineData("acc(A, B, R) :- R0 is A + B, R is R0 * 2.")] // permanents (env) + arith
    public void NonFactPredicates_AreNotFacts(string src)
        => Assert.False(IlPredicateCompiler.IsFactPredicate(CompileOne(src)));
}
