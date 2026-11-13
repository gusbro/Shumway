using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A region emitted as several methods over its one cursor space
/// (region-jit-limits.md, "A region in several methods") computes what the
/// interpreter computes. The partition target is set so low that every
/// member is a partition of its own, and every clause of a member and every
/// node of an index too: every call, return, backtrack, cut and wake
/// crosses from one method to another. Each test checks that the region was
/// emitted in several methods and that transfers between them ran; the
/// tests of members in pieces, that a member was cut.</summary>
// Sets the partition target and the transfer counter, process statics: it
// runs alone, in the gate's exclusive pass.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class RegionPartitionTests
{
    private static PrologEngine Engine(string program, int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(program);
        return e;
    }

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    // The binding of L, written.
    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query($"{goal.TrimEnd('.')}, term_to_atom(L, Text).");
        Assert.True(r.Success, goal);
        return r["Text"]!.ToString()!;
    }

    /// <summary>The goal binds L on Tier-0 and on Tier-1 with the region
    /// rooted at <paramref name="root"/> cut into partitions, the same; the
    /// region was emitted in several methods and transfers between them ran.
    /// The first Tier-1 run compiles the region; the second runs it.</summary>
    private static string SameOnBothTiers(string program, (string Name, int Arity) root, string goal,
        bool pieces = false)
    {
        int partitionRefs = IlRegionCost.PartitionRefs;
        bool counting = IlPredicateCompiler.CountPartitionTransfers;
        try
        {
            IlRegionCost.PartitionRefs = 1;
            IlPredicateCompiler.CountPartitionTransfers = true;
            IlRegionCost.ResetDiagnostics();
            string expected = Answer(Engine(program, 0), goal);

            var e = Engine(program, 1);
            int piecesBefore = Volatile.Read(ref IlPredicateCompiler.MembersInPieces);
            Assert.Equal(expected, Answer(e, goal));
            if (pieces)
                Assert.True(Volatile.Read(ref IlPredicateCompiler.MembersInPieces) > piecesBefore, "no member cut in pieces");
            int rootFid = Fid(root.Name, root.Arity);
            Assert.True(e.IlPromotion.IsPromoted(rootFid), $"{root.Name}/{root.Arity} not promoted");
            var methods = IlRegionCost.Emissions.Where(r => r.RootFid == rootFid).ToList();
            Assert.True(methods.Any(r => r.Partitions >= 2),
                $"{root.Name}/{root.Arity}: no region in several methods ("
                + string.Join(", ", methods.Select(r => $"{r.Members} members, partition {r.Partition} of {r.Partitions}")) + ")");

            long before = Interlocked.Read(ref IlPredicateCompiler.PartitionTransfers);
            Assert.Equal(expected, Answer(e, goal));
            long transfers = Interlocked.Read(ref IlPredicateCompiler.PartitionTransfers) - before;
            Assert.True(transfers > 0, "no transfer between partitions");
            return expected;
        }
        finally
        {
            IlRegionCost.PartitionRefs = partitionRefs;
            IlPredicateCompiler.CountPartitionTransfers = counting;
        }
    }

    [Fact]
    public void BacktrackingCrossesFromOnePartitionToAnother()
    {
        const string program = @"
:- public ba_pair/2.
ba_pair(X, Y) :- ba_left(X), ba_right(X, Y).
ba_left(1).
ba_left(2).
ba_left(3).
ba_right(X, Y) :- ba_mid(X, Z), Y is Z * 10.
ba_right(X, Y) :- X > 1, Y is X + 100.
ba_mid(X, X).
ba_mid(X, Z) :- Z is X + 1.
";
        string answer = SameOnBothTiers(program, ("ba_pair", 2), "findall(X-Y, ba_pair(X, Y), L).");
        Assert.Equal("[1-10,1-20,2-20,2-30,2-102,3-30,3-40,3-103]", answer.Replace(" ", ""));
    }

    [Fact]
    public void AnIndexDispatchesIntoALaterPartition()
    {
        // ix_tab/2 is indexed on its first argument; the bucket of a holds
        // three clauses and the variable clause, whose choice points resume
        // in its partition.
        const string program = @"
:- public ix_run/2.
ix_run(K, N) :- ix_pre(K), ix_tab(K, N).
ix_pre(_).
ix_tab(a, 1).
ix_tab(b, 2).
ix_tab(X, 3) :- atom(X).
ix_tab(a, 4).
ix_tab(c, 5).
ix_tab(f(_), 6).
";
        string answer = SameOnBothTiers(program, ("ix_run", 2),
            "findall(K-N, (member(K, [a, b, c, d, f(1), 1]), ix_run(K, N)), L).");
        Assert.Equal("[a-1,a-3,a-4,b-2,b-3,c-3,c-5,d-3,f(1)-6]", answer.Replace(" ", ""));
    }

    [Fact]
    public void ACutInALateClausePrunesTheChoicePointsOfOtherPartitions()
    {
        const string program = @"
:- public ct_run/2.
ct_run(X, Y) :- ct_pick(X, Y).
ct_pick(X, Y) :- X < 0, !, Y = neg.
ct_pick(X, Y) :- ct_gen(X, Y), Y > 2, !.
ct_pick(X, Y) :- ct_first(X, Y).
ct_pick(_, none).
ct_gen(X, Y) :- ct_num(Y), Y =< X.
ct_first(X, Y) :- ct_num(Y), Y =< X, !.
ct_num(1).
ct_num(2).
ct_num(3).
ct_num(4).
";
        string answer = SameOnBothTiers(program, ("ct_run", 2),
            "findall(X-Y, (member(X, [-1, 0, 1, 3, 5]), ct_run(X, Y)), L).");
        Assert.Equal("[-1-neg,0-none,1-1,1-none,3-3,5-3]", answer.Replace(" ", ""));
    }

    [Fact]
    public void ABacktrackableBuiltinResumesInItsPartition()
    {
        const string program = @"
:- public bb_pair/2.
bb_pair(X, Y) :- bb_gen(X), bb_gen2(X, Y).
bb_gen(X) :- between(1, 3, X).
bb_gen2(X, Y) :- between(X, 3, Y), Y =\= 2.
";
        string answer = SameOnBothTiers(program, ("bb_pair", 2), "findall(X-Y, bb_pair(X, Y), L).");
        Assert.Equal("[1-1,1-3,2-3,3-3]", answer.Replace(" ", ""));
    }

    [Fact]
    public void IfThenElseAndNegationCallIntoOtherPartitions()
    {
        const string program = @"
:- public ite_run/2.
ite_run(X, R) :- ( ite_test(X) -> R = yes ; \+ ite_neg(X) -> R = no ; R = maybe ).
ite_test(X) :- ite_num(X), X > 2.
ite_neg(X) :- ite_num(Y), Y =:= X.
ite_num(1).
ite_num(2).
ite_num(3).
";
        string answer = SameOnBothTiers(program, ("ite_run", 2),
            "findall(X-R, (member(X, [1, 3, 5]), ite_run(X, R)), L).");
        Assert.Equal("[1-maybe,3-yes,5-no]", answer.Replace(" ", ""));
    }

    [Fact]
    public void AnExceptionThrownInOnePartitionIsCaughtOutsideTheRegion()
    {
        const string program = @"
:- public ex_a/2.
ex_a(X, R) :- ex_b(X, Y), ex_c(Y, R).
ex_b(X, _) :- X > 2, throw(big(X)).
ex_b(X, Y) :- Y is X - 1.
ex_c(Y, R) :- R is 10 // Y.
";
        string answer = SameOnBothTiers(program, ("ex_a", 2),
            "findall(X-R, (member(X, [1, 2, 3]), catch(ex_a(X, R), E, (E = error(F, _) -> R = F ; R = E))), L).");
        Assert.Equal("[1-evaluation_error(zero_divisor),2-10,3-big(3)]", answer.Replace(" ", ""));
    }

    [Fact]
    public void AWakeReentersThePartitionThatHoldsItsWakePoint()
    {
        // The wake point is in wk_val/1's head, in a partition of its own; the
        // woken goal runs and the code re-enters there, on each solution.
        const string program = @"
:- use_module(library(coroutining)).
:- public wk_run/1.
wk_run(Out) :- wk_setup(X, Out), wk_bind(X).
wk_setup(X, Out) :- freeze(X, wk_woken(X, Out)).
wk_woken(X, Out) :- Out = got(X).
wk_bind(X) :- wk_val(X), wk_after(X).
wk_val(1).
wk_val(2).
wk_val(3).
wk_after(X) :- X =\= 2.
";
        string answer = SameOnBothTiers(program, ("wk_run", 1), "findall(O, wk_run(O), L).");
        Assert.Equal("[got(1),got(3)]", answer.Replace(" ", ""));
    }

    [Fact]
    public void TailCallsBetweenPartitionsDoNotGrowTheStack()
    {
        // A million transfers by tail call: if a transfer stacked a frame,
        // the process would overflow its stack.
        const string program = @"
:- public dr_run/2.
dr_run(N, R) :- dr_even(N, 0, R).
dr_even(0, A, A) :- !.
dr_even(N, A, R) :- N1 is N - 1, A1 is A + 2, dr_odd(N1, A1, R).
dr_odd(0, A, A) :- !.
dr_odd(N, A, R) :- N1 is N - 1, A1 is A + 1, dr_even(N1, A1, R).
";
        string answer = SameOnBothTiers(program, ("dr_run", 2), "dr_run(1000000, L).");
        Assert.Equal("1500000", answer);
    }

    [Fact]
    public void CallsAndReturnsBetweenPartitionsDoNotGrowTheStack()
    {
        // Non-tail recursion a hundred thousand deep, alternating between two
        // partitions: every call and every return is a transfer.
        const string program = @"
:- public nt_run/2.
nt_run(N, R) :- numlist(1, N, L), nt_a(L, R).
nt_a([], 0).
nt_a([X|T], R) :- nt_b(T, R0), R is R0 + X.
nt_b([], 0).
nt_b([X|T], R) :- nt_a(T, R0), R is R0 + 2 * X.
";
        string answer = SameOnBothTiers(program, ("nt_run", 2), "nt_run(100000, L).");
        // Odd positions count once, even positions twice.
        long expected = 0;
        for (long i = 1; i <= 100000; i++) expected += i % 2 == 1 ? i : 2 * i;
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), answer);
    }

    [Fact]
    public void AChainOfClausesIsCutBetweenItsClauses()
    {
        // Heads of variables only: a try_me_else chain, each clause a piece.
        const string program = @"
:- public ch_alt/2.
ch_alt(X, Y) :- Y is X + 1.
ch_alt(X, Y) :- X > 1, Y is X * 10.
ch_alt(X, Y) :- ch_more(X, Y).
ch_alt(X, Y) :- Y = neg(X).
ch_more(X, Y) :- X > 2, Y = more(X).
";
        string answer = SameOnBothTiers(program, ("ch_alt", 2),
            "findall(X-Y, (member(X, [1, 2, 3]), ch_alt(X, Y)), L).", pieces: true);
        Assert.Equal("[1-2,1-neg(1),2-3,2-20,2-neg(2),3-4,3-30,3-more(3),3-neg(3)]", answer.Replace(" ", ""));
    }

    [Fact]
    public void AGuardThatFailsInOnePieceGoesOnInTheNextPiece()
    {
        // A guard clause commits without a choice point (ADR-031); failing,
        // it goes on at the next node of its bucket, in another method.
        const string program = @"
:- public gp_run/2.
gp_run(X, Y) :- gp_pick(X, Y).
gp_pick(X, Y) :- X < 0, !, Y = neg.
gp_pick(X, Y) :- X > 10, !, Y = big.
gp_pick(_, mid).
";
        string answer = SameOnBothTiers(program, ("gp_run", 2),
            "findall(X-Y, (member(X, [-1, 5, 20]), gp_run(X, Y)), L).", pieces: true);
        Assert.Equal("[-1-neg,5-mid,20-big]", answer.Replace(" ", ""));
    }

    [Fact]
    public void AClauseBodyTwoBucketsShareRunsInTheMethodsOfBoth()
    {
        // The clauses with a variable key are in every bucket; cut by node,
        // their bodies are copied into each method that runs them.
        const string program = @"
:- public sb_run/2.
sb_run(K, N) :- sb_tab(K, N).
sb_tab(a, 1).
sb_tab(X, 2) :- sb_any(X).
sb_tab(b, 3).
sb_tab(X, 4) :- sb_any(X), X \== c.
sb_tab(c, 5).
sb_any(_).
";
        string answer = SameOnBothTiers(program, ("sb_run", 2),
            "findall(K-N, (member(K, [a, b, c, d]), sb_run(K, N)), L1), "
            + "findall(K-N, (sb_run(K0, N), (var(K0) -> K = v ; K = K0)), L2), L = L1/L2.", pieces: true);
        Assert.Equal("[a-1,a-2,a-4,b-2,b-3,b-4,c-2,c-5,d-2,d-4]/[a-1,v-2,b-3,v-4,c-5]", answer.Replace(" ", ""));
    }

    [Fact]
    public void ADynamicPredicateAGuardInlinedChangesInItsPiece()
    {
        // The guard inlines the dynamic predicate's clauses (ADR-034); once
        // it changes, the clause takes its fallback, in its own method.
        const string program = @"
:- dynamic(dg_lim/1).
:- public dg_run/2.
dg_lim(5).
dg_run(X, R) :- dg_pick(X, R).
dg_pick(X, R) :- dg_lim(L), X < L, !, R = small.
dg_pick(_, big).
";
        string answer = SameOnBothTiers(program, ("dg_run", 2),
            "findall(X-R, (member(X, [1, 7]), dg_run(X, R)), L1), retract(dg_lim(5)), assertz(dg_lim(10)), "
            + "findall(X-R, (member(X, [1, 7]), dg_run(X, R)), L2), retract(dg_lim(10)), assertz(dg_lim(5)), L = L1/L2.",
            pieces: true);
        Assert.Equal("[1-small,7-big]/[1-small,7-small]", answer.Replace(" ", ""));
    }

    [Fact]
    public void WithoutThePartitionTargetTheRegionIsOneMethod()
    {
        // The control: the same region as one method, so the tests above
        // see the cut and not something else.
        const string program = @"
:- public ba_pair/2.
ba_pair(X, Y) :- ba_left(X), ba_right(X, Y).
ba_left(1).
ba_left(2).
ba_right(X, Y) :- Y is X * 10.
";
        int partitionRefs = IlRegionCost.PartitionRefs;
        try
        {
            IlRegionCost.PartitionRefs = 0;
            IlRegionCost.ResetDiagnostics();
            var e = Engine(program, 1);
            Assert.Equal("[1-10,2-20]", Answer(e, "findall(X-Y, ba_pair(X, Y), L).").Replace(" ", ""));
            var methods = IlRegionCost.Emissions.Where(r => r.RootFid == Fid("ba_pair", 2)).ToList();
            Assert.NotEmpty(methods);
            Assert.All(methods, r => Assert.Equal(1, r.Partitions));
            Assert.Contains(methods, r => r.Members >= 2);
        }
        finally
        {
            IlRegionCost.PartitionRefs = partitionRefs;
        }
    }
}
