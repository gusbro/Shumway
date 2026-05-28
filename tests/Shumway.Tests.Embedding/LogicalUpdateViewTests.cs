using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ISO logical-update-view regression guards (Phase 20). A call to a
/// dynamic predicate sees the clause set as of the moment it was
/// entered: clauses retracted while it enumerates are still visited, and
/// clauses asserted while it enumerates are not. These pin that
/// semantics so the dead-clause reclamation optimization (which
/// physically drops retracted clauses from the dispatch chain once no
/// in-progress enumeration can reach them) cannot regress it.
/// </summary>
public class LogicalUpdateViewTests
{
    // Render a proper-list Term as "e1,e2,e3" using each element's
    // canonical ToString — Solution.Bindings exposes the raw Term AST,
    // whose ToString is functional ('.'(1, ...)), so we walk it.
    private static string ListString(Term t)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        while (t is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            if (!first) sb.Append(',');
            sb.Append(c.Args[0].ToString());
            first = false;
            t = c.Args[1];
        }
        return sb.ToString();
    }

    [Fact]
    public void RetractDuringEnumeration_StillSeesSnapshot()
    {
        // ( p(X), retract(p(X)), fail ; true ) must enumerate all three:
        // p(X) captured the 3-clause view when it started.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic p/1.\n"
            + "p(1). p(2). p(3).\n"
            + ":- public collect/1.\n"
            + "collect(L) :- findall(X, (p(X), retract(p(X))), L).\n");
        var sol = engine.Query("collect(L).");
        Assert.True(sol.Success);
        Assert.Equal("1,2,3", ListString(sol["L"]!));
        // All retracted now.
        Assert.False(engine.Query("p(_).").Success);
    }

    [Fact]
    public void AssertDuringEnumeration_DoesNotSeeNewClause()
    {
        // ( q(X), assertz(q(2)), fail ; true ) sees only the original
        // q(1) — the q(X) enumeration ignores clauses asserted after it
        // started (otherwise it would loop / see 2).
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic q/1.\n"
            + "q(1).\n"
            + ":- public collect/1.\n"
            + "collect(L) :- findall(X, (q(X), assertz(q(2))), L).\n");
        var sol = engine.Query("collect(L).");
        Assert.True(sol.Success);
        Assert.Equal("1", ListString(sol["L"]!));
        // q(2) was asserted (once) even though the enumeration didn't see it.
        Assert.True(engine.Query("q(2).").Success);
    }

    [Fact]
    public void AssertRetractLoop_StaysCorrectAndBounded()
    {
        // The next_char_i shape: assert + retract a single-clause dynamic
        // predicate in a deterministic loop. Each step must see exactly
        // the current value. Reclamation may drop the dead clauses (no
        // enumeration is in progress), but the observable result must be
        // unchanged.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic cur/1.\n"
            + ":- public run/2.\n"
            + "run(N, Last) :- assertz(cur(0)), step(N), cur(Last).\n"
            + "step(0) :- !.\n"
            + "step(N) :- N > 0, cur(C), C1 is C + 1,\n"
            + "    retract(cur(_)), assertz(cur(C1)), N1 is N - 1, step(N1).\n");
        var sol = engine.Query("run(5000, Last).");
        Assert.True(sol.Success);
        Assert.Equal("5000", sol.Bindings["Last"].ToString());
        // Exactly one live clause remains.
        var all = engine.QueryAll("cur(X).").ToList();
        Assert.Single(all);
    }

    [Fact]
    public void OuterChoicePoint_DoesNotResurrectRetractedClause()
    {
        // A choice point in an UNRELATED predicate that backtracks and
        // re-calls the dynamic predicate must see the CURRENT clause set
        // (a fresh call samples the current generation), not a stale one.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic d/1.\n"
            + "choice(a). choice(b).\n"
            + "d(1).\n"
            + ":- public probe/1.\n"
            // First choice 'a': retract d(1). Backtrack to 'b': re-call
            // d(X) — must fail (d(1) is gone), not resurrect d(1).
            + "probe(R) :- findall(C-X, (choice(C), ( retract(d(1)) -> X = retracted ; ( d(X) -> true ; X = none ) )), R).\n");
        var sol = engine.Query("probe(R).");
        Assert.True(sol.Success);
        Assert.Equal("-(a, retracted),-(b, none)", ListString(sol["R"]!));
    }
}
