using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 188: expand <see cref="IlPredicateCompiler.CanCompile"/> to
/// accept multi-clause TryMeElseChain predicates whose clause bodies
/// contain non-leaf Call sites. Pre-Phase-16 these were rejected
/// because the chunk-66 meta-CP design assumed a synchronous
/// RunSubroutine, which couldn't safely host non-leaf callees on a
/// multi-clause caller's recursive C# frame. Phase 16's threaded
/// dispatch (chunks 181-183) removed the recursive frame entirely;
/// the leaf restriction in TryDescribeTryMeElseChain.IsClauseBodyOpcode
/// became obsolete, and EmitTryMeElseChainBody now threads each
/// clause's Call sites with cursors in the post-clause-entry range
/// (cursors N..N+M-1 for N clauses and M total Call sites).
/// </summary>
public class Chunk188Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void MultiClause_TryMeElseChain_WithNonLeafCall_NowCompiles()
    {
        // Pure TryMeElse shape (no first-arg discrimination possible):
        // both clauses have variable first args. The body of clause 1
        // makes a non-tail Call to a multi-clause (non-leaf) callee.
        // Pre-chunk-188 this was rejected by IsClauseBodyOpcode's
        // leaf-callee restriction.
        var clauses = new ClauseReader(
            "callee(a).\n"
            + "callee(b).\n"
            + "caller(X) :- callee(X), nl.\n"
            + "caller(_) :- fail.\n").ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var callerPred = module.Predicates.Single(p => p.FunctorId == Fid("caller", 1));
        var calleeMap = module.Predicates.ToDictionary(p => p.FunctorId);
        Assert.True(new IlPredicateCompiler().CanCompile(callerPred, calleeMap));
    }

    [Fact]
    public void MultiClause_WithNonLeafCall_RunsCorrectly_UnderTier1()
    {
        // Functional check: the threaded multi-clause IL must give
        // the same answers as the bytecode interpreter.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            "callee(a).\n"
            + "callee(b).\n"
            + ":- public caller/1.\n"
            + "caller(X) :- callee(X), check(X).\n"
            + "caller(fallback).\n"
            + "check(a).\n"
            + "check(b).\n");

        var sols = engine.QueryAll("caller(X).")
            .Select(s => s.Bindings["X"].ToString())
            .ToList();
        // X=a (callee(a) succeeds, check(a) succeeds)
        // X=b (callee(b) succeeds, check(b) succeeds)
        // X=fallback (second clause)
        Assert.Equal(new[] { "a", "b", "fallback" }, sols);
    }

    [Fact]
    public void MultiClause_NonLeafCall_BacktrackThroughBoth()
    {
        // Backtracking must walk the callee's alternatives too.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            "pair(1, a).\n"
            + "pair(1, b).\n"
            + "pair(2, c).\n"
            + ":- public lookup/2.\n"
            + "lookup(K, V) :- pair(K, V).\n"
            + "lookup(_, none).\n");

        var sols = engine.QueryAll("lookup(1, V).")
            .Select(s => s.Bindings["V"].ToString())
            .ToList();
        Assert.Equal(new[] { "a", "b", "none" }, sols);
    }
}
