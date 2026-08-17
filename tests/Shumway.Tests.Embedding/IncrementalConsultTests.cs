using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Incremental consult: a directive takes effect for every clause that follows it
/// in the same source, exactly as `:- op` already does. The clause stream is
/// consumed lazily so a `:- use_module` / `:- set_prolog_flag` executed mid-file
/// affects the parsing of subsequent clauses — the correct behaviour of any Prolog
/// engine, and what lets a source import a library and then use its operators.
/// </summary>
public class IncrementalConsultTests
{
    [Fact]
    public void UseModuleOperators_ApplyToLaterClausesInTheSameFile()
    {
        // `:- use_module(library(clpfd))` brings the `in`/`#>`/`..` operators; the
        // clause after it must parse with them. Before incremental consult the
        // whole file was parsed before the directive ran, so `X in 1..5` was a
        // syntax error.
        var e = new PrologEngine();
        e.ConsultString("""
            :- use_module(library(clpfd)).
            solve(Xs) :- [X,Y] = Xs, X in 1..3, Y in 1..3, X #> Y, label([X,Y]).
            """);
        Assert.True(e.Query("solve([X, Y]).").Success);
    }

    [Fact]
    public void IncludePredicateCall_DoesNotForceTheEagerPath()
    {
        // A source that CALLS include/3 (the list predicate — clpz does, 6×) must
        // still get incremental consult: only a real `:- include(File)` DIRECTIVE
        // forces the eager path. Here the use_module operators must reach the later
        // clause even though the file also mentions include(.
        var e = new PrologEngine();
        e.ConsultString("""
            :- use_module(library(clpfd)).
            evens(Xs, Es) :- include([X]>>(0 is X mod 2), Xs, Es).
            solve(X) :- X in 1..3, X #> 2, label([X]).
            """);
        Assert.True(e.Query("solve(3).").Success);
    }

    [Fact]
    public void SetPrologFlag_AppliesToLaterClausesInTheSameFile()
    {
        // double_quotes set mid-file changes how a later clause's "..." reads.
        var e = new PrologEngine();
        e.ConsultString("""
            before(X) :- X = "ab".
            :- set_prolog_flag(double_quotes, chars).
            after(X) :- X = "ab".
            """);
        // `after` reads "ab" as a char list under the flag set just before it.
        Assert.Equal("[a,b]", RenderList(e.Query("after(X).")["X"]!));
    }

    [Fact]
    public void InFileTermExpansion_AppliesToLaterClausesInTheSameFile()
    {
        // A term_expansion defined in a file must expand that file's OWN later
        // clauses — the in-file case (clpz's `++>` grammar depends on it).
        var e = new PrologEngine();
        e.ConsultString("""
            term_expansion(macro(X), expanded(X)).
            macro(hi).
            """);
        Assert.True(e.Query("expanded(hi).").Success);
    }

    [Fact]
    public void InFileTermExpansion_IsOrderSensitive_LikeSwiAndScryer()
    {
        // Verified identical on SWI 9.2.3 and Scryer: a hook applies ONLY to
        // clauses AFTER its definition. A matching term before the definition
        // survives unexpanded.
        var e = new PrologEngine();
        e.ConsultString("""
            special(before).
            term_expansion(special(X), handled(X)).
            special(after).
            """);
        Assert.True(e.Query("special(before).").Success);   // before def: not expanded
        Assert.False(e.Query("handled(before).").Success);  // never created
        Assert.False(e.Query("special(after).").Success);   // after def: expanded away
        Assert.True(e.Query("handled(after).").Success);     // its expansion
    }

    [Fact]
    public void InFileTermExpansion6_DualAccumulatorGrammar()
    {
        // clpz's shape: a term_expansion/6 whose body calls file-local helpers to
        // translate a custom `++>` grammar operator, defined and used in one file.
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(1200, xfx, (++>)).
            user:term_expansion((H ++> B), _, Ids, (H :- Body), [], [d|Ids]) :- \+ member(d, Ids), mk(B, Body).
            mk(G, call(G)).
            greet ++> hello.
            hello.
            """);
        Assert.True(e.Query("greet.").Success);
    }

    [Fact]
    public void MultifileTermExpansionHook_BodyCallsAModuleLocalInsideOnce()
    {
        // clpz's exact shape: a `:- multifile user:term_expansion/6` whose body
        // calls a module-local helper inside once/1. A global hook declared
        // multifile must stay on the static pipeline (where MetaTransform lowers
        // once so the local mangles) — NOT be routed to the dynamic store, whose
        // pre-mangle leaves the once-nested call bare.
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(1200, xfx, (++>)).
            :- multifile user:term_expansion/6.
            user:term_expansion((H ++> B), _, Ids, (H :- Body), [], [d|Ids]) :- \+ member(d, Ids), once(mk(B, Body)).
            mk(G, call(G)).
            greet ++> hello.
            hello.
            """);
        Assert.True(e.Query("greet.").Success);
    }

    [Fact]
    public void TermExpansionHook_WithPhraseDcgBody_UsableByALaterConsult()
    {
        // atts.pl's shape: a hook that builds its output via phrase of a local DCG.
        // Its own (top-level) re-expansion pass must not corrupt it.
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(1150, fx, decl).
            user:term_expansion((:- decl X), Clauses) :- phrase(gen(X), Clauses).
            gen(X) --> [marked(X)].
            """);
        e.ConsultString(":- decl hi.");
        Assert.True(e.Query("marked(hi).").Success);
    }

    [Fact]
    public void UseModuleHook_AppliesToLaterClausesInTheSameConsult()
    {
        // clpz's shape: a file `:- use_module`s a library that defines a
        // term_expansion hook, then uses that hook LATER in the same file. The
        // hook must be active for those later clauses — hasTermExp is re-checked
        // as the loop advances, not fixed at the start (before the use_module ran).
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-samefile-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "declmac.pl"),
                ":- module(declmac, []).\n" +
                ":- op(1150, fx, decl).\n" +
                "user:term_expansion((:- decl X), marked(X)).");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            // use_module AND the hook's use in ONE consult.
            e.ConsultString("""
                :- use_module(library(declmac)).
                :- decl hi.
                """);
            Assert.True(e.Query("marked(hi).").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void NestedLibraryWithHooks_DoesNotCorruptTheOuterConsult()
    {
        // The atts.pl regression: a library that both use_modules another library
        // AND defines a term_expansion hook. The nested dep's re-expansion runs a
        // sub-query mid-consult; it must not break the hook for a later consult.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-nested-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "helperlib.pl"),
                ":- module(helperlib, [help/1]).\nhelp(ok).");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "declib.pl"),
                ":- module(declib, []).\n" +
                ":- use_module(library(helperlib)).\n" +
                ":- op(1150, fx, decl).\n" +
                "user:term_expansion((:- decl X), Clauses) :- help(_), phrase(gen(X), Clauses).\n" +
                "gen(X) --> [marked(X)].");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            e.ConsultString(":- use_module(library(declib)).");   // loads helperlib nested
            e.ConsultString(":- decl hi.");
            Assert.True(e.Query("marked(hi).").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void GoalExpansion_AppliesToOwnClausesInAnExportQualifiedNestedLibrary()
    {
        // clpz's shape: an export-qualified `:- module(m, [...])` library, loaded
        // via use_module, defines its OWN goal_expansion/2 macro and uses it in
        // its own clause bodies (clpz's `cis_leq`). The hook must be applied to
        // the library's clauses even though it is a nested consult — the
        // re-expansion pass runs for nested libraries, not only top level.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-gexp-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "gexpm.pl"),
                ":- module(gexpm, [test/1]).\n" +
                ":- op(700, xfx, myleq).\n" +
                "goal_expansion(A myleq B, leq(A, B)).\n" +
                "leq(X, Y) :- X =< Y.\n" +
                "test(R) :- ( 1 myleq 2 -> R = yes ; R = no ).");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            e.ConsultString(":- use_module(library(gexpm)).");
            Assert.True(e.Query("test(yes).").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void GoalExpansionClauses_NeedNotBeContiguous()
    {
        // goal_expansion/2 (and term_expansion) are hooks — implicitly
        // discontiguous + multifile; a library (format.pl, clpz.pl) scatters
        // their clauses among helpers. The contiguity check must not reject that.
        var e = new PrologEngine();
        e.ConsultString(
            ":- op(700, xfx, glt).\n" +
            ":- op(700, xfx, ggt).\n" +
            "goal_expansion(A glt B, lt(A, B)).\n" +
            "lt(X, Y) :- X < Y.\n" +                    // a non-hook clause between the two hook clauses
            "goal_expansion(A ggt B, B glt A).\n" +
            "gt(X, Y) :- Y glt X.");
        Assert.True(e.Query("gt(2, 1).").Success);
    }

    [Fact]
    public void InitializationGoal_RunsInItsOwnModuleContext()
    {
        // clpz's shape: `:- initialization` calls a module-local predicate. The
        // goal must run in the defining module's context so the local resolves
        // (before this it ran in `user`, so a module-local was existence_error).
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(genlib, []).
            :- initialization((gen(C), assertz(C))).
            gen(fact(generated)).
            """);
        Assert.True(e.Query("fact(generated).").Success);
    }

    [Fact]
    public void PromotedHook_EvictedWhenALaterConsultAddsHookClauses()
    {
        // The dcgs→atts silent failure: term_expansion/2 promoted to Tier-1 IL
        // (here: forced by calling it between consults, threshold 2, sync
        // compile), then a LATER consult appends its own hook clause to the same
        // global predicate. The consult commit must evict the promoted delegate,
        // or the stale IL silently hides the new clause.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 2;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString("term_expansion(first(X), got_first(X)).");
        // Cross the threshold OUTSIDE any consult so the promotion installs.
        for (int i = 0; i < 4; i++) e.Query("\\+ term_expansion(nomatch, _).");
        // A later consult adds a second hook clause AND uses it.
        e.ConsultString("""
            term_expansion(second(X), got_second(X)).
            second(a).
            """);
        Assert.True(e.Query("got_second(a).").Success);
    }

    [Fact]
    public void NoNewPromotionsDuringConsult_HookStaysLiveAcrossLibraries()
    {
        // Promotions are suspended while program text loads: a first library
        // whose consult calls term_expansion once per clause (crossing the
        // threshold) must NOT freeze the hook predicate mid-load — a second
        // library's hook clauses, added later in the same load, must fire.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-promo-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var lib1 = new System.Text.StringBuilder(
                ":- module(promolib1, []).\n" +
                "user:term_expansion(marker1(X), got1(X)).\n");
            for (int i = 0; i < 12; i++) lib1.Append($"filler{i}(x).\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "promolib1.pl"),
                lib1.ToString());
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "promolib2.pl"),
                ":- module(promolib2, []).\n" +
                ":- use_module(library(promolib1)).\n" +
                "user:term_expansion(marker2(X), got2(X)).\n");
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 2;
            e.IlPromotion.BackgroundCompilation = false;
            e.AddLibraryDirectory(dir);
            e.ConsultString("""
                :- use_module(library(promolib2)).
                marker2(b).
                """);
            Assert.True(e.Query("got2(b).").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void BodyUnificationHook_StillFires_UnderTheDiscriminatorIndex()
    {
        // The Scryer hook idiom: a var head narrowed by a body unification.
        // The discriminator index extracts `special/1` from `T0 = special(A)`
        // and must still RUN the hook for a matching term (and keep, unharmed,
        // terms the index skips — the numeric fact and other(b)).
        var e = new PrologEngine();
        e.ConsultString("""
            term_expansion(T0, done(A)) :- nonvar(T0), T0 = special(A).
            special(a).
            other(b).
            num(42).
            """);
        Assert.True(e.Query("done(a).").Success);       // matched + expanded
        // Expanded away entirely: special/1 no longer exists (a bare call
        // would raise existence_error under the default unknown=error).
        Assert.False(e.Query("current_predicate(special/1).").Success);
        Assert.True(e.Query("other(b).").Success);      // skipped by index, kept
        Assert.True(e.Query("num(42).").Success);       // number arg, kept
    }

    [Fact]
    public void OpaqueHookClause_MakesTheFamilyAnyMatch_NothingIsWronglySkipped()
    {
        // One analyzable clause + one the analysis can't see through (its body
        // starts with an opaque call). The family must fall back to always-try:
        // the opaque hook still fires for the shape only IT accepts.
        var e = new PrologEngine();
        e.ConsultString("""
            term_expansion(T0, done1(A)) :- nonvar(T0), T0 = alpha(A).
            term_expansion(T0, done2(A)) :- match_beta(T0, A).
            match_beta(beta(A), A).
            alpha(x).
            beta(y).
            """);
        Assert.True(e.Query("done1(x).").Success);
        Assert.True(e.Query("done2(y).").Success);
    }

    [Fact]
    public void GoalExpansion_RewritesGoalsInsideDcgBraces()
    {
        // clpz's shape: an in-file goal_expansion macro (`cis_leq` -> a real
        // predicate) used inside DCG `{ }` braces. The re-expansion pass must
        // rewrite brace goals — the DCG rule itself is core-transformed later,
        // and skipping it wholesale left the macro as a runtime
        // existence_error (clpz$$disj/cis_leq).
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(700, xfx, mleq).
            goal_expansion(A mleq B, leq(A, B)).
            leq(X, Y) :- X =< Y.
            check(N) --> { N mleq 5 }, [ok].
            run(N, L) :- phrase(check(N), L).
            """);
        Assert.True(e.Query("run(3, [ok]).").Success);
        Assert.False(e.Query("run(9, [ok]).").Success);
    }

    [Fact]
    public void GoalExpansion_NeverTouchesAVariableGoal()
    {
        // A VARIABLE goal is a runtime meta-call. A hook with an unguarded
        // head pattern (dcgs's `goal_expansion(phrase(B,S), phrase(B,S,[]))`
        // is a fact) must NOT unify its pattern INTO the variable — that
        // replaced clpz's `( Repeat -> ... )` condition with an orphaned
        // phrase/3 and destroyed the goal.
        var e = new PrologEngine();
        e.ConsultString("""
            goal_expansion(phr(B, S), phr(B, S, [])).
            phr(_, _, _).
            t(Cond, R) :- ( Cond -> R = yes ; R = no ).
            """);
        Assert.True(e.Query("t(true, R), R == yes.").Success);
        Assert.True(e.Query("t(fail, R), R == no.").Success);
    }

    [Fact]
    public void GoalExpansion_HookFreshVariables_GetUniqueNames()
    {
        // Hook-introduced variables must not capture same-named variables
        // elsewhere in the clause (heap-address _G names repeat across
        // materialisations): each expansion's fresh vars are uniquified.
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(700, xfx, gets).
            goal_expansion(X gets E, (T = E, X = f(T))).
            use(A, B, Out) :- A gets one, B gets two, Out = A-B.
            """);
        Assert.True(e.Query("use(A, B, Out), Out == f(one)-f(two).").Success);
    }

    [Fact]
    public void OpDirective_StillAppliesToLaterClauses()
    {
        // The baseline the others generalise — a plain `:- op` from that point on.
        var e = new PrologEngine();
        e.ConsultString("""
            :- op(700, xfx, ===>).
            rule(a ===> b).
            """);
        Assert.True(e.Query("rule(a ===> b).").Success);
    }

    private static string RenderList(Shumway.Compiler.Ast.Term t)
    {
        var sb = new System.Text.StringBuilder("[");
        bool first = true;
        Shumway.Compiler.Ast.Term cur = t;
        while (cur is Shumway.Compiler.Ast.CompoundTerm { Functor: ".", Args: [var h, var tl] })
        {
            if (!first) sb.Append(',');
            sb.Append((h as Shumway.Compiler.Ast.AtomTerm)?.Name ?? h.ToString());
            first = false;
            cur = tl;
        }
        return sb.Append(']').ToString();
    }
}
