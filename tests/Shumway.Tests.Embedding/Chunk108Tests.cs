using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 108 (Phase 7): non-ground tabled answers. A tabled answer may
/// contain unbound variables; the driver deduplicates answers up to
/// variable renaming (variant tabling), so a non-ground answer derived
/// more than once — or once per fixpoint round — is stored as one
/// answer rather than looping forever on fresh variable names.
/// </summary>
public class Chunk108Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void NonGroundAnswer_IsReturned()
    {
        // gen/1's answer pair(X, X) is non-ground; the call yields it once,
        // and its two arguments are still the same variable.
        var engine = WithProgram("""
            :- table gen/1.
            gen(pair(X, X)).
            """);
        Assert.Single(engine.QueryAll("gen(R)."));
        Assert.True(engine.Query("gen(pair(A, A)).").Success);
        Assert.False(engine.Query("gen(pair(a, b)).").Success);
    }

    [Fact]
    public void VariantNonGroundAnswers_DeduplicatedToOne()
    {
        // Both clauses derive a variant of t(f(_)); variant tabling keeps
        // exactly one answer, not two.
        var engine = WithProgram("""
            :- table t/1.
            lhs(f(Y)).
            rhs(f(Z)).
            t(X) :- lhs(X).
            t(X) :- rhs(X).
            """);
        Assert.Single(engine.QueryAll("t(R)."));
    }

    [Fact]
    public void NonGroundAnswer_ThroughRecursion_Terminates()
    {
        // The recursive clause re-derives loop(item(_)) with a fresh
        // variable every round; without variant deduplication the fixpoint
        // would never settle. It must terminate with one answer.
        var engine = WithProgram("""
            :- table loop/1.
            seed(item(_)).
            loop(X) :- seed(X).
            loop(X) :- loop(X).
            """);
        Assert.Single(engine.QueryAll("loop(R)."));
    }

    [Fact]
    public void GroundAnswers_StillDeduplicated()
    {
        // The renaming-invariant encoding must not change ground behaviour.
        var engine = WithProgram("""
            :- table path/2.
            edge(a, b).  edge(a, b).  edge(b, c).
            path(X, Y) :- edge(X, Y).
            path(X, Y) :- path(X, Z), edge(Z, Y).
            """);
        Assert.Equal(2, engine.QueryAll("path(a, X).").Count());   // b, c
    }
}
