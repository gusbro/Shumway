using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-055: a clause written <c>M:Head :- Body</c> defines Head in module M and
/// runs its body in the module it was written in. Each expectation is the
/// answer Scryer gives for the same source (SICStus by its manual).
/// </summary>
public class ClausesForAnotherModuleTests
{
    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    private static void Fails(PrologEngine e, string goal)
        => Assert.False(e.Query(goal).Success, goal);

    private static DirectoryInfo NewTempDirectory()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "shumway-adr055-" + Guid.NewGuid().ToString("N")));

    private const string Src = """
        :- module(src, [run/0]).
        user:hello(X) :- helper(X).
        helper(src).
        other:foo(1).
        other:foo(2) :- helper(src).
        src:own(7).
        user:fact(5).
        a:b:nest(1).
        run.
        """;

    [Fact]
    public void TheClauseJoinsTheTargetModuleAndItsBodyRunsInTheSource()
    {
        var e = new PrologEngine();
        e.ConsultString("helper(user).");
        e.ConsultString(Src);
        Succeeds(e, "hello(X), X == src.");
        Succeeds(e, "user:hello(X), X == src.");
        Succeeds(e, "fact(5).");
        Succeeds(e, "src:own(7).");
        Succeeds(e, "b:nest(1).");
    }

    [Fact]
    public void AMissingTargetModuleIsCreated()
    {
        var e = new PrologEngine();
        e.ConsultString(Src);
        Succeeds(e, "findall(X, other:foo(X), L), L == [1, 2].");
        Succeeds(e, "predicate_property(other:foo(_), defined).");
        // foo/1 is other's, not user's.
        Succeeds(e, "catch((foo(1), fail), error(existence_error(procedure, foo/1), _), true).");
    }

    [Fact]
    public void ClauseShowsTheBodyQualifiedWithTheSourceModule()
    {
        var e = new PrologEngine();
        e.ConsultString(Src);
        Succeeds(e, "clause(user:hello(X), B), B == src:helper(X).");
        Succeeds(e, "clause(other:foo(2), B), B == src:helper(src).");
        Succeeds(e, "clause(other:foo(1), B), B == true.");
    }

    [Fact]
    public void TheQualificationIsDistributedOverTheControlConstructs()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(ctl, [touch/0]).
            item(a). item(b). item(c).
            user:pick(X) :- item(X), !.
            user:both(X) :- ( item(X), X \== a -> true ; X = none ).
            touch.
            """);
        Succeeds(e, "findall(X, pick(X), L), L == [a].");
        Succeeds(e, "both(X), X == b.");
        Succeeds(e, "clause(user:pick(X), B), B == (ctl:item(X), !).");
    }

    [Fact]
    public void GoalArgumentsOfMetaPredicatesRunInTheSource()
    {
        var e = new PrologEngine();
        e.ConsultString("item(user_item).");
        e.ConsultString("""
            :- module(meta, [touch/0]).
            item(a). item(b).
            user:all(L) :- findall(X, item(X), L).
            user:none(X) :- \+ item(X).
            user:each :- forall(item(X), atom(X)).
            user:via_call(X) :- call(item, X).
            user:caught(X) :- catch(item(X), _, true).
            user:grouped(L) :- setof(X, item(X), L).
            user:once_item(X) :- once(item(X)).
            touch.
            """);
        Succeeds(e, "all(L), L == [a, b].");
        Succeeds(e, "none(z).");
        Fails(e, "none(a).");
        Succeeds(e, "each.");
        Succeeds(e, "findall(X, via_call(X), L), L == [a, b].");
        Succeeds(e, "findall(X, caught(X), L), L == [a, b].");
        Succeeds(e, "grouped(L), L == [a, b].");
        Succeeds(e, "once_item(X), X == a.");
    }

    [Fact]
    public void AMetaPredicateDeclarationMarksTheGoalArguments()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(lib).
            :- public(apply0/1).
            :- meta_predicate(apply0(0)).
            apply0(G) :- call(G).
            """);
        e.ConsultString("item(user_item).");
        e.ConsultString("""
            :- module(mp, [touch/0]).
            item(mp_item).
            user:via_apply(X) :- apply0(item(X)).
            touch.
            """);
        Succeeds(e, "via_apply(X), X == mp_item.");
    }

    [Fact]
    public void AGrammarRuleIsTranslatedAndItsNonTerminalsAreTheSources()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(gram, [touch/0]).
            user:greet --> [hi], who.
            who --> [bob].
            touch.
            """);
        Succeeds(e, "phrase(greet, [hi, bob]).");
        Fails(e, "phrase(greet, [hi, ann]).");
    }

    [Fact]
    public void AMultifilePredicateTakesClausesFromAnotherSource()
    {
        // A solver's rule table extended from outside: declared multifile,
        // the new rule joins the module's own (SICStus).
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(solver, [run/2]).
            :- multifile(step/3).
            run(P, Out) :- phrase(step(P), Out).
            step(double(X)) --> [X, X].
            step(single(X)) --> [X].
            """);
        e.ConsultString("""
            :- multifile(solver:step/3).
            solver:step(triple(X)) --> [X], solver:step(double(X)).
            """);
        Succeeds(e, "run(triple(a), L), L == [a, a, a].");
        Succeeds(e, "run(double(b), L), L == [b, b].");
        Succeeds(e, "run(single(c), L), L == [c].");
    }

    [Fact]
    public void WithoutMultifileAnotherSourceRedefinesThePredicate()
    {
        // SICStus and Scryer: a second source defining the predicate
        // replaces the first one's clauses, with a warning.
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("""
            :- module(ra, []).
            user:shared(1) :- ra_only.
            ra_only.
            """);
        e.ConsultString("""
            :- module(rb, []).
            user:shared(2) :- rb_only.
            rb_only.
            """);
        Succeeds(e, "findall(X, shared(X), L), L == [2].");
        Assert.Contains("user:shared/1 redefined by module rb", e.Warnings.ToString());
    }

    [Fact]
    public void TheTargetsOwnSourceAndAnotherRedefineEachOther()
    {
        var dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "own.pl");
            File.WriteAllText(path, """
                :- module(own, []).
                base(1).
                """);
            var e = new PrologEngine { Warnings = new StringWriter() };
            e.ConsultFile(path);
            // another source redefines own:base/1...
            e.ConsultString(":- module(other_src, []).\nown:base(2).\n");
            Succeeds(e, "findall(X, own:base(X), L), L == [2].");
            // ...and a reload of own's file redefines it back.
            e.ConsultFile(path);
            Succeeds(e, "findall(X, own:base(X), L), L == [1].");
            // A user source defining what a module gave user redefines it too.
            e.ConsultString(":- module(giver, []).\nuser:given(1).\n");
            e.ConsultString("given(9).");
            Succeeds(e, "findall(X, given(X), L), L == [9].");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ADynamicPredicateTakesTheClauseIntoItsStore()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(dm, [touch/0]).
            :- dynamic(user:dq/1).
            user:dq(1).
            user:dq(2).
            :- dynamic(own/1).
            dm:own(7).
            touch.
            """);
        Succeeds(e, "findall(X, clause(dq(X), true), L), L == [1, 2].");
        Succeeds(e, "retract(dq(1)).");
        Succeeds(e, "findall(X, dq(X), L), L == [2].");
        Succeeds(e, "retract(dm:own(7)).");
        Fails(e, "dm:own(7).");
    }

    [Fact]
    public void AClauseForAProtectedBuiltinIsRefused()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(pb, [touch/0]).
            user:atom_length(_, 42).
            touch.
            """);
        Succeeds(e, "atom_length(abc, N), N == 3.");
    }

    [Fact]
    public void AModuleFileReloadedReplacesWhatItGaveOtherModules()
    {
        var dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "rel.pl");
            File.WriteAllText(path, """
                :- module(rel, [touch/0]).
                user:greeting(hello).
                user:gone(1).
                touch.
                """);
            var e = new PrologEngine();
            e.ConsultFile(path);
            e.ConsultFile(path);
            Succeeds(e, "findall(X, greeting(X), L), L == [hello].");

            File.WriteAllText(path, """
                :- module(rel, [touch/0]).
                user:greeting(bonjour).
                touch.
                """);
            e.ConsultFile(path);
            Succeeds(e, "findall(X, greeting(X), L), L == [bonjour].");
            Succeeds(e, "\\+ catch(gone(_), _, fail).");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void AReplacedModuleKeepsWhatOtherSourcesGaveIt()
    {
        var dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "tgt.pl");
            File.WriteAllText(path, """
                :- module(tgt, [base/1]).
                base(1).
                """);
            var e = new PrologEngine();
            e.ConsultFile(path);
            e.ConsultString("tgt:extra(1).");
            Succeeds(e, "tgt:extra(1).");
            e.ConsultFile(path);
            Succeeds(e, "tgt:extra(1).");
            Succeeds(e, "tgt:base(1).");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void AReconsultedSourceWithdrawsItsClausesAndAnAppendingConsultAdds()
    {
        var e = new PrologEngine();
        e.ConsultString("other:bar(1).");
        e.ConsultString("other:bar(2).");
        Succeeds(e, "findall(X, other:bar(X), L), L == [1, 2].");
        e.ReconsultString("other:bar(3).");
        Succeeds(e, "findall(X, other:bar(X), L), L == [3].");
    }

    [Fact]
    public void AQualifiedHeadInAUserSourceNamingUserIsAnOrdinaryClause()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            user:ufact(5).
            user:urule(X) :- ufact(X).
            """);
        Succeeds(e, "urule(5).");
        Succeeds(e, "clause(user:urule(X), B), B == ufact(X).");
    }
}
