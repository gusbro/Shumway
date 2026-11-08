using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>clause/2 finds its candidates through indexes: the static
/// clauses of a predicate from a per-program functor index (rebuilt when
/// the program changes), the dynamic ones through the first-argument index
/// retract/1 keeps, so a lookup costs its candidates, not the program or
/// the predicate. The rest pins what clause/2 answers: privacy, errors,
/// grammar rules, the logical update view, and that a later consult is
/// seen.</summary>
public sealed class ClauseLookupIndexTests
{
    private static PrologEngine Engine(bool tier1 = true)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        if (!tier1) e.IlPromotion.Threshold = 0;
        return e;
    }

    private static bool Holds(PrologEngine e, string query) => e.Query(query).Success;

    [Fact]
    public void AKeyedLookupInALargeDynamicPredicateLooksAtItsCandidatesOnly()
    {
        var e = Engine();
        e.ConsultString(":- dynamic(p/2).");
        Assert.True(Holds(e, "between(1, 10000, I), assertz(p(I, I)), fail ; true."));
        Assert.True(Holds(e, "assertz(p(k(1), c)), assertz(p([x], l)), assertz(p(a, atom))."));

        long scanned = e.ClauseCandidatesScanned, tried = e.ClauseCandidatesTried;
        Assert.True(Holds(e, "clause(p(5000, X), B), X == 5000, B == true."));
        Assert.True(e.ClauseCandidatesScanned - scanned <= 2);
        Assert.Equal(1, e.ClauseCandidatesTried - tried);

        // A compound, a list and an atom key the same way; a key with no
        // clause looks at none.
        scanned = e.ClauseCandidatesScanned;
        Assert.True(Holds(e, "clause(p(k(N), c), true), N == 1, clause(p([Y], l), true), Y == x, "
            + "clause(p(a, A), true), A == atom, \\+ clause(p(99999, _), _), \\+ clause(p(zz, _), _)."));
        Assert.True(e.ClauseCandidatesScanned - scanned <= 6);

        // An unbound first argument has every clause as a candidate, as it must.
        Assert.True(Holds(e, "findall(X, clause(p(X, _), true), Xs), length(Xs, 10003)."));
    }

    [Fact]
    public void AClauseWhoseFirstArgumentKeysNothingStaysACandidateOfEveryLookup()
    {
        var e = Engine();
        e.ConsultString(":- dynamic(p/2).");
        Assert.True(Holds(e, "between(1, 1000, I), assertz(p(I, I)), fail ; true."));
        Assert.True(Holds(e, "assertz(p(_, any)), assertz(p(2.5, float))."));
        long scanned = e.ClauseCandidatesScanned;
        Assert.True(Holds(e, "findall(X, clause(p(500, X), true), Xs), Xs == [500, any]."));
        Assert.True(e.ClauseCandidatesScanned - scanned <= 3);
        // A float keys no bucket: every clause is looked at, the two that
        // can match are found.
        Assert.True(Holds(e, "findall(X, clause(p(2.5, X), true), Xs), Xs == [any, float]."));
    }

    [Fact]
    public void AStaticLookupLooksAtThePredicatesClauses_NotTheProgram()
    {
        var e = Engine();
        var program = new System.Text.StringBuilder(":- public(q/1).\nq(1).\nq(2) :- true.\nq(3) :- q(1), q(2).\n");
        for (int i = 0; i < 5000; i++) program.Append("r(").Append(i).Append(").\n");
        e.ConsultString(program.ToString());
        long scanned = e.ClauseCandidatesScanned;
        Assert.True(Holds(e, "findall(X-B, clause(q(X), B), L), L == [1-true, 2-true, 3-(q(1), q(2))]."));
        Assert.Equal(3, e.ClauseCandidatesScanned - scanned);
        scanned = e.ClauseCandidatesScanned;
        // Repeated lookups probe the index; nothing is rebuilt.
        for (int i = 0; i < 100; i++)
            Assert.True(Holds(e, "clause(q(2), B), B == true."));
        Assert.Equal(300, e.ClauseCandidatesScanned - scanned);
    }

    [Fact]
    public void ALaterConsultIsSeen()
    {
        var e = Engine();
        e.ConsultString(":- public(q/1).\nq(1).\n");
        Assert.True(Holds(e, "findall(X, clause(q(X), true), L), L == [1]."));
        e.ConsultString("q(2).\n");
        Assert.True(Holds(e, "findall(X, clause(q(X), true), L), L == [1, 2]."));
        e.ConsultString(":- module(cli_m).\n:- public(mq/1).\nmq(a).\n");
        Assert.True(Holds(e, "findall(X, clause(mq(X), true), L), L == [a]."));
        Assert.True(Holds(e, "findall(X, clause(q(X), true), L), L == [1, 2]."));
        // A grammar rule's clauses are its translated ones.
        e.ConsultString(":- public(greeting/2).\ngreeting --> [hello].\n");
        Assert.True(Holds(e, "findall(B, clause(greeting(_, _), B), Bs), length(Bs, 1), "
            + "clause(greeting(S0, S), Body), S0 = [hello], call(Body), S == []."));
    }

    [Fact]
    public void DynamicClausesFromSourceAndAssertsAreSeenInOrder_RetractedOnesAreNot()
    {
        var e = Engine();
        e.ConsultString(":- dynamic(d/1).\nd(1).\nd(2).\n");
        Assert.True(Holds(e, "assertz(d(3)), asserta(d(0)), findall(X, clause(d(X), true), L), L == [0, 1, 2, 3]."));
        Assert.True(Holds(e, "retract(d(2)), findall(X, clause(d(X), true), L), L == [0, 1, 3]."));
        // The slot a retract emptied is skipped by a keyed and an unkeyed lookup alike.
        Assert.True(Holds(e, "\\+ clause(d(2), _), clause(d(3), B), B == true."));
        Assert.True(Holds(e, "retractall(d(_)), \\+ clause(d(_), _)."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLogicalUpdateView_AssertsAndRetractsDuringAnEnumerationAreNotSeenByIt(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(":- dynamic(d/2).");
        Assert.True(Holds(e, "assertz(d(1, a)), assertz(d(2, b)), assertz(d(k, c)), assertz(d(_, any))."));
        Assert.True(Holds(e,
            "findall(K, (clause(d(K, _), true), (K == 1 -> assertz(d(new, z)) ; true)), Ks), "
            + "Ks = [1, 2, k, V], var(V), findall(Z, clause(d(new, Z), true), Zs), Zs == [any, z]."));
        Assert.True(Holds(e,
            "findall(K, (clause(d(K, _), true), (K == 1 -> retract(d(2, b)), retract(d(k, c)) ; true)), Ks), "
            + "Ks = [1, 2, k, V, new], var(V), findall(X, clause(d(2, X), true), Xs), Xs == [any]."));
        Assert.True(Holds(e,
            "findall(K, (clause(d(K, _), true), (K == 1 -> retract(d(new, z)) ; true)), Ks), "
            + "Ks = [1, V, new], var(V)."));
    }

    [Theory]
    [InlineData("catch(clause(q(_), _), error(permission_error(access, private_procedure, q/1), _), true).")]
    [InlineData("catch(clause(atom(_), _), error(permission_error(access, private_procedure, atom/1), _), true).")]
    [InlineData("catch(clause(length(_, _), _), error(permission_error(access, private_procedure, length/2), _), true).")]
    [InlineData("catch(clause((_ ; _), _), error(permission_error(access, private_procedure, (;)/2), _), true).")]
    [InlineData("catch(clause((a --> _), _), error(permission_error(access, private_procedure, (-->)/2), _), true).")]
    [InlineData("catch(clause(_, _), error(instantiation_error, _), true).")]
    [InlineData("catch(clause(1, _), error(type_error(callable, 1), _), true).")]
    [InlineData("catch(clause(pq(_), 1), error(type_error(callable, 1), _), true).")]
    [InlineData("\\+ clause(nonexistent(_), _).")]
    [InlineData("findall(X-B, clause(pq(X), B), L), L == [1-true, 2-(pq(1), !)].")]
    [InlineData("findall(X-B, clause(user:pq(X), B), L), L == [1-true, 2-(pq(1), !)].")]
    [InlineData("\\+ clause(other:pq(_), _).")]
    // The enumerator behind clause/2 is a $-named helper; a ball it raises
    // must not name it, the program called clause/2.
    [InlineData("catch(clause(q(_), _), error(permission_error(access, private_procedure, q/1), C), true),"
                + " \\+ ( C = N/_, atom(N), sub_atom(N, 0, 1, _, '$') ).")]
    [InlineData("catch(clause(user:q(_), _), error(permission_error(access, private_procedure, q/1), C), true),"
                + " \\+ ( C = N/_, atom(N), sub_atom(N, 0, 1, _, '$') ).")]
    public void PrivacyAndErrorsAreAsBefore(string query)
    {
        var e = Engine();
        e.ConsultString(":- public(pq/1).\npq(1).\npq(2) :- pq(1), !.\nq(1).\n");
        Assert.True(Holds(e, query));
    }

    /// <summary>The qualified form filters heads as the bare one does: a
    /// module's own dynamic read by a keyed head leaves no choice point.</summary>
    [Fact]
    public void AQualifiedLookupFiltersHeadsLikeTheBareOne()
    {
        var e = Engine();
        e.ConsultString(":- module(cli_dyn, []).\n:- dynamic(dd/1).\ndd(1).\ndd(2).\ndd(3).\n");
        Assert.True(Holds(e,
            "setup_call_cleanup(true, clause(cli_dyn:dd(2), B), Done = yes), B == true, nonvar(Done)."));
        Assert.True(Holds(e, "findall(X, clause(cli_dyn:dd(X), true), L), L == [1, 2, 3]."));
    }

    [Fact]
    public void AnSwiModuleReadsItsOwnStaticClauses()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "cli-swi-" + System.Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "climod.pl"),
                ":- module(climod, [own/1]).\nsecret(1).\nsecret(2).\n"
                + "own(L) :- findall(X, clause(secret(X), true), L).\n");
            var e = Engine();
            e.AddLibraryDirectory(tmp, "swi");
            e.ConsultString(":- use_module(library(climod)).");
            Assert.True(Holds(e, "own(L), L == [1, 2]."));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
