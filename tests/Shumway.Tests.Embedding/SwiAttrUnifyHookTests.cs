using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>SWI's attribute hook, <c>attr_unify_hook/2</c>, through the SWI
/// compat shim: the engine's own hook is <c>verify_attributes/4</c> (ADR-040),
/// and the shim gives a module that defines <c>attr_unify_hook/2</c> a
/// <c>verify_attributes/4</c> whose goal calls it after the binding, as SWI
/// does. Without the shim the engine does not know the name.</summary>
public sealed class SwiAttrUnifyHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway_swi_auh_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool Holds(PrologEngine e, string goal) => e.Query(goal).Success;

    // A domain: binding the variable to a value outside it fails.
    private const string Qualified = """
        dom:attr_unify_hook(Domain, Y) :-
            (   var(Y) -> true
            ;   memberchk(Y, Domain)
            ).
        """;

    // An SWI-style module with two clauses for its hook, the second after the
    // first: the shim's verify_attributes/4 must not split them.
    private const string Module = """
        :- module(swi_dom, [domain/2]).
        domain(X, Dom) :- put_attr(X, swi_dom, Dom).
        attr_unify_hook(Dom, Y) :-
            var(Y), !,
            (   get_attr(Y, swi_dom, Dom2)
            ->  intersection(Dom, Dom2, Dom3), Dom3 \== [], put_attr(Y, swi_dom, Dom3)
            ;   put_attr(Y, swi_dom, Dom)
            ).
        attr_unify_hook(Dom, Y) :-
            memberchk(Y, Dom).
        """;

    [Fact]
    public void WithoutTheShim_TheEngineDoesNotCallIt()
    {
        var e = new PrologEngine();
        e.ConsultString(Qualified);
        // The binding happens: no verify_attributes/4 hook, nothing runs.
        Assert.True(Holds(e, "put_attr(X, dom, [a, b]), X = c."));
    }

    [Fact]
    public void WithTheShim_AQualifiedHookRunsAfterTheBinding()
    {
        var e = new PrologEngine();
        Assert.True(Holds(e, "use_module(library(swi))."));
        e.ConsultString(Qualified);
        Assert.True(Holds(e, "put_attr(X, dom, [a, b]), X = a."));
        Assert.False(Holds(e, "put_attr(X, dom, [a, b]), X = c."));
    }

    [Fact]
    public void WithTheShim_AModulesHookOfSeveralClausesRuns()
    {
        var e = new PrologEngine();
        Assert.True(Holds(e, "use_module(library(swi))."));
        e.ConsultString(Module);
        Assert.True(Holds(e, "swi_dom:domain(X, [a, b]), X = b."));
        Assert.False(Holds(e, "swi_dom:domain(X, [a, b]), X = c."));
        // Two attributed variables: the hook sees the other variable.
        Assert.True(Holds(e,
            "swi_dom:domain(X, [a, b, c]), swi_dom:domain(Y, [b, c, d]), X = Y, get_attr(Y, swi_dom, D), D == [b, c]."));
        Assert.False(Holds(e, "swi_dom:domain(X, [a]), swi_dom:domain(Y, [b]), X = Y."));
    }

    [Fact]
    public void Reloaded_TheModuleKeepsItsHook()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "swi_dom.pl");
        File.WriteAllText(path, Module);
        var e = new PrologEngine();
        Assert.True(Holds(e, "use_module(library(swi))."));
        e.ConsultFile(path);
        e.ConsultFile(path);
        Assert.False(Holds(e, "swi_dom:domain(X, [a, b]), X = c."));
        Assert.True(Holds(e, "swi_dom:domain(X, [a, b]), X = a."));
    }

    [Fact]
    public void AnSwiDialectLibrary_LoadsTheShimAndItsHookRuns()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "swi_dom.pl"), Module);
        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir, "swi");
        e.ConsultString(":- use_module(library(swi_dom)).");
        Assert.True(Holds(e, "domain(X, [a, b]), X = a."));
        Assert.False(Holds(e, "domain(X, [a, b]), X = c."));
    }
}
