using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-038 (amendment) point 7: a call a module's compile could not
/// place resolves when it runs. What that costs, counted rather than timed:
/// which dispatches go through the run-time resolution, in which query, and
/// what answers them. A predicate asserted into the module from outside, in
/// the query that calls it, is resolved through the address map on every
/// interpreter dispatch of that query and linked directly by the next setup.
/// A literal <c>assertz(p(..))</c> in the module's own text makes <c>p</c> a
/// dynamic at consult (the <c>implicit_dynamic</c> scan), so its sites link
/// at setup and pay nothing. A name a later load makes public is answered
/// by the global view until the module is transformed again.</summary>
public sealed class ModuleDynamicsCostTests
{
    private const string Pm = """
        :- module(pmc, [loop_u/1, bench_literal/1, bench_declared/1, call_later/1]).
        loop_u(0) :- !.
        loop_u(N) :- ( dyn_u(_) -> true ; true ), N1 is N - 1, loop_u(N1).
        bench_literal(N) :-
            ( catch(dyn_l(_), _, fail) -> true ; assertz(dyn_l(0)) ),
            loop_l(N).
        loop_l(0) :- !.
        loop_l(N) :- ( dyn_l(_) -> true ; true ), N1 is N - 1, loop_l(N1).
        :- dynamic dyn_d/1.
        bench_declared(N) :-
            ( dyn_d(_) -> true ; assertz(dyn_d(0)) ),
            loop_d(N).
        loop_d(0) :- !.
        loop_d(N) :- ( dyn_d(_) -> true ; true ), N1 is N - 1, loop_d(N1).
        call_later(X) :- later(X).
        """;

    private static void Reset()
    {
        Activation.DiagUnresolvedSiteDispatches = 0;
        Activation.DiagModuleFallbackResolutions = 0;
    }

    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    private static PrologEngine Engine(bool tier1)
    {
        var e = new PrologEngine();
        if (tier1) e.IlPromotion.Threshold = 1;
        e.ConsultString(Pm);
        if (!tier1) Succeeds(e, "jit_compile(0).");
        return e;
    }

    [DiagTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADynamicAssertedFromOutside_IsResolvedPerDispatchInThatQuery_AndLinkedByTheNext(bool tier1)
    {
        var e = Engine(tier1);
        // The call is a sentinel at the link; the assert materializes the
        // trampoline mid-query, and every interpreter dispatch of the loop
        // finds it through the address map. Tier-1 takes the loop over at
        // some point, after which its own dispatch resolves by functor.
        Reset();
        Succeeds(e, "assertz(pmc:dyn_u(0)), loop_u(1000).");
        if (tier1)
            Assert.InRange(Activation.DiagUnresolvedSiteDispatches, 1, 1000);
        else
            Assert.Equal(1000, Activation.DiagUnresolvedSiteDispatches);
        Assert.Equal(0, Activation.DiagModuleFallbackResolutions);

        // The next query's setup linked the site: nothing is resolved at run time.
        Reset();
        Succeeds(e, "loop_u(1000).");
        Assert.Equal(0, Activation.DiagUnresolvedSiteDispatches);
        Assert.Equal(0, Activation.DiagModuleFallbackResolutions);
    }

    /// <summary>The Tier-1 side, after the setup linked the site. A loop
    /// promoted in the query whose assert created its callee keeps one
    /// interpreter dispatch per call for the rest of the process, while a
    /// loop promoted over a declared dynamic, or in a later query, runs
    /// entirely in IL; that predates the amendment and holds in the global
    /// module alike. What the amendment owes is that the module's case costs
    /// exactly what the global module's does: both callees promoted, no
    /// run-time resolution left, and the interpreter's share of each loop
    /// the same in the two worlds.</summary>
    [DiagFact]
    public void OnTier1_TheModulesCase_CostsWhatTheGlobalModulesDoes()
    {
        var m = Engine(tier1: true);
        Succeeds(m, "assertz(pmc:dyn_u(0)), loop_u(1000), bench_declared(1000).");
        var u = new PrologEngine();
        u.IlPromotion.Threshold = 1;
        u.ConsultString("""
            loop_x(0) :- !.
            loop_x(N) :- ( dyn_x(_) -> true ; true ), N1 is N - 1, loop_x(N1).
            :- dynamic dyn_y/1.
            loop_y(0) :- !.
            loop_y(N) :- ( dyn_y(_) -> true ; true ), N1 is N - 1, loop_y(N1).
            """);
        Succeeds(u, "assertz(dyn_x(0)), loop_x(1000), assertz(dyn_y(0)), loop_y(1000).");
        Assert.True(m.IlPromotion.WaitForPendingPromotions(60_000));
        Assert.True(u.IlPromotion.WaitForPendingPromotions(60_000));
        for (int q = 2; q <= 4; q++)
        {
            Reset();
            var sm = m.Query("statistics(inferences, I0), loop_u(1000), statistics(inferences, I1), "
                + "bench_declared(1000), statistics(inferences, I2), Iu is I1 - I0, Id is I2 - I1.");
            var su = u.Query("statistics(inferences, I0), loop_x(1000), statistics(inferences, I1), "
                + "loop_y(1000), statistics(inferences, I2), Ix is I1 - I0, Iy is I2 - I1.");
            Assert.True(sm.Success && su.Success);
            Assert.True(m.IlPromotion.WaitForPendingPromotions(60_000));
            Assert.True(u.IlPromotion.WaitForPendingPromotions(60_000));
            int fu = FunctorTable.Intern(AtomTable.Intern("pmc$dyn_u").Id, 1);
            int fd = FunctorTable.Intern(AtomTable.Intern("pmc$dyn_d").Id, 1);
            _out.WriteLine($"q{q}: interpreter inferences, module loop_u={sm["Iu"]} loop_d={sm["Id"]}, "
                + $"global loop_x={su["Ix"]} loop_y={su["Iy"]}");
            Assert.Equal(0, Activation.DiagUnresolvedSiteDispatches);
            Assert.Equal(0, Activation.DiagModuleFallbackResolutions);
            Assert.True(m.IlPromotion.IsPromoted(fu), "the module's asserted dynamic did not promote");
            Assert.True(m.IlPromotion.IsPromoted(fd), "the module's declared dynamic did not promote");
            Assert.Equal(su["Ix"]!.ToString(), sm["Iu"]!.ToString());
            Assert.Equal(su["Iy"]!.ToString(), sm["Id"]!.ToString());
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ModuleDynamicsCostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [DiagTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALiteralAssertInTheModule_AndADeclaredDynamic_PayNothing(bool tier1)
    {
        var e = Engine(tier1);
        Reset();
        Succeeds(e, "bench_literal(1000).");
        Succeeds(e, "bench_declared(1000).");
        Assert.Equal(0, Activation.DiagUnresolvedSiteDispatches);
        Assert.Equal(0, Activation.DiagModuleFallbackResolutions);
    }

    [DiagFact]
    public void AGlobalMadePublicLater_IsAnsweredByTheGlobalView_UntilTheModuleIsTransformedAgain()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway_pmc_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string legacy = System.IO.Path.Combine(dir, "pmc_leg.pl").Replace('\\', '/');
            System.IO.File.WriteAllText(legacy, """
                :- module(pmc_leg).
                :- public later/1.
                later(1).
                later(2).
                later(3).
                """);
            var e = new PrologEngine();
            Succeeds(e, "jit_compile(0).");
            e.ConsultString(Pm);
            // Compiled before later/1 existed, the call is the module's own.
            Succeeds(e, "catch(call_later(_), error(existence_error(procedure, later/1), _), true).");
            // Loaded inside the query, the public is reached through the
            // global view: one dispatch of the site, answered there.
            Reset();
            Succeeds(e, $"consult('{legacy}'), findall(X, call_later(X), L), L == [1, 2, 3].");
            Assert.Equal(1, Activation.DiagModuleFallbackResolutions);
            // The next setup saw the name become public and transformed the
            // module again: the call links bare.
            Reset();
            Succeeds(e, "findall(X, call_later(X), L), L == [1, 2, 3].");
            Assert.Equal(0, Activation.DiagModuleFallbackResolutions);
            Assert.Equal(0, Activation.DiagUnresolvedSiteDispatches);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }
}
