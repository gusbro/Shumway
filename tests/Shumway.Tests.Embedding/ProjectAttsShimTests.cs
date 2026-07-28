using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The Scryer bootstrap-library shims: <c>library('$project_atts')</c> — our
/// implementation of <c>term_residual_goals/2</c> over the engine's attvar
/// machinery — and <c>library(loader)</c> (no-op; strip_module/3 is in the
/// prelude). Plus the <c>M:G</c> resolution-precedence fix they surfaced: a
/// module-qualified call must reach the module's OWN version of a
/// builtin-named predicate (Scryer's iso_ext defines copy_term/3) before the
/// engine builtin.
/// </summary>
public class ProjectAttsShimTests
{
    // ---- library resolution (no warnings, no errors) ----

    [Fact]
    public void ProjectAttsAndLoader_ResolveAsLibraries()
    {
        var e = new PrologEngine();
        // The goal-form use_module raises existence_error(library, _) for an
        // unknown name — both names must be known.
        Assert.True(e.Query("use_module(library('$project_atts')).").Success);
        Assert.True(e.Query("use_module(library(loader)).").Success);
    }

    // ---- term_residual_goals/2: raw fallback (no attribute_goals hook) ----

    [Fact]
    public void TermResidualGoals_RawAttributes_EmitPutAttsGoals()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library('$project_atts')).");
        var sol = e.Query(
            "put_atts(V, mymod, color(red)), term_residual_goals(f(V), Gs), "
            + "Gs = [put_atts(W, mymod, color(red))], W == V.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void TermResidualGoals_PlainTerm_EmitsNothing()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library('$project_atts')).");
        Assert.True(e.Query("term_residual_goals(f(a, _), []).").Success);
    }

    // ---- term_residual_goals/2: the attribute_goals//1 projection hook ----

    [Fact]
    public void TermResidualGoals_ModuleHook_ProjectsThroughAttributeGoals()
    {
        // The Scryer/SICStus convention: the module owning the attribute
        // defines attribute_goals//1; projection prefers it over raw dumping.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-attg-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "myfrz.pl"),
                ":- module(myfrz, [susp/2]).\n" +
                "susp(V, G) :- put_atts(V, myfrz, held(G)).\n" +
                "attribute_goals(V) --> { get_atts(V, myfrz, held(G)) }, [myfrz:susp(V, G)].\n");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            e.ConsultString(
                ":- use_module(library('$project_atts')).\n" +
                ":- use_module(library(myfrz)).");
            var sol = e.Query(
                "susp(X, wake), term_residual_goals(g(X), Gs), "
                + "Gs = [myfrz:susp(Y, wake)], Y == X.");
            Assert.True(sol.Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- M:G precedence: module-local beats builtin ----

    private static PrologEngine BuiltinShadowEngine(out string dir)
    {
        dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-shadow-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "shadow.pl"),
            ":- module(shadow, [copy_term/3, runct/1]).\n" +
            "copy_term(_, _, marker).\n" +          // shadows the /3 builtin inside the module
            "runct(R) :- copy_term(a, b, R).\n");   // internal call resolves the local
        var e = new PrologEngine();
        e.AddLibraryDirectory(dir);
        e.ConsultString(":- use_module(library(shadow)).");
        return e;
    }

    [Fact]
    public void ModuleQualifiedCall_ReachesTheModulesBuiltinNamedLocal()
    {
        var e = BuiltinShadowEngine(out string dir);
        try
        {
            // Direct M:G body-goal form (the prelude ':'/2 must preserve the
            // qualification — it used to discard the module and call bare).
            Assert.True(e.Query("shadow:copy_term(x, y, R), R == marker.").Success);
            // Runtime meta-call form (DispatchCall's Colon unwrap; the mangled
            // local must be tried BEFORE the builtin).
            Assert.True(e.Query("G = shadow:copy_term(x, y, R), call(G), R == marker.").Success);
            // Internal call (compile-time ModuleRewrite local resolution).
            Assert.True(e.Query("runct(R), R == marker.").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ImportersBareCall_GetsTheImportedVersion()
    {
        var e = BuiltinShadowEngine(out string dir);
        try
        {
            // The importer ASKED for shadow's exports: its bare copy_term/3 is
            // the imported one (import table before builtin) — exactly why a
            // Scryer program importing iso_ext gets ISO_EXT's copy_term/3.
            Assert.True(e.Query("copy_term(x, y, R), R == marker.").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void NonImportersBareCall_StillRunsTheEngineBuiltin()
    {
        // An engine that never imported the shadowing module keeps the builtin.
        var e = new PrologEngine();
        Assert.True(e.Query("copy_term(f(A, A), Copy, _), Copy = f(B, C), B == C.").Success);
    }

    [Fact]
    public void QualifiedCallToUndefinedLocal_FallsThroughToBuiltin()
    {
        var e = BuiltinShadowEngine(out string dir);
        try
        {
            // shadow does NOT define length/2 — M:length falls through the
            // module-local and import steps to the builtin.
            Assert.True(e.Query("shadow:length([a, b], N), N == 2.").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }
}
