using System;
using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-038 (amendment): a dynamic predicate of an export-qualified
/// module belongs to that module. What a module asserts is its own; another
/// module or the global module reaches it only as <c>M:p(X)</c>, and its own
/// assert of the same name makes a second predicate. The predicate argument
/// of a database builtin is a meta-argument, so a term passed to a plain
/// predicate of the module is asserted there and one passed through a
/// <c>:- meta_predicate p(:)</c> stays the caller's. The legacy world, a
/// plain file, a <c>:- module(Name)</c> module and anything compiled under
/// <c>arity_compat</c>, keeps the global store. Each behaviour was measured
/// on the two reference systems for modules and on a third.</summary>
public sealed class ModuleDynamicsTests
{
    private const string ModA = """
        :- module(mdyn_a, [a_add/1, a_all/1, a_retract/1, a_retractall/0, a_abolish/0,
                           a_call/1, a_keep/1, a_take/1, a_before/1, a_rule/0,
                           a_meta/1, a_map/1, a_hook/0, a_cp/1, a_prop/2, a_clause/1]).
        :- meta_predicate a_take(:).
        a_add(X) :- assertz(b(X)).
        a_all(L) :- findall(X, b(X), L).
        a_retract(X) :- retract(b(X)).
        a_retractall :- retractall(b(_)).
        a_abolish :- abolish(b/1).
        a_call(X) :- G = assertz(i(X)), call(G).
        a_keep(T) :- assertz(T).
        a_take(T) :- assertz(T).
        a_before(L) :- catch(findall(X, j(X), L), error(E, _), L = err(E)).
        helper(from_a).
        a_rule :- assertz((r(X) :- helper(X))).
        a_meta(L) :- findall(X, (G = b(X), call(G)), L).
        a_map(L) :- maplist(assertz, L).
        a_hook :- assertz(file_search_path(mdyn_alias, '/from/a')).
        a_cp(L) :- findall(F/A, (current_predicate(F/A), F == b), L).
        a_prop(H, P) :- predicate_property(H, P).
        a_clause(L) :- findall(X-B, clause(b(X), B), L).
        """;

    private const string ModC = """
        :- module(mdyn_c, [c_all/1, c_add/1, c_via_a/1, c_retract/1]).
        c_all(L) :- catch(findall(X, b(X), L), error(E, _), L = err(E)).
        c_add(X) :- assertz(b(X)).
        c_via_a(L) :- findall(X, mdyn_a:b(X), L).
        c_retract(X) :- retract(b(X)).
        """;

    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    private static void Fails(PrologEngine e, string goal)
        => Assert.False(e.Query(goal).Success, goal);

    // `, fail` inside: a goal that succeeds must not pass for one that raised.
    private static void RaisesExistence(PrologEngine e, string goal, string indicator)
        => Succeeds(e, $"catch(({goal}, fail), error(existence_error(procedure, {indicator}), _), true).");

    private static PrologEngine Engine(bool tier1)
    {
        var e = new PrologEngine();
        if (tier1) e.IlPromotion.Threshold = 1;
        return e;
    }

    /// <summary>Runs the goal twice so Tier-1 promotes what it calls, then
    /// waits for the promotions and checks the module's predicate took IL
    /// when the tier is on: a Tier-0 fallback would test nothing new.</summary>
    private static void Warm(PrologEngine e, bool tier1, string goal, string mangledName, int arity)
    {
        Succeeds(e, goal);
        if (!tier1) return;
        Succeeds(e, goal);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        int fid = FunctorTable.Intern(AtomTable.Intern(mangledName).Id, arity);
        Assert.True(e.IlPromotion.IsPromoted(fid), $"{mangledName}/{arity} must promote to IL");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatAModuleAsserts_IsItsOwn(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        e.ConsultString(ModC);
        Succeeds(e, "a_add(1).");
        Warm(e, tier1, "a_all(L), L == [1].", "mdyn_a$a_all", 1);
        // The global module and another module see nothing of it bare.
        RaisesExistence(e, "b(_)", "b/1");
        RaisesExistence(e, "findall(X, b(X), _)", "b/1");
        Succeeds(e, "c_all(L), L == err(existence_error(procedure, b/1)).");
        // Qualified, both reach it.
        Succeeds(e, "findall(X, mdyn_a:b(X), L), L == [1].");
        Succeeds(e, "c_via_a(L), L == [1].");
        Succeeds(e, "G = mdyn_a:b(X), call(G), X == 1.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherModuleAssertingTheSameName_MakesASecondPredicate(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        e.ConsultString(ModC);
        Succeeds(e, "a_add(1), c_add(2).");
        Warm(e, tier1, "a_all(L), L == [1].", "mdyn_a$a_all", 1);
        Warm(e, tier1, "c_all(L), L == [2].", "mdyn_c$c_all", 1);
        Succeeds(e, "findall(X, mdyn_c:b(X), L), L == [2].");
        // retract, retractall and abolish in one leave the other's alone.
        Succeeds(e, "a_add(3), a_retract(1), a_all(L), L == [3], c_all(M), M == [2].");
        Succeeds(e, "a_retractall, a_all(L), L == [], c_all(M), M == [2].");
        Succeeds(e, "a_add(4), a_abolish, c_all(M), M == [2].");
        RaisesExistence(e, "a_all(_)", "b/1");
        Succeeds(e, "c_retract(2), c_all(M), M == [].");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATermPassedToAPlainPredicate_IsAssertedInTheCalleesModule(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        Warm(e, tier1, "a_keep(k(1)).", "mdyn_a$a_keep", 1);
        Succeeds(e, "findall(X, mdyn_a:k(X), L), L \\== [].");
        RaisesExistence(e, "k(_)", "k/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATermPassedThroughAMetaPredicate_StaysTheCallers(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        // `:- meta_predicate a_take(:)`: the global module is the caller.
        Warm(e, tier1, "a_take(t(1)).", "mdyn_a$a_take", 1);
        Succeeds(e, "findall(X, t(X), L), L \\== [].");
        RaisesExistence(e, "mdyn_a:t(_)", "t/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMetaCalledAssert_IsTheModulesOwn(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        Warm(e, tier1, "a_call(1).", "mdyn_a$a_call", 1);
        Succeeds(e, "findall(X, mdyn_a:i(X), L), L \\== [].");
        RaisesExistence(e, "i(_)", "i/1");
        // A closure naming the builtin carries the module too.
        Succeeds(e, "a_map([m(1), m(2)]), findall(X, mdyn_a:m(X), L), L == [1, 2].");
        RaisesExistence(e, "m(_)", "m/1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMetaCallInTheModule_ReachesItsOwnDynamic(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        Succeeds(e, "a_add(7).");
        Warm(e, tier1, "a_meta(L), L == [7].", "mdyn_a$a_meta", 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACallBeforeTheFirstAssert_IsAnExistenceError_AndWorksAfter(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        Warm(e, tier1, "a_before(L), L == err(existence_error(procedure, j/1)).", "mdyn_a$a_before", 1);
        Succeeds(e, "assertz(mdyn_a:j(5)).");
        Succeeds(e, "a_before(L), L == [5].");
        // Under unknown=fail the call fails instead, as an Arity bundle expects.
        Succeeds(e, "abolish(mdyn_a:j/1).");
        Succeeds(e, "set_prolog_flag(unknown, fail), a_before(L), L == [].");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARuleAssertedInTheModule_CallsItsPrivateHelper(bool tier1)
    {
        var e = Engine(tier1);
        e.ConsultString(ModA);
        e.ConsultString("helper(from_user).");
        Warm(e, tier1, "a_rule.", "mdyn_a$a_rule", 0);
        Succeeds(e, "mdyn_a:r(X), X == from_a.");
        // Asserted from outside with the module on the whole clause, the
        // body runs in the module; with the module on the head alone, it
        // runs where it was asserted from.
        Succeeds(e, "assertz(mdyn_a:(r2(Y) :- helper(Y))), mdyn_a:r2(X), X == from_a.");
        Succeeds(e, "assertz((mdyn_a:r3(Y) :- helper(Y))), mdyn_a:r3(X), X == from_user.");
    }

    [Fact]
    public void AQualifiedAssert_CreatesTheModule()
    {
        var e = new PrologEngine();
        Fails(e, "current_module(mdyn_z).");
        Succeeds(e, "assertz(mdyn_z:q(1)), asserta(mdyn_z:q(0)).");
        Succeeds(e, "current_module(mdyn_z).");
        Succeeds(e, "findall(X, mdyn_z:q(X), L), L == [0, 1].");
        RaisesExistence(e, "q(_)", "q/1");
        // The reflection of the module: what #214 asked.
        Succeeds(e, "findall(F/A, current_predicate(mdyn_z:F/A), L), L == [q/1].");
        Succeeds(e, "predicate_property(mdyn_z:q(_), dynamic).");
        Succeeds(e, "findall(X-B, clause(mdyn_z:q(X), B), L), L == [0-true, 1-true].");
        Succeeds(e, "retract(mdyn_z:q(0)), findall(X, mdyn_z:q(X), L), L == [1].");
        Succeeds(e, "retractall(mdyn_z:q(_)), findall(X, mdyn_z:q(X), L), L == [].");
        Succeeds(e, "assertz(mdyn_z:q(2)), abolish(mdyn_z:q/1).");
        RaisesExistence(e, "mdyn_z:q(_)", "q/1");
        Succeeds(e, "findall(F/A, current_predicate(mdyn_z:F/A), L), L == [].");
        // Two such modules keep their data apart; user: names the global module.
        Succeeds(e, "assertz(mdyn_z:d(1)), assertz(mdyn_w:d(2)), findall(X, mdyn_z:d(X), L), L == [1].");
        Succeeds(e, "assertz(user:g(1)), g(X), X == 1.");
    }

    [Fact]
    public void TheModuleReflectsItsDynamics()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Succeeds(e, "a_add(1).");
        Succeeds(e, "a_cp(L), L == [b/1].");
        Succeeds(e, "a_prop(b(_), dynamic).");
        Succeeds(e, "a_clause(L), L == [1-true].");
        Succeeds(e, "findall(F/A, current_predicate(mdyn_a:F/A), L), memberchk(b/1, L).");
        Succeeds(e, "predicate_property(mdyn_a:b(_), dynamic).");
        Succeeds(e, "clause(mdyn_a:b(X), true), X == 1.");
    }

    [Fact]
    public void AnExportedDynamic_IsOnePredicateSharedWithItsImporters()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_mdyn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mdyn_m.pl"), """
                :- module(mdyn_m, [e/1, m_all/1, m_take/1]).
                :- meta_predicate m_take(:).
                :- dynamic e/1.
                e(1).
                m_all(L) :- findall(X, e(X), L).
                m_take(T) :- assertz(T).
                """);
            File.WriteAllText(Path.Combine(dir, "mdyn_imp.pl"), """
                :- module(mdyn_imp, [imp_add/1, imp_all/1, imp_give/1, imp_own/1]).
                :- use_module('mdyn_m.pl').
                imp_add(X) :- assertz(e(X)).
                imp_all(L) :- findall(X, e(X), L).
                imp_give(X) :- m_take(own(X)).
                imp_own(L) :- findall(X, own(X), L).
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "mdyn_imp.pl"));
            Succeeds(e, "imp_all(L), L == [1].");
            Succeeds(e, "imp_add(2), imp_all(L), L == [1, 2].");
            // The dependency's exports are not the global module's: it is
            // reached qualified from here.
            Succeeds(e, "mdyn_m:m_all(M), M == [1, 2].");
            Succeeds(e, "findall(X, mdyn_m:e(X), L), L == [1, 2].");
            // A term passed through a meta-predicate from a module is that
            // module's.
            Succeeds(e, "imp_give(9), imp_own(L), L == [9].");
            RaisesExistence(e, "mdyn_m:own(_)", "own/1");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AStaticOfTheModule_BesideAGlobalDynamicOfTheSameName()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mdyn_n, [n_f/1]).
            f(mine).
            n_f(L) :- findall(X, f(X), L).
            """);
        e.ConsultString("""
            :- dynamic f/1.
            f(global).
            """);
        Succeeds(e, "n_f(L), L == [mine].");
        Succeeds(e, "findall(X, f(X), L), L == [global].");
        Succeeds(e, "findall(X, mdyn_n:f(X), L), L == [mine].");
    }

    [Fact]
    public void TheGlobalModulesDynamic_IsReachedAsUserFromAModule()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic dy/1.
            dy(2).
            """);
        e.ConsultString("""
            :- module(mdyn_v, [v_bare/1, v_user/1, v_add/1, v_retract/1]).
            v_bare(L) :- catch(findall(X, dy(X), L), error(E, _), L = err(E)).
            v_user(L) :- findall(X, user:dy(X), L).
            v_add(X) :- assertz(user:dy(X)).
            v_retract(X) :- retract(dy(X)).
            """);
        Succeeds(e, "v_bare(L), L == err(existence_error(procedure, dy/1)).");
        Succeeds(e, "v_user(L), L == [2].");
        Succeeds(e, "v_add(3), findall(X, dy(X), L), L == [2, 3].");
        // retract of the bare name is the module's own: the global one stays.
        Fails(e, "v_retract(2).");
        Succeeds(e, "findall(X, dy(X), L), L == [2, 3].");
    }

    [Fact]
    public void TheEnginesHookFacts_AreTheGlobalModules()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Succeeds(e, "a_hook.");
        Fails(e, "file_search_path(mdyn_alias, _).");
        Succeeds(e, "assertz(user:file_search_path(mdyn_alias2, '/y')), file_search_path(mdyn_alias2, P), P == '/y'.");
    }

    [Fact]
    public void ThePrivatesName_CanBecomeTheGlobalModulesOwnDynamic()
    {
        // ADR-056 point 3, withdrawn: a global `helper/1` cannot take over
        // the module's private, so the assert defines the global one.
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Succeeds(e, "assertz(helper(9)), helper(X), X == 9.");
        Succeeds(e, "a_rule, mdyn_a:r(X), X == from_a.");
    }

    [Fact]
    public void AFileLoadedIntoAModule_IsThatModules()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_mdyn_into_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string plain = Path.Combine(dir, "plain.pl").Replace('\\', '/');
            File.WriteAllText(plain, """
                pp(1).
                :- dynamic dd/1.
                dd(2).
                """);
            File.WriteAllText(Path.Combine(dir, "mdyn_mm.pl"), $"""
                :- module(mdyn_mm, [go/0, mm_pp/1]).
                go :- consult('{plain}').
                mm_pp(L) :- findall(X, pp(X), L).
                """);
            var e = new PrologEngine();
            // M:reconsult(File) and consult(M:File) load a file without a
            // module directive into M, created when nothing defined it; its
            // predicates are M's, dynamics included.
            Succeeds(e, $"mdyn_nd:reconsult('{plain}').");
            Succeeds(e, "mdyn_nd:pp(X), X == 1.");
            Succeeds(e, "mdyn_nd:dd(X), X == 2.");
            Succeeds(e, "assertz(mdyn_nd:dd(3)), findall(X, mdyn_nd:dd(X), L), L == [2, 3].");
            Succeeds(e, "findall(F/A, current_predicate(mdyn_nd:F/A), L), msort(L, S), S == [dd/1, pp/1].");
            RaisesExistence(e, "pp(_)", "pp/1");
            RaisesExistence(e, "dd(_)", "dd/1");
            // Loaded again, the file replaces what it defines there.
            Succeeds(e, $"mdyn_nd:reconsult('{plain}'), findall(X, mdyn_nd:pp(X), L), L == [1].");
            Succeeds(e, "findall(X, mdyn_nd:dd(X), L), L == [2].");
            Succeeds(e, $"consult(mdyn_nd2:'{plain}'), mdyn_nd2:pp(X), X == 1, current_module(mdyn_nd2).");
            // consult/1 called from a module loads the file into that module
            // (SICStus, SWI).
            e.ConsultFile(Path.Combine(dir, "mdyn_mm.pl"));
            Succeeds(e, "go, mm_pp(L), L == [1].");
            Succeeds(e, "mdyn_mm:pp(X), X == 1.");
            RaisesExistence(e, "pp(_)", "pp/1");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The base case: two modules each declaring <c>p/1</c>
    /// <c>:- multifile</c> and <c>:- dynamic</c> with a fact of their own
    /// hold two predicates, and the global module holds none (SICStus, SWI,
    /// Scryer). The declaration names the module's own predicate, so it
    /// leaves no empty <c>p/1</c> behind in the global module, nor does a
    /// <c>:- discontiguous</c> one.</summary>
    [Fact]
    public void TwoModulesDeclaringTheSameMultifile_HoldTwoPredicates()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mf2_m, []).
            :- multifile(p/1).
            :- dynamic(p/1).
            :- discontiguous(d/1).
            p(m_static).
            d(1).
            """);
        e.ConsultString("""
            :- module(mf2_n, []).
            :- multifile(p/1).
            :- dynamic(p/1).
            p(n_static).
            """);
        Succeeds(e, "findall(X, mf2_m:p(X), L), L == [m_static].");
        Succeeds(e, "findall(X, mf2_n:p(X), L), L == [n_static].");
        Succeeds(e, "mf2_m:d(X), X == 1.");
        RaisesExistence(e, "p(_)", "p/1");
        RaisesExistence(e, "d(_)", "d/1");
        Fails(e, "current_predicate(p/1).");
        Fails(e, "predicate_property(p(_), multifile).");
        Succeeds(e, "predicate_property(mf2_m:p(_), multifile).");
        Succeeds(e, "predicate_property(mf2_m:p(_), dynamic).");
        Succeeds(e, "current_predicate(mf2_m:p/1), current_predicate(mf2_n:p/1).");
    }

    /// <summary>What the declaration changes: with it, a second source adds
    /// its clauses to the module's predicate and a reload of that source
    /// replaces only what it gave (SICStus, SWI, and so here); without it, a
    /// second source's clauses for a static predicate redefine it, with a
    /// warning (ADR-055 point 6), while for a dynamic one they join it as an
    /// assert would, and a reload still withdraws that source's own.</summary>
    [Fact]
    public void WithoutMultifile_ASecondSourceRedefinesAStatic_AndFeedsADynamicOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_mdyn_mf3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string P(string name) => Path.Combine(dir, name).Replace('\\', '/');
            File.WriteAllText(P("mf3_s.pl"), """
                :- module(mf3_s, []).
                p(m_static).
                """);
            File.WriteAllText(P("mf3_d.pl"), """
                :- module(mf3_d, []).
                :- dynamic(p/1).
                p(m_static).
                """);
            File.WriteAllText(P("mf3_w.pl"), """
                :- module(mf3_w, []).
                :- multifile(p/1).
                p(m_static).
                """);
            File.WriteAllText(P("mf3_gs.pl"), "mf3_s:p(g_static).\n");
            File.WriteAllText(P("mf3_gd.pl"), "mf3_d:p(g_static).\n");
            File.WriteAllText(P("mf3_gw.pl"), ":- multifile(mf3_w:p/1).\nmf3_w:p(g_static).\n");
            var e = new PrologEngine();
            foreach (string f in new[] { "mf3_s.pl", "mf3_d.pl", "mf3_w.pl" }) e.ConsultFile(P(f));
            // Each load is a query of its own: a static change a consult makes
            // inside a query is seen by the next one.
            // Static, undeclared: the later source redefines.
            Succeeds(e, $"consult('{P("mf3_gs.pl")}').");
            Succeeds(e, "findall(X, mf3_s:p(X), L), L == [g_static].");
            Succeeds(e, $"reconsult('{P("mf3_gs.pl")}').");
            Succeeds(e, "findall(X, mf3_s:p(X), L), L == [g_static].");
            // Dynamic, undeclared: the clause joins; a reload gives it once.
            Succeeds(e, $"consult('{P("mf3_gd.pl")}').");
            Succeeds(e, "findall(X, mf3_d:p(X), L), L == [m_static, g_static].");
            Succeeds(e, $"reconsult('{P("mf3_gd.pl")}').");
            Succeeds(e, "findall(X, mf3_d:p(X), L), L == [m_static, g_static].");
            // Multifile: the same, declared.
            Succeeds(e, $"consult('{P("mf3_gw.pl")}').");
            Succeeds(e, "findall(X, mf3_w:p(X), L), L == [m_static, g_static].");
            Succeeds(e, $"reconsult('{P("mf3_gw.pl")}').");
            Succeeds(e, "findall(X, mf3_w:p(X), L), L == [m_static, g_static].");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A multifile dynamic's clauses belong to the module that
    /// names them (SICStus, SWI): the module's own text, a qualified head
    /// in another file, a qualified assert from another module, and a bare
    /// assert from a module that imports it. Another module's own
    /// <c>:- multifile p/1</c> is a different predicate, and a plain file's
    /// <c>p/1</c> is the global one.</summary>
    [Fact]
    public void AMultifileDynamic_BelongsToTheModuleThatNamesIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_mdyn_mf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string P(string name) => Path.Combine(dir, name).Replace('\\', '/');
            File.WriteAllText(P("mf_m.pl"), """
                :- module(mf_m, [p/1, add_here/1]).
                :- multifile(p/1).
                :- dynamic(p/1).
                p(m_static).
                add_here(X) :- assertz(p(X)).
                """);
            File.WriteAllText(P("mf_n.pl"), """
                :- module(mf_n, [add_n/1, add_m/1, n_sees/1]).
                :- multifile(p/1).
                :- dynamic(p/1).
                p(n_static).
                add_n(X) :- assertz(p(X)).
                add_m(X) :- assertz(mf_m:p(X)).
                n_sees(L) :- findall(X, p(X), L).
                """);
            File.WriteAllText(P("mf_i.pl"), $"""
                :- module(mf_i, [add_i/1, i_sees/1]).
                :- use_module('{P("mf_m.pl")}').
                add_i(X) :- assertz(p(X)).
                i_sees(L) :- findall(X, p(X), L).
                """);
            File.WriteAllText(P("mf_g.pl"), """
                :- multifile(mf_m:p/1).
                mf_m:p(g_static).
                """);
            // Loaded without importing anything into user, which has its own p/1.
            File.WriteAllText(P("mf_drv.pl"), $"""
                :- use_module('{P("mf_m.pl")}', []).
                :- use_module('{P("mf_n.pl")}', []).
                :- use_module('{P("mf_i.pl")}', []).
                :- dynamic(p/1).
                p(f_static).
                """);
            var e = new PrologEngine();
            e.ConsultFile(P("mf_drv.pl"));
            Succeeds(e, $"consult('{P("mf_g.pl")}').");
            Succeeds(e, "mf_m:add_here(m_added), mf_n:add_n(n_added), mf_n:add_m(via_n), mf_i:add_i(i_added), assertz(p(user_added)).");
            Succeeds(e, "findall(X, mf_m:p(X), L), L == [m_static, g_static, m_added, via_n, i_added].");
            Succeeds(e, "mf_n:n_sees(L), L == [n_static, n_added].");
            Succeeds(e, "findall(X, mf_n:p(X), L), L == [n_static, n_added].");
            Succeeds(e, "mf_i:i_sees(L), L == [m_static, g_static, m_added, via_n, i_added].");
            Succeeds(e, "findall(X, p(X), L), L == [f_static, user_added].");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheLegacyWorldIsUnchanged()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mdyn_leg).
            :- public leg_add/1.
            leg_add(X) :- assertz(lb(X)).
            """);
        e.ConsultString("plain_add(X) :- assertz(pb(X)).");
        Succeeds(e, "leg_add(1), lb(X), X == 1.");
        Succeeds(e, "plain_add(1), pb(X), X == 1.");
        // An export-qualified module compiled under arity_compat is legacy too.
        Succeeds(e, "set_prolog_flag(arity_compat, true).");
        e.ConsultString("""
            :- module(mdyn_ar, [ar_add/1, ar_all/1]).
            ar_add(X) :- assertz(ab(X)).
            ar_all(L) :- findall(X, ab(X), L).
            """);
        Succeeds(e, "set_prolog_flag(arity_compat, false).");
        Succeeds(e, "ar_add(1), ab(X), X == 1.");
        Succeeds(e, "assertz(ab(2)), ar_all(L), L == [1, 2].");
    }
}
