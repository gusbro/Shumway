using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 56: <c>call/N</c> now pushes a runtime choice point so a goal
/// with multiple solutions enumerates via standard Prolog backtracking
/// instead of stopping at the first solution. Builtins that decompose
/// lists or atoms (<c>append/3</c>, <c>atom_concat/3</c>,
/// <c>sub_atom/5</c>) also gain their fully-non-deterministic split
/// modes by using the same CP machinery.
/// </summary>
public class Chunk56Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ============================================================================
    // call/N enumerates all solutions
    // ============================================================================

    [Fact]
    public void Call1_EnumeratesAllMemberSolutions()
    {
        // call(member(X, [a, b, c])) should enumerate X = a, b, c.
        var engine = new PrologEngine();
        var sols = engine.QueryAll("call(member(X, [a, b, c])).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Atom("a"), sols[0]["X"]);
        Assert.Equal(Atom("b"), sols[1]["X"]);
        Assert.Equal(Atom("c"), sols[2]["X"]);
    }

    [Fact]
    public void Call2_BacktracksThroughExtraArg()
    {
        // call(member, [1, 2, 3], X) is call(member(X, [1, 2, 3])) after
        // arg-appending. Same enumeration.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public membr/2.
            membr(X, L) :- member(X, L).
            """);
        var sols = engine.QueryAll("call(membr, X, [10, 20, 30]).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Int(10), sols[0]["X"]);
        Assert.Equal(Int(20), sols[1]["X"]);
        Assert.Equal(Int(30), sols[2]["X"]);
    }

    [Fact]
    public void CallInsideConjunction_FailsAfterAllAlternativesExhausted()
    {
        // call(member(X, [1, 2, 3])), X > 5 — no member is > 5, so the
        // overall query fails after backtracking through all three.
        var engine = new PrologEngine();
        Assert.False(engine.Query(
            "call(member(X, [1, 2, 3])), X > 5.").Success);
    }

    [Fact]
    public void CallInsideConjunction_FindsBacktrackedMatch()
    {
        // call(member(X, [1, foo, 3])), atom(X) — backtrack until X=foo.
        var engine = new PrologEngine();
        var sol = engine.Query("call(member(X, [1, foo, 3])), atom(X).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo"), sol["X"]);
    }

    // ============================================================================
    // forall via direct call(Cond), \+ call(Then) pattern now correct
    // ============================================================================

    [Fact]
    public void UserForall_FindsCounterExampleViaBacktracking()
    {
        // The prelude's forall is a builtin (chunk 54 workaround). But a
        // user-defined forall using the classic Prolog pattern should now
        // also work, since call/N backtracks.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public myforall/2.
            :- public counter/2.
            myforall(C, T) :- \+ counter(C, T).
            counter(C, T) :- call(C), \+ call(T).
            """);
        // All ints — no counter-example.
        Assert.True(engine.Query(
            "myforall(member(X, [1, 2, 3]), integer(X)).").Success);
        // Has counter-example (X=foo).
        Assert.False(engine.Query(
            "myforall(member(X, [1, foo, 3]), integer(X)).").Success);
    }

    // ============================================================================
    // Non-deterministic modes for append/3
    // ============================================================================

    [Fact]
    public void Append_NonDet_SplitsList()
    {
        // append(X, Y, [a, b, c]) should enumerate four splits:
        // ([], [a,b,c]), ([a], [b,c]), ([a,b], [c]), ([a,b,c], []).
        var engine = new PrologEngine();
        var sols = engine.QueryAll("append(X, Y, [a, b, c]).").ToList();
        Assert.Equal(4, sols.Count);
    }

    [Fact]
    public void Append_NonDet_FirstSplitIsEmptyPrefix()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("append(X, Y, [a, b, c]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("[]"), sol["X"]);
    }

    // ============================================================================
    // Non-deterministic modes for atom_concat/3
    // ============================================================================

    [Fact]
    public void AtomConcat_NonDet_SplitsAtom()
    {
        // atom_concat(X, Y, hello) should enumerate splits:
        // ('', hello), (h, ello), (he, llo), (hel, lo), (hell, o), (hello, '').
        var engine = new PrologEngine();
        var sols = engine.QueryAll("atom_concat(X, Y, hello).").ToList();
        Assert.Equal(6, sols.Count);
    }

    // ============================================================================
    // Non-deterministic modes for sub_atom/5
    // ============================================================================

    [Fact]
    public void SubAtom_NonDet_EnumeratesDecompositions()
    {
        // sub_atom(abc, B, L, A, Sub) enumerates every (before, length,
        // after, sub) decomposition. For "abc" there are 4*5/2 = 10
        // possible substring positions (including empty subs).
        var engine = new PrologEngine();
        var sols = engine.QueryAll("sub_atom(abc, _, _, _, _).").ToList();
        // Number of substrings of a 3-char atom (including '' at every
        // position): 1 (empty starts at 0..3) + 3 (length 1) + 2 (length 2)
        // + 1 (length 3) = 4 empty + 3 + 2 + 1 = 10.
        Assert.Equal(10, sols.Count);
    }

    [Fact]
    public void SubAtom_GroundSub_FindsMatchPosition()
    {
        // sub_atom(hello, B, 2, A, ll) should find exactly one match at
        // B = 2 (Before = "he") with A = 1 (After = "o").
        var engine = new PrologEngine();
        var sol = engine.Query("sub_atom(hello, B, 2, A, ll).");
        Assert.True(sol.Success);
        Assert.Equal(Int(2), sol["B"]);
        Assert.Equal(Int(1), sol["A"]);
    }
}
