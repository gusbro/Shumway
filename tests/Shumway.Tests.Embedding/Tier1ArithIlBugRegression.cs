using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Documented investigation of the Tier-1 IL correctness bug
/// that surfaces when promotion is enabled against Blint.pl.
/// Both tests in this class currently PASS — neither reproduces
/// the actual Blint failure mode despite mirroring the DCG-
/// transformed predicate shape that fails there. The bug is
/// state / timing dependent in ways the synthetic repros so far
/// have not captured.
///
/// <para>Symptom in Blint: with
/// <c>engine.IlPromotion.Threshold = 32</c>, Blint linting
/// itself crashes with <c>error(instantiation_error, &lt;/2)</c>
/// shortly after seven hot predicates promote — most of them
/// DCG-transformed parsers like <c>parse_subgoal/5</c>,
/// <c>parse_postfix_op/6</c>, <c>parse_infix_op/6</c>,
/// <c>parse_prefix_op/4</c>.</para>
///
/// <para>What chunk 172's investigation established (using a
/// diagnostic <c>SHUMWAY_IL_TRACE_BUILTIN</c> environment
/// variable that logs every IL <c>CallBuiltin</c> / <c>Execute</c>
/// + a dump of X registers and CP chain at the
/// <c>instantiation_error</c> throw site in
/// <c>ArithmeticEvaluator.Evaluate</c>):</para>
/// <list type="bullet">
/// <item>The failing call is <c>validate_postfix_op/3</c> (only
///   called from <c>parse_postfix_op/6</c>, via tail-call), and
///   the bytecode for both predicates is correct — Tier-0 runs
///   them with no issue.</item>
/// <item>At the moment of the <c>execute validate_postfix_op</c>
///   the IL has X[0]=Ref→Atom (Assoc, fine) and X[2]=Int (CurPrec,
///   fine) but X[1]=Ref(N→N:Ref) — truly unbound. X[1] should
///   have been <c>OpPrec</c>, set by <c>put_value_y 1, 1</c>
///   from <c>parse_postfix_op</c>'s Y[1].</item>
/// <item>X[1]'s heap home (N) does NOT match the OpPrec heap
///   home (M) that the immediately preceding parse_op invocation
///   bound. They are two different cells — so the running theory
///   is that <c>parse_postfix_op</c>'s Y[1] held a Ref to a
///   different cell from the one passed to parse_op, OR the
///   meta-CP / resume cascade rewrote Y[1] mid-body.</item>
/// <item>The IL's <c>put_value_y</c> emission itself looks
///   correct in isolation (SetRegister(arg, GetY(slot))) — the
///   discrepancy must come from upstream state mutation.</item>
/// </list>
///
/// <para>Synthetic repro attempts (the two tests below) match the
/// DCG-transform shape and the meta-CP / promote-at-threshold-1
/// timing but still pass — so the actual bug needs Blint's
/// specific call graph (depth of nested DCG bodies, alternation
/// patterns through parse_subgoal_g_cont) to surface. Further
/// triage needs an in-engine breakpoint at parse_postfix_op's
/// IL just before the tail-call to validate_postfix_op, dumping
/// Y[1]'s heap home and walking it back to its allocation site.
/// Recorded as a Phase 15+ candidate.</para>
/// </summary>
public class Tier1ArithIlBugRegression
{
    [Fact]
    public void Tier1_DcgWithStateArgs_BraceLessThan_Works()
    {
        // Mirrors Blint's parse_subgoal/5 exactly: 5-arg head where
        // arg 3 (S0) is left in X-register (not stored in Y),
        // arg 4 (S) is stored in Y, with a middle Call between Y
        // setups and the brace < goal.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(@"
:- public driver/2.
driver(MedOut, ListOut) :-
  ps(100, NewPrec, MedOut, [a, b, c], ListOut),
  % Use NewPrec to force its binding
  number(NewPrec).

ps(MaxPrec, NewPrec, SubGoal, S0, S) :-
  ps_g(MedPrec, SubGoal1, Cont, S0, S1),
  MedPrec < MaxPrec,
  ps_g_cont(Cont, MedPrec, SubGoal1, NewPrec, SubGoal, S1, S).

% Multi-clause ps_g to force a meta-CP push at the IL caller's site.
ps_g(200, sg_high, cont, [high|R], R).
ps_g(20, sg_low, cont, [_|R], R).
ps_g_cont(_, MedPrec, _, NewPrec, MedPrec, R, R) :- NewPrec = MedPrec.
");
        var s1 = e.Query("driver(M, L).");
        Assert.True(s1.Success, "Tier-1 driver call #1 should succeed");
        var s2 = e.Query("driver(M, L).");
        Assert.True(s2.Success, "Tier-1 driver call #2 should succeed");
        Assert.Equal("20", s2["M"]!.ToString());
    }

    [Fact]
    public void Tier0_BaselineWorks_AndTier1_Matches()
    {
        // Baseline: Tier-0 (no IL promotion).
        var e0 = new PrologEngine();
        Consult(e0);
        var s0 = e0.Query("driver(R).");
        Assert.True(s0.Success, "Tier-0 driver should succeed");
        Assert.Equal("20", s0["R"]!.ToString());

        // Tier-1: enable promotion at threshold 1 so the second call
        // runs against the IL delegate. We invoke twice — the first
        // call records the invocation and may already swap to IL on
        // the threshold; the second call always sees IL.
        var e1 = new PrologEngine();
        e1.IlPromotion.Threshold = 1;
        Consult(e1);
        var t1 = e1.Query("driver(R1).");
        Assert.True(t1.Success, "Tier-1 driver call #1 should succeed");
        Assert.Equal("20", t1["R1"]!.ToString());
        var t2 = e1.Query("driver(R2).");
        Assert.True(t2.Success, "Tier-1 driver call #2 should succeed");
        Assert.Equal("20", t2["R2"]!.ToString());
    }

    private static void Consult(PrologEngine e)
    {
        e.ConsultString(@"
:- public driver/1.
driver(Out) :-
  cmp_dcg(100, 50, [foo], Out).

% Shape of Blint's parse_subgoal/5 after the DCG transform:
% multiple permanent vars (MaxPrec, NewPrec, S0, S),
% one call producing MedPrec, a brace-escaped < goal,
% then another call.
cmp_dcg(MaxPrec, NewPrec, In, Out) :-
  get_med(MedPrec, In, Mid),
  MedPrec < MaxPrec,
  emit_result(MedPrec, NewPrec, Mid, Out).

get_med(20, [_|R], R).
% Out carries MedPrec so the test can verify the < site actually ran
% with the right registers.
emit_result(MedPrec, _NewPrec, _Mid, MedPrec).
");
    }
}
