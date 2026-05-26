using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Failing-test (Skip-marked) for the Tier-1 IL correctness bug
/// surfaced when enabling auto-promotion against Blint.pl.
///
/// <para>Symptom: with <c>engine.IlPromotion.Threshold = 32</c>,
/// Blint linting Blint.pl crashes with
/// <c>error(instantiation_error, </2)</c>. Tracing
/// <c>IlPromotionStore.RecordInvocation</c> shows the last
/// promotion before the crash is <c>parse_subgoal/5</c> — the
/// DCG-transformed version of Blint's
/// <c>parse_subgoal(MaxPrec, NewPrec, SubGoal) --> ...,
/// { MedPrec &lt; MaxPrec }, ...</c>. The IL compiler emits a
/// <c>CallBuiltin &lt;/2</c> for the brace-escaped arithmetic
/// comparison, but the IL emit path doesn't appear to put
/// <c>MedPrec</c> / <c>MaxPrec</c> into <c>X[0]</c> /
/// <c>X[1]</c> before the call — so the <c>&lt;</c> builtin
/// reads two Refs and raises instantiation_error.</para>
///
/// <para>The Tier-0 bytecode path runs the same predicate
/// without issue, which is why no existing test catches this.
/// Suspect: a missing put_value / put_variable emit, or a
/// register-allocation bug specific to the multi-clause /
/// indexed-atom IL emitter for predicates with brace-escaped
/// comparison goals.</para>
///
/// <para>The Skip is in place so CI stays green while the
/// fix is investigated. Re-enable when the IL emit is
/// corrected.</para>
/// </summary>
public class Tier1ArithIlBugRegression
{
    [Fact(Skip = "Tier-1 IL correctness bug — see class doc-comment (chunk 171 follow-up)")]
    public void PromotedPredicate_WithBraceLessThan_DoesNotLoseRegisters()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(@"
:- public driver/1.
% Force the promotion to fire and then exercise the `<` site:
driver(Result) :-
  warm_then_compare(50, 100, R1),
  warm_then_compare(50, 100, R2),
  warm_then_compare(50, 100, Result),
  R1 = R2.

% DCG-style: parametrised body with a `{ A < B }` brace escape that
% becomes the IL CallBuiltin emission shape parse_subgoal/5 uses.
warm_then_compare(A, B, less) :-
  cmp_inner(A, B).

cmp_inner(A, B) :- A < B.
");
        var sol = e.Query("driver(R).");
        Assert.True(sol.Success);
        Assert.Equal("less", sol["R"]!.ToString());
    }
}
