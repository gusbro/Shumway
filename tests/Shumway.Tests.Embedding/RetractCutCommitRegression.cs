using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Bug class root-caused while linting Blint.pl: a <c>retract/1</c>
/// followed by an immediate cut inside an inner predicate is NOT
/// properly committed when an outer predicate sibling subsequently
/// fails. On the failure-driven backtrack, retract re-enters and
/// enumerates *more* clauses — every matching clause gets retracted
/// instead of just the first one.
///
/// <para>Pattern (matches Blint's <c>s_get_char(_, Char) :-
/// retract(next_char_i(XChar)), !, Char = XChar.</c> inside
/// <c>token('(' ...)</c> whose <c>openpar_or_operator</c> first
/// clause fails on non-operator chars):</para>
///
/// <code>
///   :- dynamic q/1.
///   outer :- inner_with_cut, sibling_that_fails.
///   inner_with_cut :- retract(q(_X)), !.
///   sibling_that_fails :- 1 = 2.
///
///   ?- assertz(q(a)), assertz(q(b)), assertz(q(c)),
///      ( outer ; true ),
///      findall(Z, q(Z), L).
///   % expected L = [b, c]   (cut commits, one retract)
///   % actual   L = [c]      (cut leaks, two retracts on backtrack)
/// </code>
///
/// Currently marked as <c>Skip</c> — the underlying engine bug is
/// the next item on the Phase-15 punch list. Re-enable when the
/// retract-cut commit is fixed.
/// </summary>
public class RetractCutCommitRegression
{
    [Fact(Skip = "Engine bug under investigation — see class doc-comment")]
    public void Retract_WithCut_AndSiblingFailure_DoesNotEnumerateOnBacktrack()
    {
        var e = new PrologEngine();
        e.ConsultString(@"
:- dynamic q/1.
:- public test/1.
test(L) :-
  assertz(q(a)), assertz(q(b)), assertz(q(c)),
  ( outer_clause ; true ),
  findall(Z, q(Z), L).

outer_clause :-
  inner_with_cut,
  sibling_that_fails.

inner_with_cut :- retract(q(_X)), !.
sibling_that_fails :- Y = 1, Y = 2.
");
        var sol = e.Query("test(L).");
        Assert.True(sol.Success);
        // ISO semantics: cut commits, only q(a) retracted. L = [b, c].
        var l = sol["L"]!.ToString();
        Assert.Contains("b", l);
        Assert.Contains("c", l);
        Assert.DoesNotContain("[c]", l);  // [c] would prove the over-retract bug.
    }

    [Fact(Skip = "Engine bug under investigation — see class doc-comment")]
    public void Findall_Call_ReturnsList_NotCompound()
    {
        // Companion to the above: in the same scenario, findall over
        // a `call/1` of a dynamic predicate returns a compound term
        // (e.g. `q(b)`) instead of a list. This is downstream
        // corruption from the same retract/cut leak — findall's CP
        // setup observes the stale state.
        var e = new PrologEngine();
        e.ConsultString(@"
:- dynamic q/1.
:- public test/1.
test(Keys) :-
  assertz(q(a)), assertz(q(b)), assertz(q(c)),
  ( outer_clause ; true ),
  findall(K, call(q(K)), Keys).

outer_clause :-
  inner_with_cut,
  sibling_that_fails.

inner_with_cut :- retract(q(_X)), !.
sibling_that_fails :- Y = 1, Y = 2.
");
        var sol = e.Query("test(Keys).");
        Assert.True(sol.Success);
        // Whatever the retract semantics, findall MUST return a list.
        Assert.StartsWith("[", sol["Keys"]!.ToString());
    }
}
