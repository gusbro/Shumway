using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate a <c>:- module</c> file does not export is private:
/// only the module's own clauses call it by its bare name, and anyone else
/// reaches it only by qualifying, <c>M:Goal</c>. That holds for the top
/// level, for a clause of another module and for a clause of a file without
/// a module directive, whether the module was consulted directly or loaded
/// by use_module. The linker already refused such a call; the interactive
/// engine used to resolve it.</summary>
public sealed class ModulePrivatePredicateTests
{
    private const string ModA = """
        :- module(mpp_a, [a_probe/1]).
        a_local(1).
        a_local(2).
        shared_here(a).
        a_probe(L) :- findall(X, a_local(X), L).
        """;

    private const string ModB = """
        :- module(mpp_b, []).
        shared_here(b).
        """;

    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    // `, fail` inside: a goal that succeeds must not pass for one that raised.
    private static void RaisesExistence(PrologEngine e, string goal, string indicator)
        => Succeeds(e, $"catch(({goal}, fail), error(existence_error(procedure, {indicator}), _), true).");

    [Fact]
    public void ThePrivateIsNotCallableBareFromTheTopLevel()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        RaisesExistence(e, "a_local(_)", "a_local/1");
        RaisesExistence(e, "call(a_local(2))", "a_local/1");
        RaisesExistence(e, "findall(X, a_local(X), _)", "a_local/1");
    }

    [Fact]
    public void QualifyingReachesIt()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Succeeds(e, "findall(X, mpp_a:a_local(X), L), L == [1, 2].");
        Succeeds(e, "G = mpp_a:a_local(2), call(G).");
        // The module's own clauses call it bare, as always.
        Succeeds(e, "a_probe(L), L == [1, 2].");
    }

    [Fact]
    public void AnotherModulesClauseDoesNotReachIt()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString("""
            :- module(mpp_caller, [calls_it/1]).
            calls_it(X) :- a_local(X).
            """);
        RaisesExistence(e, "calls_it(_)", "a_local/1");
    }

    [Fact]
    public void AClauseOfAFileWithoutModuleDirectiveDoesNotReachIt()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString("user_calls_it(X) :- a_local(X).");
        RaisesExistence(e, "user_calls_it(_)", "a_local/1");
    }

    [Fact]
    public void TwoModulesWithTheSamePrivate_EachKeepsItsOwn()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        RaisesExistence(e, "shared_here(_)", "shared_here/1");
        Succeeds(e, "mpp_a:shared_here(W), W == a.");
        Succeeds(e, "mpp_b:shared_here(W), W == b.");
    }

    [Fact]
    public void AUseModuleDependencysPrivateStaysPrivateToo()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway_mpp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mpp_dep.pl"), """
                :- module(mpp_dep, [dep_export/0]).
                dep_export.
                dep_local.
                """);
            File.WriteAllText(Path.Combine(dir, "mpp_top.pl"), """
                :- module(mpp_top, [top_export/0]).
                :- use_module('mpp_dep.pl').
                top_export :- dep_export, top_local.
                top_local.
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "mpp_top.pl"));
            Succeeds(e, "top_export.");
            RaisesExistence(e, "top_local", "top_local/0");
            RaisesExistence(e, "dep_local", "dep_local/0");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AGlobalDefinitionOfTheSameNameIsUnaffected()
    {
        var e = new PrologEngine();
        e.ConsultString("shared_here(global).");
        e.ConsultString(ModA);
        Succeeds(e, "findall(W, shared_here(W), L), L == [global].");
        Succeeds(e, "mpp_a:shared_here(W), W == a.");
    }

    [Fact]
    public void DynamicsDeclaredInAModuleStayGlobal()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_f, []).
            :- dynamic(f_d/1).
            f_s(ok).
            """);
        // ADR-008: a dynamic predicate is global, so it is callable bare and
        // an empty one fails rather than raising.
        Assert.False(e.Query("f_d(_).").Success);
        Succeeds(e, "assertz(f_d(9)), f_d(9).");
        RaisesExistence(e, "f_s(ok)", "f_s/1");
    }

    [Fact]
    public void ThePrivatesNameCannotBecomeAGlobalDynamic()
    {
        // Dynamics are global, so a bare dynamic a_local/1 would take the
        // name from the module's own clauses. assertz refuses it as it
        // refuses any static procedure.
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Succeeds(e,
            "catch((assertz(a_local(99)), fail), error(permission_error(modify, static_procedure, _), _), true).");
        Succeeds(e, "a_probe(L), L == [1, 2].");
    }

    [Fact]
    public void AFileWithoutModuleDirectiveDefinesUserPredicates()
    {
        var e = new PrologEngine();
        e.ConsultString("plain_pred(yes).");
        Succeeds(e, "plain_pred(yes).");
        RaisesExistence(e, "never_defined", "never_defined/0");
    }

    // A goal of findall/bagof/setof/\+ with a cut of its own keeps the cut
    // local to it; it must still be the module's goal, calling the privates.
    private const string LocalCuts = """
        :- module(mpp_cut, [lc/2]).
        p(1). p(2).
        lc(findall, L) :- findall(X, (p(X), !), L).
        lc(findall_guard, L) :- findall(X, (p(X), X > 0, !), L).
        lc(bagof, L) :- bagof(X, (p(X), !), L).
        lc(setof, L) :- setof(X, (p(X) ; p(X), !), L).
        lc(not, yes) :- \+ (p(X), X > 5, !).
        lc(not_cut_fail, yes) :- \+ (!, fail).
        lc(after, L) :- findall(X, (p(X), !), L).
        lc(after, more).
        """;

    [Theory]
    [InlineData("findall", "[1]")]
    [InlineData("findall_guard", "[1]")]
    [InlineData("bagof", "[1]")]
    [InlineData("setof", "[1, 2]")]
    [InlineData("not", "yes")]
    [InlineData("not_cut_fail", "yes")]
    public void ALocalCutInAnAllSolutionsGoalStillCallsThePrivate(string which, string expected)
    {
        var e = new PrologEngine();
        e.ConsultString(LocalCuts);
        Succeeds(e, $"lc({which}, L), L == {expected}.");
    }

    [Fact]
    public void TheLocalCutDoesNotCutTheClause()
    {
        var e = new PrologEngine();
        e.ConsultString(LocalCuts);
        Succeeds(e, "findall(L, lc(after, L), Ls), Ls == [[1], more].");
    }

    // ---- a goal built at run time, and the closures a module hands out ----

    [Fact]
    public void AGoalBuiltAtRunTimeDoesNotReachThePrivate()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString("""
            :- module(mpp_rt_caller, [calls_it_later/1]).
            calls_it_later(X) :- G = a_local(X), call(G).
            """);
        RaisesExistence(e, "G = a_local(_), call(G)", "a_local/1");
        RaisesExistence(e, "calls_it_later(_)", "a_local/1");
    }

    private const string Closures = """
        :- module(mpp_cl, [cl_map/0, cl_include/1, cl_fold/1, cl_sort/1,
                           cl_later_map/0, cl_later_include/1, cl_apply/1,
                           cl_apply_later/1, cl_apply_there/1]).
        ok(X) :- integer(X).
        add(X, A0, A) :- A is A0 + X.
        rev(O, A, B) :- compare(O0, A, B), flip(O0, O).
        flip(<, >). flip(>, <). flip(=, =).
        cl_map :- maplist(ok, [1, 2]).
        cl_include(L) :- include(ok, [a, 1, b, 2], L).
        cl_fold(S) :- foldl(add, [1, 2, 3], 0, S).
        cl_sort(L) :- predsort(rev, [2, 3, 1], L).
        cl_later_map :- G = maplist(ok, [1, 2]), call(G).
        cl_later_include(L) :- G = include(ok, [a, 1], L), G.
        cl_apply(X) :- apply_to(ok, X).
        cl_apply_later(X) :- G = apply_to(ok, X), call(G).
        cl_apply_there(X) :- mpp_hof:apply_to(ok, X).
        """;

    private const string HigherOrder = """
        :- module(mpp_hof).
        :- public(apply_to/2).
        :- meta_predicate(apply_to(1, ?)).
        apply_to(G, X) :- call(G, X).
        """;

    [Fact]
    public void AModulePassesItsPrivatesToHigherOrderPredicates()
    {
        // The closure is called from maplist's clauses, not the module's:
        // it reaches the private because the module qualifies it.
        var e = new PrologEngine();
        e.ConsultString(HigherOrder);
        e.ConsultString(Closures);
        Succeeds(e, "mpp_cl:cl_map.");
        Succeeds(e, "mpp_cl:cl_include(L), L == [1, 2].");
        Succeeds(e, "mpp_cl:cl_fold(S), S == 6.");
        Succeeds(e, "mpp_cl:cl_sort(L), L == [3, 2, 1].");
        Succeeds(e, "mpp_cl:cl_apply(3).");
    }

    [Fact]
    public void AGoalBuiltAtRunTimeInsideTheModuleKeepsItsModule()
    {
        var e = new PrologEngine();
        e.ConsultString(HigherOrder);
        e.ConsultString(Closures);
        Succeeds(e, "mpp_cl:cl_later_map.");
        Succeeds(e, "mpp_cl:cl_later_include(L), L == [1].");
        Succeeds(e, "mpp_cl:cl_apply_later(3).");
    }

    [Fact]
    public void AGoalQualifiedWithAnotherModuleTakesThatModulesContext()
    {
        // M:Goal makes M the context of Goal's meta-arguments as well, so
        // the caller's private closure is looked up in M (Scryer agrees).
        var e = new PrologEngine();
        e.ConsultString(HigherOrder);
        e.ConsultString(Closures);
        RaisesExistence(e, "mpp_cl:cl_apply_there(3)", "ok/1");
    }

    [Fact]
    public void AFrozenGoalRunsInTheModuleThatFroze()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_fz, [fz_go/1, fz_goal/1]).
            :- use_module(library(coroutining)).
            loc(1).
            fz_go(X) :- freeze(X, loc(X)), X = 1.
            fz_goal(G) :- freeze(Y, loc(Y)), frozen(Y, G).
            """);
        Succeeds(e, "fz_go(X), X == 1.");
        Succeeds(e, "fz_goal(G), G = coroutining:freeze(V, mpp_fz:loc(V)).");
    }

    [Fact]
    public void AHooksGoalsRunInTheAttributesModule()
    {
        // verify_attributes/3 returns goals that name the module's own
        // privates, as clp(Z)'s do_queue//0.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_hk, [watch/1]).
            :- dynamic(hk_seen/1).
            watch(X) :- put_attr(X, mpp_hk, on).
            verify_attributes(_, Value, [note(Value)]).
            note(V) :- assertz(hk_seen(V)).
            """);
        Succeeds(e, "watch(X), X = 5, hk_seen(5).");
    }

    [Fact]
    public void APhraseBodyQualifiedWithItsModuleRunsThere()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_g, []).
            greeting --> [hi], who.
            who --> [bob].
            """);
        Succeeds(e, "phrase(mpp_g:greeting, [hi, bob]).");
        Succeeds(e, "phrase(mpp_g:(greeting, [x]), [hi, bob, x]).");
        RaisesExistence(e, "phrase(greeting, [hi, bob])", "greeting/2");
        // M:phrase(Body, L): M is the body's context as well.
        Succeeds(e, "mpp_g:phrase((greeting, [x]), [hi, bob, x]).");
    }

    [Fact]
    public void AGrammarBodyAModulePassesToPhraseRunsThere()
    {
        // A control-construct body stays a runtime phrase call; its
        // nonterminals are the module's privates.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_pg, [pg_parse/1]).
            pg_parse(L) :- phrase((greet, who), L).
            greet --> [hi].
            who --> [bob].
            """);
        Succeeds(e, "pg_parse([hi, bob]).");
    }

    [Fact]
    public void AProgramsOwnPredicateNamedLikeALibraryOneGetsItsArgumentsAsWritten()
    {
        // The prelude's partition/4 takes a closure; a program's own
        // partition/4 (Van Roy's qsort) takes a list, and must receive it.
        var e = new PrologEngine();
        e.ConsultString("""
            partition([], _, [], []).
            partition([X|L], Y, [X|L1], L2) :- X =< Y, !, partition(L, Y, L1, L2).
            partition([X|L], Y, L1, [X|L2]) :- partition(L, Y, L1, L2).
            split(S, B) :- partition([3, 1, 2], 2, S, B).
            """);
        Succeeds(e, "split(S, B), S == [1, 2], B == [3].");
        Succeeds(e, "G = partition([5, 0], 1, S, B), call(G), S == [0], B == [5].");
        e.ConsultString("""
            :- module(mpp_own, [own_run/1]).
            maplist(_, x).
            own_run(R) :- maplist(whatever, R).
            """);
        Succeeds(e, "own_run(R), R == x.");
    }

    [Fact]
    public void AModulesOwnMetaPredicateDeclarationQualifiesItsCalls()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mpp_tw, [tw_run/0]).
            :- meta_predicate(twice(0)).
            twice(G) :- G, G.
            loc.
            tw_run :- twice(loc).
            """);
        Succeeds(e, "tw_run.");
    }

    [Fact]
    public void UsersPredicatesStayReachableFromLibraryMetaCalls()
    {
        // user is the global module: a goal naming one of its predicates
        // resolves from the library code that runs it.
        var e = new PrologEngine();
        e.ConsultString("""
            u_item(1). u_item(2).
            u_ok(X) :- integer(X).
            """);
        Succeeds(e, "time(u_item(1)).");
        Succeeds(e, "maplist(u_ok, [1, 2]).");
        Succeeds(e, "G = maplist(u_ok, [1]), call(G).");
        Succeeds(e, "findall(X, u_item(X), L), L == [1, 2].");
    }

    /// <summary>A qualified meta-argument keeps bagof/setof's ^: the
    /// caller's <c>Y^Goal</c> reaches them as <c>M:(Y^Goal)</c>, and Y must
    /// still be existentially quantified, not called as a predicate.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(":- module(mpp_bag, [bag/1, set/1]).\n")]
    public void QualifiedBagofGoalsKeepTheirCaret(string header)
    {
        var e = new PrologEngine();
        e.ConsultString(header + """
            pair(b, 2). pair(a, 1). pair(a, 3).
            bag(L) :- bagof(X, Y^pair(X, Y), L).
            set(L) :- setof(X, Y^pair(X, Y), L).
            """);
        Succeeds(e, "bag(L), L == [b, a, a].");
        Succeeds(e, "set(L), L == [a, b].");
        // Qualified at run time: the goal's module reaches its ^ argument.
        string m = header.Length == 0 ? "user" : "mpp_bag";
        Succeeds(e, $"call({m}:bagof(X, Y^pair(X, Y), L)), L == [b, a, a].");
        Succeeds(e, $"findall(L, {m}:setof(X, Y^pair(X, Y), L), R), R == [[a, b]].");
    }
}
