using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-031: a guard that holds a <c>catch/3</c> keeps its choice point. The
/// catch frame records the choice point register and the frame its helper was
/// called from, so a guard that kept its alternative in IL locals lost the
/// alternative when the recovery failed, and lost the rest of its clause when
/// the recovery succeeded. Tier-0 is the oracle.
/// </summary>
public class Adr031CatchInGuardTests
{
    private const string Program = @"
:- public cond_fails/2, cond_recovers/2, cond_binds/2,
          guard_fails/2, guard_recovers/2, negated/2, unmatched/2, warm/1.

% The condition of an if-then-else. atom_length/2 raises on a compound.
cond_fails(X, R) :-
    (   catch(atom_length(X, _), _, fail) -> R = then
    ;   R = else
    ).
cond_recovers(X, R) :-
    (   catch(atom_length(X, _), _, true) -> R = then
    ;   R = else
    ).
cond_binds(X, R) :-
    (   catch(atom_length(X, N), error(_, _), N = none) -> R = then(N)
    ;   R = else
    ).

% The guard of a clause.
guard_fails(X, R) :- catch(atom_length(X, _), _, fail), !, R = first.
guard_fails(_, second).
guard_recovers(X, R) :- catch(atom_length(X, _), _, true), !, R = first.
guard_recovers(_, second).

negated(X, R) :-
    (   \+ catch(atom_length(X, _), _, fail) -> R = then
    ;   R = else
    ).

unmatched(X, R) :-
    (   catch(atom_length(X, _), another_ball, fail) -> R = then
    ;   R = else
    ).

warm(0) :- !.
warm(N) :-
    cond_fails(abc, _), cond_recovers(abc, _), cond_binds(abc, _),
    guard_fails(abc, _), guard_recovers(abc, _), negated(abc, _), unmatched(abc, _),
    N1 is N - 1, warm(N1).
";

    private static PrologEngine Engine(Adr031CpFreeGuardTests.Mode m)
    {
        switch (m)
        {
            case Adr031CpFreeGuardTests.Mode.Tier0:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 0;
                e.ConsultString(Program);
                return e;
            }
            case Adr031CpFreeGuardTests.Mode.Tier1Runtime:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 1;
                e.ConsultString(Program);
                Assert.True(e.Query("warm(20000).").Success);
                return e;
            }
            default:
            {
                var bundle = new Bundle(new[] { new BundleEntry("adr031catch", Program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    public static TheoryData<Adr031CpFreeGuardTests.Mode> Modes => new()
    {
        Adr031CpFreeGuardTests.Mode.Tier0,
        Adr031CpFreeGuardTests.Mode.Tier1Runtime,
        Adr031CpFreeGuardTests.Mode.Tier1Bundle,
    };

    private static void Answers(PrologEngine e, string goal, string expected)
    {
        Assert.True(e.Query($"{goal}, R == {expected}.").Success, $"{goal} is not {expected}");
        Assert.Single(e.QueryAll($"{goal}."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheGoalRaises_AndTheRecoveryFails_TheElseRuns(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        Answers(e, "cond_fails(abc, R)", "then");
        Answers(e, "cond_fails(f(x), R)", "else");
        Answers(e, "guard_fails(abc, R)", "first");
        Answers(e, "guard_fails(f(x), R)", "second");
        Answers(e, "negated(abc, R)", "else");
        Answers(e, "negated(f(x), R)", "then");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheGoalRaises_AndTheRecoverySucceeds_TheRestOfTheClauseRuns(
        Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        Answers(e, "cond_recovers(abc, R)", "then");
        Answers(e, "cond_recovers(f(x), R)", "then");
        Answers(e, "cond_binds(abc, R)", "then(3)");
        Answers(e, "cond_binds(f(x), R)", "then(none)");
        Answers(e, "guard_recovers(abc, R)", "first");
        Answers(e, "guard_recovers(f(x), R)", "first");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ABallTheCatcherDoesNotMatch_ReachesTheCaller(Adr031CpFreeGuardTests.Mode m)
    {
        var e = Engine(m);
        Answers(e, "catch(unmatched(f(x), R), error(type_error(_, _), _), R = caught)", "caught");
        Answers(e, "unmatched(abc, R)", "then");
        Answers(e, "cond_fails(f(x), R)", "else");
    }

    // The guard that the compiled tier has to refuse: without this the modes
    // above could pass with the helper never reaching the recogniser.
    [Fact]
    public void TheCompiledBuild_RefusesToInlineTheCatchHelper()
    {
        const string Detail = "builtin:$catch_begin/2";
        IlPredicateCompiler.CpFreeGuardStats.RejectShapeDetail.TryGetValue(Detail, out long before);
        Engine(Adr031CpFreeGuardTests.Mode.Tier1Bundle);
        IlPredicateCompiler.CpFreeGuardStats.RejectShapeDetail.TryGetValue(Detail, out long after);
        Assert.True(after > before, "no guard of the program reached the catch helper");
    }
}
